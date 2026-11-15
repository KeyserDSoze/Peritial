using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;

namespace Peritial.Infrastructures;

/// <summary>
/// Copy an already encrypted PostgreSQL+S3 bundle to a dedicated remote S3
/// store, never the plaintext PostgreSQL or document archives. No Object Lock
/// or geographic/offsite guarantee: infrastructure must supply those.
/// </summary>
public static class SealedBundleOffsite
{
    private sealed record FileEntry(string Name, long Length, string Sha256);
    private sealed record Inventory(int Version, IReadOnlyList<FileEntry> Files);

    private const string InventoryName = "transfer.json";
    private const string SignatureName = "transfer.hmac";
    private const string CompletedName = "complete";
    private const int MaxManifestBytes = 50_000_000;

    public static async Task UploadAsync(
        string sealedDirectory, IAmazonS3 destination, string bucket,
        string backupId, byte[] encryptionKey, CancellationToken ct = default,
        bool disposableUnprotectedDemo = false)
    {
        Validate(encryptionKey, bucket, backupId);
        // Authenticate every GCM tag in memory, with no temporary plaintext.
        var files = await SealedRecoveryBundle.ValidateEncryptedAsync(
            sealedDirectory, encryptionKey, ct);
        // Production is fail-closed. The only opt-out is explicitly selected
        // by disposable local tests; CLI restricts it to a fixed CI host.
        if (!disposableUnprotectedDemo)
            await OffsiteObjectLockPolicy.RequireCompliantBucketAsync(
                destination, bucket, ct);
        var prefix = Prefix(backupId);
        if ((await destination.ListObjectsV2Async(new ListObjectsV2Request
            { BucketName = bucket, Prefix = prefix, MaxKeys = 1 }, ct))
            .S3Objects is { Count: > 0 })
            throw new InvalidOperationException("Backup prefix already contains remote objects.");

        var entries = new List<FileEntry>(files.Count);
        foreach (var name in files)
        {
            var path = Path.Combine(sealedDirectory,
                name.Replace('/', Path.DirectorySeparatorChar));
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, ct);
            entries.Add(new FileEntry(name, stream.Length,
                Convert.ToHexString(hash).ToLowerInvariant()));
        }

        var inventory = JsonSerializer.SerializeToUtf8Bytes(new Inventory(1, entries));
        if (inventory.Length == 0 || inventory.Length > MaxManifestBytes)
            throw new InvalidOperationException("Encrypted inventory exceeds maximum length.");
        var signature = Sign(encryptionKey, backupId, inventory);

        // Fail closed on any 412/preexisting key: never overwrite a sealed
        // backup. The sole completion marker is written LAST.
        foreach (var entry in entries)
        {
            var path = Path.Combine(sealedDirectory,
                entry.Name.Replace('/', Path.DirectorySeparatorChar));
            await using var stream = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            var started = DateTime.UtcNow;
            var write = await destination.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = prefix + entry.Name,
                InputStream = stream, AutoCloseStream = false,
                ContentType = "application/octet-stream",
                IfNoneMatch = "*"
            }, ct);
            if (!disposableUnprotectedDemo)
                await OffsiteObjectLockPolicy.RequireProtectedObjectVersionAsync(
                    destination, bucket, prefix + entry.Name, write.VersionId, started, ct);
            var received = await HashRemoteAsync(destination, bucket,
                prefix + entry.Name, ct);
            if (received.Length != entry.Length || received.Sha256 != entry.Sha256)
                throw new InvalidOperationException(
                    "Remote ciphertext differs after upload; backup remains incomplete.");
        }
        var inventoryStarted = DateTime.UtcNow;
        var inventoryVersion = await PutBytesAsync(
            destination, bucket, prefix + InventoryName, inventory, ct);
        if (!disposableUnprotectedDemo)
            await OffsiteObjectLockPolicy.RequireProtectedObjectVersionAsync(
                destination, bucket, prefix + InventoryName, inventoryVersion,
                inventoryStarted, ct);
        var signatureStarted = DateTime.UtcNow;
        var signatureVersion = await PutBytesAsync(
            destination, bucket, prefix + SignatureName, signature, ct);
        if (!disposableUnprotectedDemo)
            await OffsiteObjectLockPolicy.RequireProtectedObjectVersionAsync(
                destination, bucket, prefix + SignatureName, signatureVersion,
                signatureStarted, ct);
        var expectedComplete = CompletionDigest(inventory, signature);
        var remoteInventory = await GetBytesAsync(destination, bucket,
            prefix + InventoryName, MaxManifestBytes, ct);
        var remoteSignature = await GetBytesAsync(destination, bucket,
            prefix + SignatureName, 32, ct);
        if (!CryptographicOperations.FixedTimeEquals(inventory, remoteInventory) ||
            !CryptographicOperations.FixedTimeEquals(signature, remoteSignature))
            throw new InvalidOperationException("Remote inventory was changed before completion.");
        var completionStarted = DateTime.UtcNow;
        var completionVersion = await PutBytesAsync(
            destination, bucket, prefix + CompletedName, expectedComplete, ct);
        if (!disposableUnprotectedDemo)
            await OffsiteObjectLockPolicy.RequireProtectedObjectVersionAsync(
                destination, bucket, prefix + CompletedName, completionVersion,
                completionStarted, ct);
    }

    public static async Task DownloadAsync(
        IAmazonS3 source, string bucket, string backupId,
        string destinationDirectory, byte[] encryptionKey, CancellationToken ct = default)
    {
        Validate(encryptionKey, bucket, backupId);
        if (!Path.IsPathFullyQualified(destinationDirectory) ||
            !Directory.Exists(Path.GetDirectoryName(destinationDirectory)) ||
            Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
            throw new InvalidOperationException(
                "Downloaded encrypted bundle must have a new absolute destination.");

        var prefix = Prefix(backupId);
        // No completion marker means a partial/crashed transfer, not a backup.
        var completed = await GetBytesAsync(source, bucket,
            prefix + CompletedName, 32, ct);
        var raw = await GetBytesAsync(source, bucket,
            prefix + InventoryName, MaxManifestBytes, ct);
        var signed = await GetBytesAsync(source, bucket,
            prefix + SignatureName, 32, ct);
        if (completed.Length != 32 || signed.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(
                completed, CompletionDigest(raw, signed)) ||
            !CryptographicOperations.FixedTimeEquals(signed, Sign(encryptionKey, backupId, raw)))
            throw new InvalidOperationException("Remote encrypted inventory authentication failed.");

        Inventory? inventory;
        try { inventory = JsonSerializer.Deserialize<Inventory>(raw); }
        catch (JsonException) { throw new InvalidOperationException("Remote inventory malformed."); }
        ValidateEntries(inventory);

        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            prefix + InventoryName, prefix + SignatureName, prefix + CompletedName
        };
        foreach (var item in inventory!.Files)
            expected.Add(prefix + item.Name);
        // Detect unexpected objects, duplicate or stale keys in the snapshot.
        string? continuation = null;
        do
        {
            var page = await source.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket, Prefix = prefix, ContinuationToken = continuation
            }, ct);
            foreach (var item in page.S3Objects ?? [])
                if (!expected.Remove(item.Key))
                    throw new InvalidOperationException(
                        "Remote backup prefix contains unexpected or repeated objects.");
            continuation = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (continuation is not null);
        if (expected.Count != 0)
            throw new InvalidOperationException("Remote encrypted backup is missing objects.");

        var stage = destinationDirectory + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            CreatePrivateDirectory(stage);
            CreatePrivateDirectory(Path.Combine(stage, "payload"));
            foreach (var entry in inventory.Files)
            {
                var local = Path.Combine(stage,
                    entry.Name.Replace('/', Path.DirectorySeparatorChar));
                var result = await CopyRemoteToFileAsync(source, bucket,
                    prefix + entry.Name, local, ct);
                if (result.Length != entry.Length || result.Sha256 != entry.Sha256)
                    throw new InvalidOperationException(
                        "Downloaded encrypted bundle file failed authenticated SHA-256 verification.");
            }
            // Re-check all AES-GCM tags and the original sealed index MAC
            // before publishing the encrypted archive locally.
            await SealedRecoveryBundle.ValidateEncryptedAsync(stage, encryptionKey, ct);
            if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
                throw new InvalidOperationException("Downloaded backup destination already exists.");
            Directory.Move(stage, destinationDirectory);
        }
        finally
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
        }
    }

    private static void Validate(byte[] key, string bucket, string id)
    {
        if (key is not { Length: 32 } || string.IsNullOrWhiteSpace(bucket) ||
            id is null || id.Length != 32 ||
            !id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidOperationException(
                "Remote encrypted backup requires a valid key, bucket and unique opaque backup ID.");
    }

    private static string Prefix(string id) => "sealed-v1/" + id + "/";

    private static byte[] Sign(byte[] key, string backupId, byte[] raw)
    {
        var derived = HMACSHA256.HashData(key,
            "Peritial.SealedBundle.OffsiteTransfer.HMACSHA256.v1"u8);
        try
        {
            using var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, derived);
            // The backup identity is authenticated as part of the protocol,
            // preventing re-labeling old ciphertext under a fresh backup ID.
            // Backup IDs are fixed-length 32-character ASCII hex strings.
            mac.AppendData(System.Text.Encoding.ASCII.GetBytes(backupId));
            mac.AppendData(raw);
            return mac.GetHashAndReset();
        }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    private static byte[] CompletionDigest(byte[] raw, byte[] signature)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(raw);
        hash.AppendData(signature);
        return hash.GetHashAndReset();
    }

    private static void ValidateEntries(Inventory? inventory)
    {
        if (inventory is null || inventory.Version != 1 ||
            inventory.Files is null || inventory.Files.Count < 6 ||
            inventory.Files.Count > 100_002)
            throw new InvalidOperationException("Remote encrypted inventory invalid.");
        for (int i = 0; i < inventory.Files.Count; i++)
        {
            var item = inventory.Files[i];
            var expectedName = i switch
            {
                0 => "index.json",
                1 => "index.hmac",
                _ => "payload/" + (i - 2).ToString("D8",
                        System.Globalization.CultureInfo.InvariantCulture) + ".enc"
            };
            if (item is null || item.Name != expectedName ||
                item.Length <= 0 || item.Sha256 is not { Length: 64 } ||
                !item.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new InvalidOperationException(
                    "Remote encrypted inventory has unsafe file entries.");
        }
    }

    private static async Task<string?> PutBytesAsync(IAmazonS3 s3, string bucket,
        string key, byte[] data, CancellationToken ct)
    {
        using var stream = new MemoryStream(data, writable: false);
        var response = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = key, InputStream = stream,
            AutoCloseStream = false, ContentType = "application/octet-stream",
            IfNoneMatch = "*"
        }, ct);
        return response.VersionId;
    }

    private static async Task<byte[]> GetBytesAsync(IAmazonS3 s3, string bucket,
        string key, int limit, CancellationToken ct)
    {
        using var response = await ReadRemoteAsync(s3, bucket, key, ct);
        await using var memory = new MemoryStream();
        var buffer = new byte[65536];
        int n;
        while ((n = await response.ResponseStream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + n > limit)
                throw new InvalidOperationException("Remote inventory or completion marker too large.");
            await memory.WriteAsync(buffer.AsMemory(0, n), ct);
        }
        return memory.ToArray();
    }

    private static async Task<(long Length, string Sha256)> HashRemoteAsync(
        IAmazonS3 s3, string bucket, string key, CancellationToken ct)
    {
        using var remote = await ReadRemoteAsync(s3, bucket, key, ct);
        return await CopyHashAsync(remote.ResponseStream, Stream.Null, ct);
    }

    private static async Task<(long Length, string Sha256)> CopyRemoteToFileAsync(
        IAmazonS3 s3, string bucket, string key, string file, CancellationToken ct)
    {
        using var remote = await ReadRemoteAsync(s3, bucket, key, ct);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var output = new FileStream(file, options);
        return await CopyHashAsync(remote.ResponseStream, output, ct);
    }

    private static async Task<(long Length, string Sha256)> CopyHashAsync(
        Stream input, Stream output, CancellationToken ct)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long count = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            count = checked(count + n);
            digest.AppendData(buffer, 0, n);
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
        }
        return (count, Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<GetObjectResponse> ReadRemoteAsync(
        IAmazonS3 s3, string bucket, string key, CancellationToken ct)
    {
        try
        {
            return await s3.GetObjectAsync(new GetObjectRequest
            { BucketName = bucket, Key = key }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "Remote encrypted backup missing an expected object or completion marker.");
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            Directory.CreateDirectory(path, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
            Directory.CreateDirectory(path);
    }
}
