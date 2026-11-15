using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;

namespace Peritial.Infrastructures;

/// <summary>
/// Offline-only export of actual referenced S3 bytes. The archive directory is
/// a local bundle of opaque object filenames plus an integrity manifest.
/// It is NOT an authenticated, encrypted or atomic PostgreSQL/S3 snapshot.
/// </summary>
public static class ObjectRecoveryArchive
{
    public sealed record ArchiveManifest(
        int ArchiveVersion,
        ObjectRecoveryCheckpoint.Manifest Checkpoint,
        IReadOnlyList<string> ContentTypes);

    private const string ManifestName = "manifest.json";
    private const string ObjectDirectory = "objects";
    private const long MaxManifestBytes = 50_000_000;

    /// <summary>
    /// Export to a NEW private directory. Every S3 byte is verified against the
    /// separately captured metadata/checkpoint before a bundle is published.
    /// Uploaded blobs are named by ordinal, never by untrusted S3 keys.
    /// </summary>
    public static async Task<ArchiveManifest> ExportAsync(
        NpgsqlDataSource administrator, IAmazonS3 source, string bucket,
        string destinationDirectory, CancellationToken ct = default)
    {
        CheckNewDestination(destinationDirectory);
        var checkpoint = await ObjectRecoveryCheckpoint.CaptureAsync(
            administrator, source, bucket, ct);
        var stage = destinationDirectory + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            CreatePrivateDirectory(stage);
            var objects = Path.Combine(stage, ObjectDirectory);
            CreatePrivateDirectory(objects);
            var contentTypes = new List<string>(checkpoint.Documents.Count);
            for (var index = 0; index < checkpoint.Documents.Count; index++)
            {
                var item = checkpoint.Documents[index];
                using var remote = await source.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = bucket, Key = item.StorageKey
                }, ct);
                var mime = remote.Headers.ContentType ?? "application/octet-stream";
                if (mime.Length is < 1 or > 256 || mime.Any(c => c is '\r' or '\n'))
                    throw new InvalidOperationException("Invalid object content type.");
                contentTypes.Add(mime);

                var filename = Path.Combine(objects, NameFor(index));
                await using var local = new FileStream(filename, PrivateWriteOptions());
                var (length, digest) = await CopyHashAsync(remote.ResponseStream, local, ct);
                if (length != item.LengthBytes ||
                    !string.Equals(digest, item.Sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Source S3 object changed during archive export; discard checkpoint.");
            }
            var bundle = new ArchiveManifest(1, checkpoint, contentTypes);
            await using (var stream = new FileStream(
                Path.Combine(stage, ManifestName), PrivateWriteOptions()))
            {
                await JsonSerializer.SerializeAsync(stream, bundle, cancellationToken: ct);
                await stream.FlushAsync(ct);
            }
            CheckNewDestination(destinationDirectory);
            Directory.Move(stage, destinationDirectory); // no existing directory replaced
            return bundle;
        }
        finally
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
        }
    }

    /// <summary>
    /// Restore only into a separately provisioned EMPTY S3 bucket. Validate
    /// all local bytes and the COMPLETE target PostgreSQL metadata first.
    /// A failure mid-upload leaves a PARTIAL bucket; discard the bucket, never
    /// retry against it or use in production. Does not write to PostgreSQL.
    /// </summary>
    public static async Task<int> ImportAsync(
        NpgsqlDataSource restoredDatabase, IAmazonS3 destination, string bucket,
        string archiveDirectory, CancellationToken ct = default)
    {
        var manifest = await ReadAndValidateAsync(archiveDirectory, ct);
        await ObjectRecoveryCheckpoint.VerifyMetadataAsync(
            restoredDatabase, manifest.Checkpoint, ct);

        // Fail before ANY S3 PUT if one file has been modified after export.
        // Hash every source file rather than trusting the manifest alone.
        for (var i = 0; i < manifest.Checkpoint.Documents.Count; i++)
        {
            var saved = manifest.Checkpoint.Documents[i];
            var file = Path.Combine(archiveDirectory, ObjectDirectory, NameFor(i));
            await using var input = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var (length, hash) = await CopyHashAsync(input, Stream.Null, ct);
            if (length != saved.LengthBytes || hash != saved.Sha256)
                throw new InvalidOperationException("Archive blob checksum or length differs.");
        }

        if (string.IsNullOrWhiteSpace(bucket))
            throw new InvalidOperationException("Destination bucket is required.");
        var inventory = await destination.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket, MaxKeys = 1
        }, ct);
        if (inventory.S3Objects is { Count: > 0 })
            throw new InvalidOperationException(
                "Restore refused: S3 destination bucket must be newly provisioned and empty.");

        for (var i = 0; i < manifest.Checkpoint.Documents.Count; i++)
        {
            var saved = manifest.Checkpoint.Documents[i];
            var file = Path.Combine(archiveDirectory, ObjectDirectory, NameFor(i));
            await using var input = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read);
            await destination.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = saved.StorageKey,
                InputStream = input,
                ContentType = manifest.ContentTypes[i],
                AutoCloseStream = false,
                // A second writer must not replace an existing destination key.
                IfNoneMatch = "*"
            }, ct);
        }
        // GET every restored object and validate SHA-256 against the database.
        await ObjectRecoveryCheckpoint.VerifyAsync(
            restoredDatabase, destination, bucket, manifest.Checkpoint, ct);
        return manifest.Checkpoint.Documents.Count;
    }

    public static async Task<ArchiveManifest> ReadAndValidateAsync(
        string archiveDirectory, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(archiveDirectory) ||
            !Directory.Exists(archiveDirectory) ||
            IsLink(archiveDirectory))
            throw new InvalidOperationException("Archive directory is missing or unsafe.");

        var manifestPath = Path.Combine(archiveDirectory, ManifestName);
        var objectDir = Path.Combine(archiveDirectory, ObjectDirectory);
        if (!File.Exists(manifestPath) || IsLink(manifestPath) ||
            !Directory.Exists(objectDir) || IsLink(objectDir) ||
            new FileInfo(manifestPath).Length is <= 0 or > MaxManifestBytes)
            throw new InvalidOperationException("Archive manifest or content directory is unsafe.");

        var topEntries = Directory.EnumerateFileSystemEntries(archiveDirectory)
            .Select(Path.GetFileName).ToArray();
        if (topEntries.Length != 2 ||
            !topEntries.Contains(ManifestName, StringComparer.Ordinal) ||
            !topEntries.Contains(ObjectDirectory, StringComparer.Ordinal))
            throw new InvalidOperationException("Archive directory has unexpected entries.");

        await using var stream = new FileStream(
            manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var manifest = await JsonSerializer.DeserializeAsync<ArchiveManifest>(stream, cancellationToken: ct);
        if (manifest is null || manifest.ArchiveVersion != 1 ||
            manifest.Checkpoint is null || manifest.Checkpoint.FormatVersion != 1 ||
            manifest.Checkpoint.Documents is null || manifest.ContentTypes is null ||
            manifest.Checkpoint.Documents.Count != manifest.ContentTypes.Count)
            throw new InvalidOperationException("Archive manifest invalid or unsupported.");
        var objects = Directory.EnumerateFileSystemEntries(objectDir).ToArray();
        if (objects.Length != manifest.Checkpoint.Documents.Count)
            throw new InvalidOperationException("Archive has missing or unexpected object files.");
        for (var i = 0; i < manifest.Checkpoint.Documents.Count; i++)
        {
            var item = manifest.Checkpoint.Documents[i];
            var type = manifest.ContentTypes[i];
            var file = Path.Combine(objectDir, NameFor(i));
            if (item is null || string.IsNullOrWhiteSpace(item.StorageKey) ||
                item.LengthBytes <= 0 || item.Sha256 is not { Length: 64 } ||
                !item.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                type is null || type.Length is < 1 or > 256 ||
                type.Any(c => c is '\r' or '\n') ||
                !File.Exists(file) || IsLink(file) ||
                new FileInfo(file).Length != item.LengthBytes)
                throw new InvalidOperationException("Archive object entry invalid or incomplete.");
        }
        return manifest;
    }

    private static string NameFor(int index) =>
        index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".blob";

    private static bool IsLink(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static void CheckNewDestination(string destination)
    {
        if (!Path.IsPathFullyQualified(destination) ||
            Directory.Exists(destination) || File.Exists(destination) ||
            !Directory.Exists(Path.GetDirectoryName(destination)))
            throw new InvalidOperationException(
                "Archive target must be an unused absolute path under an existing private directory.");
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
            Mode = FileMode.CreateNew, Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private static async Task<(long Length, string Sha256)> CopyHashAsync(
        Stream input, Stream output, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            length = checked(length + read);
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}
