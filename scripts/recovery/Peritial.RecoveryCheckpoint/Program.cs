using System.Text.Json;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Npgsql;
using Peritial.Infrastructures;

// The checkpoint is a read-only integrity proof, not a backup of object bytes
// or an authenticated snapshot. All application writers MUST be stopped.
if (args.Length != 1 || args[0] is not ("capture" or "verify" or "export" or "restore-objects"))
{
    Console.Error.WriteLine("Usage: dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- capture|verify|export|restore-objects");
    return 2;
}

try
{
    if (Environment.GetEnvironmentVariable("PERITIAL_CHECKPOINT_CONFIRM") != "WRITERS_STOPPED")
        throw new InvalidOperationException("Explicit offline maintenance confirmation required.");

    static string Required(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException("Missing checkpoint configuration.");

    var checkpointFile = args[0] is "capture" or "verify"
        ? Required("PERITIAL_CHECKPOINT_FILE") : null;
    if (checkpointFile is not null && !Path.IsPathFullyQualified(checkpointFile))
        throw new InvalidOperationException("Checkpoint filename must be absolute.");
    var endpoint = Required("PERITIAL_CHECKPOINT_S3_ENDPOINT");
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
        endpointUri.Scheme is not ("http" or "https"))
        throw new InvalidOperationException("Invalid S3 endpoint.");
    var bucket = Required("PERITIAL_CHECKPOINT_S3_BUCKET");
    var access = Required("PERITIAL_CHECKPOINT_S3_ACCESS_KEY");
    var secret = Required("PERITIAL_CHECKPOINT_S3_SECRET_KEY");
    var pg = Required("PERITIAL_CHECKPOINT_POSTGRES");

    // An aggregate S3 backup has a larger capacity footprint than a single
    // document. Operators must explicitly authorize bigger recovery windows;
    // malformed, zero or negative values never mean "unlimited".
    var budgetText = Environment.GetEnvironmentVariable("PERITIAL_RECOVERY_MAX_TOTAL_BYTES");
    var maxTotalBytes = budgetText is null
        ? ObjectRecoveryBudget.DefaultMaxTotalBytes
        : long.TryParse(budgetText, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var configuredBudget) &&
          configuredBudget > 0
            ? configuredBudget
            : throw new InvalidOperationException("Invalid offline recovery byte budget.");

    // Bound the number of small objects independently of their byte sum.
    // Operators can raise the default after capacity planning, but never
    // beyond the reviewed hard ceiling to avoid huge manifests and RAM use.
    var countText = Environment.GetEnvironmentVariable("PERITIAL_RECOVERY_MAX_OBJECTS");
    var maxObjects = countText is null
        ? ObjectRecoveryBudget.DefaultMaxObjects
        : int.TryParse(countText, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var configuredCount) &&
          configuredCount is >= 1 and <= ObjectRecoveryBudget.MaxSupportedObjects
            ? configuredCount
            : throw new InvalidOperationException("Invalid offline recovery object count budget.");

    using var s3 = new AmazonS3Client(
        new BasicAWSCredentials(access, secret),
        new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });
    await using var db = NpgsqlDataSource.Create(pg);

    if (args[0] == "export")
    {
        var archive = Required("PERITIAL_ARCHIVE_DIR");
        var signingKey = ArchiveHmacKey.Load();
        try
        {
            var manifest = await ObjectRecoveryArchive.ExportAsync(
                db, s3, bucket, archive, signingKey, maxTotalBytes: maxTotalBytes, maxObjects: maxObjects);
            Console.WriteLine($"Object archive exported: {manifest.Checkpoint.Documents.Count} S3 blobs. Secure and preserve the directory.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }
    else if (args[0] == "restore-objects")
    {
        if (Environment.GetEnvironmentVariable("PERITIAL_RESTORE_OBJECTS_CONFIRM") != "EMPTY_DESTINATION_BUCKET")
            throw new InvalidOperationException("Restoring object bytes requires empty destination confirmation.");
        var archive = Required("PERITIAL_ARCHIVE_DIR");
        var signingKey = ArchiveHmacKey.Load();
        try
        {
            var imported = await ObjectRecoveryArchive.ImportAsync(
                db, s3, bucket, archive, signingKey, maxTotalBytes: maxTotalBytes, maxObjects: maxObjects);
            Console.WriteLine($"Object archive restored and verified: {imported} S3 blobs.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }
    else if (args[0] == "capture")
    {
        if (File.Exists(checkpointFile))
            throw new InvalidOperationException("Refusing to overwrite existing checkpoint.");

        var manifest = await ObjectRecoveryCheckpoint.CaptureAsync(
            db, s3, bucket, maxTotalBytes: maxTotalBytes, maxObjects: maxObjects);
        var partialFile = checkpointFile + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            // The manifest includes tenant IDs and storage keys. Request 0600
            // at creation time, not after an initially world-readable file
            // was created (chmod would expose a short but real leak window).
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(partialFile, fileOptions))
            {
                await JsonSerializer.SerializeAsync(output, manifest);
                await output.FlushAsync();
            }
            File.Move(partialFile, checkpointFile); // never overwrite
        }
        finally
        {
            if (File.Exists(partialFile))
                File.Delete(partialFile);
        }
        Console.WriteLine($"Checkpoint captured: {manifest.Documents.Count} objects. No blob bytes were backed up.");
    }
    else
    {
        var info = new FileInfo(checkpointFile);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            info.Length == 0 || info.Length > 50_000_000)
            throw new InvalidOperationException("Checkpoint manifest is missing or unsafe.");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(checkpointFile);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                         UnixFileMode.OtherRead | UnixFileMode.OtherWrite |
                         UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidOperationException("Checkpoint manifest permissions are too broad.");
        }
        await using var input = new FileStream(
            checkpointFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        var manifest = await JsonSerializer.DeserializeAsync<ObjectRecoveryCheckpoint.Manifest>(input)
            ?? throw new InvalidOperationException("Malformed checkpoint manifest.");
        await ObjectRecoveryCheckpoint.VerifyAsync(
            db, s3, bucket, manifest, maxTotalBytes: maxTotalBytes, maxObjects: maxObjects);
        Console.WriteLine($"Checkpoint verified: {manifest.Documents.Count} database references and object digests.");
    }
    return 0;
}
catch (Exception ex)
{
    // Print no tenant IDs, S3 keys, raw SQL, file paths, tokens or credentials.
    Console.Error.WriteLine("Offline object checkpoint failed closed (" + ex.GetType().Name + ").");
    return 1;
}
