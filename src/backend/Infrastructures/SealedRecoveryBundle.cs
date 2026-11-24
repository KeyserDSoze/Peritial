using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Peritial.Infrastructures;

/// <summary>
/// Manual, offline AES-256-GCM seal of the PostgreSQL PGDMP archive and the
/// already HMAC-authenticated S3 object archive. The two source archives MUST
/// represent the same stopped-writer recovery point. No database or S3 writes.
/// </summary>
public static partial class SealedRecoveryBundle
{
    private const int ChunkBytes = 1024 * 1024;
    private const int TagBytes = 16;
    private const int MaxFiles = 100_000;
    private const int MaxIndexBytes = 50_000_000;
    private const string IndexName = "index.json";
    private const string IndexMacName = "index.hmac";
    private const string PayloadDirectory = "payload";

    private sealed record BundleFile(string Path, long Length);
    private sealed record BundleIndex(int Version, string Salt, IReadOnlyList<BundleFile> Files);

    /// <summary>
    /// Encrypt and authenticate ALL material, including PostgreSQL digest and
    /// the S3 manifest/HMAC, as separate chunked ciphertexts. Source files
    /// remain on disk and must be deleted via the operator's retention policy.
    /// </summary>
    public static async Task SealAsync(string postgresDump, string s3Archive,
        string destination, byte[] encryptionKey, byte[] s3SigningKey,
        CancellationToken ct = default,
        long maxPlaintextBytes = SealedRecoveryBudget.MaxPlaintextBytes)
    {
        SealedRecoveryBudget.RequireLimit(maxPlaintextBytes);
        ValidateKeys(encryptionKey, s3SigningKey);
        // Reject plaintext PGDMP or S3 archives on disk-backed staging
        // BEFORE reading them or starting the encrypted publication.
        SecureRecoveryStaging.RequirePrivateTmpfsFile(postgresDump);
        SecureRecoveryStaging.RequirePrivateTmpfsFile(postgresDump + ".sha256");
        SecureRecoveryStaging.RequirePrivateTmpfsDirectory(s3Archive);
        CheckNewDirectory(destination);
        // Reject an oversized dump before hashing or reading any blob.
        SealedRecoveryBudget.RequirePlaintext(new[] { new FileInfo(postgresDump).Length },
            maxPlaintextBytes);
        var objects = await ObjectRecoveryArchive.ReadAndValidateAsync(
            s3Archive, s3SigningKey, ct);
        if (objects.Checkpoint.Documents.Count > MaxFiles - 4)
            throw new InvalidOperationException("Too many objects in recovery bundle.");

        var sources = new List<(string Relative, string Source)>
        {
            ("postgres/peritial.dump", postgresDump),
            ("postgres/peritial.dump.sha256", postgresDump + ".sha256"),
            ("s3/manifest.json", Path.Combine(s3Archive, "manifest.json")),
            ("s3/manifest.hmac", Path.Combine(s3Archive, "manifest.hmac"))
        };
        for (var i = 0; i < objects.Checkpoint.Documents.Count; i++)
        {
            var source = Path.Combine(s3Archive, "objects", ObjectName(i));
            CheckRegularFile(source);
            sources.Add(("s3/objects/" + ObjectName(i), source));
        }
        var entries = sources.Select(x =>
        {
            CheckRegularFile(x.Source);
            return new BundleFile(x.Relative, new FileInfo(x.Source).Length);
        }).ToArray();
        ValidateEntries(entries, maxPlaintextBytes);
        // Check the total capacity before hashing the PGDMP or S3 blobs.
        await VerifyPostgresAsync(postgresDump, ct);
        for (var i = 0; i < objects.Checkpoint.Documents.Count; i++)
        {
            var record = objects.Checkpoint.Documents[i];
            await using var stream = new FileStream(sources[i + 4].Source,
                FileMode.Open, FileAccess.Read, FileShare.Read);
            var (_, digest) = await ObjectRecoveryBoundedHash.CopyHashAsync(
                stream, Stream.Null, record.LengthBytes, ct);
            if (!string.Equals(digest, record.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Source S3 bytes no longer match signed archive manifest.");
        }
        var salt = RandomNumberGenerator.GetBytes(32);
        var index = new BundleIndex(1, Convert.ToBase64String(salt), entries);
        var indexBytes = JsonSerializer.SerializeToUtf8Bytes(index);
        if (indexBytes.Length > MaxIndexBytes)
            throw new InvalidOperationException("Recovery bundle index too large.");
        var mac = HMACSHA256.HashData(DeriveKey(encryptionKey, salt, -1), indexBytes);
        var digestIndex = SHA256.HashData(indexBytes);
        var stage = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            CreatePrivateDirectory(stage);
            var payload = Path.Combine(stage, PayloadDirectory);
            CreatePrivateDirectory(payload);
            for (var i = 0; i < sources.Count; i++)
                await EncryptFileAsync(sources[i].Source,
                    Path.Combine(payload, CipherName(i)), entries[i].Length,
                    DeriveKey(encryptionKey, salt, i), digestIndex, i, ct);
            await WritePrivateAsync(Path.Combine(stage, IndexName), indexBytes, ct);
            await WritePrivateAsync(Path.Combine(stage, IndexMacName), mac, ct);
            CheckNewDirectory(destination);
            Directory.Move(stage, destination);
        }
        finally
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    /// <summary>
    /// Refuse invalid MAC/ciphertext/structure before publishing plaintext.
    /// Decrypted material is placed in a new 0700 directory and requires
    /// the usual PostgreSQL restore and S3 import checks before activation.
    /// </summary>
    public static async Task UnsealAsync(string bundleDirectory, string destination,
        byte[] encryptionKey, byte[] s3SigningKey, CancellationToken ct = default,
        long maxPlaintextBytes = SealedRecoveryBudget.MaxPlaintextBytes)
    {
        SealedRecoveryBudget.RequireLimit(maxPlaintextBytes);
        ValidateKeys(encryptionKey, s3SigningKey);
        // The target will contain the full decrypted PostgreSQL database and
        // every document. Never publish it on overlay/ext4 by accident.
        SecureRecoveryStaging.RequirePrivateTmpfsParent(destination);
        CheckNewDirectory(destination);
        if (!Path.IsPathFullyQualified(bundleDirectory) ||
            !Directory.Exists(bundleDirectory) || IsLink(bundleDirectory))
            throw new InvalidOperationException("Sealed recovery bundle directory is missing or unsafe.");
        var indexPath = Path.Combine(bundleDirectory, IndexName);
        var macPath = Path.Combine(bundleDirectory, IndexMacName);
        var payloadPath = Path.Combine(bundleDirectory, PayloadDirectory);
        CheckRegularFile(indexPath);
        CheckRegularFile(macPath);
        if (!Directory.Exists(payloadPath) || IsLink(payloadPath) ||
            new FileInfo(indexPath).Length is <= 0 or > MaxIndexBytes ||
            new FileInfo(macPath).Length != 32 ||
            !ObjectRecoveryArchive.HasExactEntryCount(
                Directory.EnumerateFileSystemEntries(bundleDirectory), 3))
            throw new InvalidOperationException("Sealed recovery bundle structure is unsafe.");

        var indexBytes = await File.ReadAllBytesAsync(indexPath, ct);
        // The salt is public, but not trusted until MAC verification. It is
        // bounded and only used to derive an authenticated index key.
        BundleIndex? index;
        try
        {
            index = JsonSerializer.Deserialize<BundleIndex>(indexBytes);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Invalid sealed recovery bundle index.");
        }
        if (index is null || index.Version != 1 || index.Salt?.Length != 44)
            throw new InvalidOperationException("Unsupported recovery bundle index.");
        byte[] salt;
        try { salt = Convert.FromBase64String(index.Salt); }
        catch (FormatException) { throw new InvalidOperationException("Invalid bundle salt."); }
        if (salt.Length != 32)
            throw new InvalidOperationException("Invalid bundle salt.");
        var expectedMac = HMACSHA256.HashData(DeriveKey(encryptionKey, salt, -1), indexBytes);
        var actualMac = await File.ReadAllBytesAsync(macPath, ct);
        if (!CryptographicOperations.FixedTimeEquals(expectedMac, actualMac))
            throw new InvalidOperationException("Sealed recovery bundle authentication failed.");

        ValidateEntries(index.Files, maxPlaintextBytes);
        // The authenticated index supplies the exact logical output lengths.
        // Refuse insufficient actual tmpfs capacity before touching ciphertext.
        SecureRecoveryStaging.RequireCapacityForFiles(destination,
            index.Files.Select(file => file.Length));
        if (!ObjectRecoveryArchive.HasExactEntryCount(
                Directory.EnumerateFileSystemEntries(payloadPath), index.Files.Count))
            throw new InvalidOperationException("Unexpected ciphertext in sealed bundle.");
        for (var i = 0; i < index.Files.Count; i++)
        {
            var path = Path.Combine(payloadPath, CipherName(i));
            CheckRegularFile(path);
            long chunks = ChunkCount(index.Files[i].Length);
            if (new FileInfo(path).Length != checked(index.Files[i].Length + chunks * TagBytes))
                throw new InvalidOperationException("Sealed recovery payload length mismatch.");
        }

        var stage = destination + ".partial-" + Guid.NewGuid().ToString("N");
        var indexDigest = SHA256.HashData(indexBytes);
        try
        {
            CreatePrivateDirectory(stage);
            CreatePrivateDirectory(Path.Combine(stage, "postgres"));
            CreatePrivateDirectory(Path.Combine(stage, "s3"));
            CreatePrivateDirectory(Path.Combine(stage, "s3", "objects"));
            for (var i = 0; i < index.Files.Count; i++)
            {
                var outputPath = Path.Combine(stage,
                    index.Files[i].Path.Replace('/', Path.DirectorySeparatorChar));
                await DecryptFileAsync(
                    Path.Combine(payloadPath, CipherName(i)), outputPath,
                    index.Files[i].Length, DeriveKey(encryptionKey, salt, i),
                    indexDigest, i, ct);
            }

            // Authenticate both source-specific formats again before exposing
            // any decrypted contents outside the private staging directory.
            await VerifyPostgresAsync(Path.Combine(stage, "postgres", "peritial.dump"), ct);
            await ObjectRecoveryArchive.ReadAndValidateAsync(
                Path.Combine(stage, "s3"), s3SigningKey, ct);
            CheckNewDirectory(destination);
            Directory.Move(stage, destination);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Sealed recovery payload authentication failed.");
        }
        finally
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    private static void ValidateEntries(IReadOnlyList<BundleFile>? files, long maxPlaintextBytes)
    {
        if (files is null || files.Count < 4 || files.Count > MaxFiles)
            throw new InvalidOperationException("Invalid recovery bundle file count.");
        string[] required = ["postgres/peritial.dump", "postgres/peritial.dump.sha256",
                             "s3/manifest.json", "s3/manifest.hmac"];
        for (var i = 0; i < files.Count; i++)
        {
            var expected = i < required.Length
                ? required[i] : "s3/objects/" + ObjectName(i - required.Length);
            if (files[i] is null || files[i].Path != expected || files[i].Length < 0)
                throw new InvalidOperationException("Invalid recovery bundle file path or length.");
        }

        // Enforce the inner archive's existing default budgets before
        // decrypting any file or validating the ciphertext payload. The
        // authenticated outer index already contains these exact lengths.
        if (files[0].Length < 5 || files[1].Length is < 64 or > 128 ||
            files[2].Length is <= 0 or > MaxIndexBytes || files[3].Length != 32)
            throw new InvalidOperationException("Invalid recovery bundle metadata length.");
        ObjectRecoveryBudget.RequireWithin(
            files.Skip(4).Select(file => file.Length),
            ObjectRecoveryBudget.DefaultMaxTotalBytes,
            ObjectRecoveryBudget.DefaultMaxObjects);
        SealedRecoveryBudget.RequirePlaintext(files.Select(file => file.Length), maxPlaintextBytes);
    }

    private static async Task EncryptFileAsync(string source, string target, long length,
        byte[] key, byte[] indexDigest, int fileId, CancellationToken ct)
    {
        CheckRegularFile(source);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(target, PrivateWriteOptions());
        using var aes = new AesGcm(key, TagBytes);
        long processed = 0;
        for (long part = 0; part < ChunkCount(length); part++)
        {
            int n = (int)Math.Min(ChunkBytes, length - processed);
            var plain = new byte[n];
            await input.ReadExactlyAsync(plain.AsMemory(), ct);
            var encrypted = new byte[n];
            var tag = new byte[TagBytes];
            aes.Encrypt(Nonce(part), plain, encrypted, tag, AssociatedData(indexDigest, fileId, part));
            await output.WriteAsync(encrypted, ct);
            await output.WriteAsync(tag, ct);
            processed += n;
        }
        if (input.ReadByte() != -1 || processed != length)
            throw new InvalidOperationException("Source changed while sealing backup.");
        await output.FlushAsync(ct);
        CryptographicOperations.ZeroMemory(key);
    }

    private static async Task DecryptFileAsync(string source, string target, long length,
        byte[] key, byte[] indexDigest, int fileId, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(target, PrivateWriteOptions());
        using var aes = new AesGcm(key, TagBytes);
        long processed = 0;
        for (long part = 0; part < ChunkCount(length); part++)
        {
            int n = (int)Math.Min(ChunkBytes, length - processed);
            var encrypted = new byte[n];
            var tag = new byte[TagBytes];
            await input.ReadExactlyAsync(encrypted.AsMemory(), ct);
            await input.ReadExactlyAsync(tag.AsMemory(), ct);
            var plain = new byte[n];
            aes.Decrypt(Nonce(part), encrypted, tag, plain,
                AssociatedData(indexDigest, fileId, part));
            await output.WriteAsync(plain, ct);
            CryptographicOperations.ZeroMemory(plain);
            processed += n;
        }
        if (input.ReadByte() != -1 || processed != length)
            throw new InvalidOperationException("Unexpected extra encrypted bytes.");
        await output.FlushAsync(ct);
        CryptographicOperations.ZeroMemory(key);
    }

    private static byte[] AssociatedData(byte[] indexDigest, int fileId, long chunkId)
    {
        var aad = new byte[44];
        indexDigest.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(32, 4), fileId);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(36, 8), chunkId);
        return aad;
    }

    private static byte[] Nonce(long chunkId)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), chunkId);
        return nonce;
    }

    private static long ChunkCount(long length) =>
        length == 0 ? 1 : (length - 1) / ChunkBytes + 1;

    private static byte[] DeriveKey(byte[] masterKey, byte[] salt, int fileId)
    {
        var prefix = Encoding.UTF8.GetBytes("Peritial.SealedRecoveryBundle.AESGCM.v1|");
        var input = new byte[prefix.Length + salt.Length + 4];
        prefix.CopyTo(input, 0);
        salt.CopyTo(input, prefix.Length);
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(input.Length - 4), fileId);
        return HMACSHA256.HashData(masterKey, input);
    }

    private static void ValidateKeys(byte[] encryptionKey, byte[] s3SigningKey)
    {
        if (encryptionKey is not { Length: 32 } || s3SigningKey is not { Length: 32 } ||
            CryptographicOperations.FixedTimeEquals(encryptionKey, s3SigningKey))
            throw new InvalidOperationException(
                "Two different independent 32-byte recovery keys are required.");
    }

    private static string ObjectName(int i) =>
        i.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".blob";
    private static string CipherName(int i) =>
        i.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".enc";

    private static bool IsLink(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static void CheckRegularFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            IsLink(path))
            throw new InvalidOperationException("Recovery file is missing or unsafe.");
    }

    private static void CheckNewDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || File.Exists(path) ||
            Directory.Exists(path) ||
            !Directory.Exists(Path.GetDirectoryName(path)))
            throw new InvalidOperationException("Recovery destination must be a new absolute directory.");
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static FileStreamOptions PrivateWriteOptions()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private static async Task WritePrivateAsync(string path, byte[] bytes, CancellationToken ct)
    {
        await using var file = new FileStream(path, PrivateWriteOptions());
        await file.WriteAsync(bytes, ct);
        await file.FlushAsync(ct);
    }

    private static async Task VerifyPostgresAsync(string file, CancellationToken ct)
    {
        CheckRegularFile(file);
        CheckRegularFile(file + ".sha256");
        if (new FileInfo(file).Length < 5 ||
            new FileInfo(file + ".sha256").Length > 128)
            throw new InvalidOperationException("Invalid PostgreSQL backup format or digest.");
        var text = (await File.ReadAllTextAsync(file + ".sha256", ct)).Trim();
        if (text.Length != 64 ||
            !text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidOperationException("Invalid PostgreSQL SHA-256 digest.");
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] magic = new byte[5];
        await stream.ReadExactlyAsync(magic.AsMemory(), ct);
        if (!magic.AsSpan().SequenceEqual("PGDMP"u8))
            throw new InvalidOperationException("Not a PostgreSQL PGDMP archive.");
        stream.Position = 0;
        var actual = await SHA256.HashDataAsync(stream, ct);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(text), actual))
            throw new InvalidOperationException("PostgreSQL backup SHA-256 mismatch.");
    }
}
