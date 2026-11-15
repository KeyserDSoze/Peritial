using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Npgsql;
using Peritial.Infrastructures;

// The checkpoint is a read-only integrity proof, not a backup of object bytes
// or an authenticated snapshot. All application writers MUST be stopped.
if (args.Length != 1 || args[0] is not ("capture" or "verify"))
{
    Console.Error.WriteLine("Usage: dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- capture|verify");
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

    var checkpointFile = Required("PERITIAL_CHECKPOINT_FILE");
    if (!Path.IsPathFullyQualified(checkpointFile))
        throw new InvalidOperationException("Checkpoint filename must be absolute.");
    var endpoint = Required("PERITIAL_CHECKPOINT_S3_ENDPOINT");
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
        endpointUri.Scheme is not ("http" or "https"))
        throw new InvalidOperationException("Invalid S3 endpoint.");
    var bucket = Required("PERITIAL_CHECKPOINT_S3_BUCKET");
    var access = Required("PERITIAL_CHECKPOINT_S3_ACCESS_KEY");
    var secret = Required("PERITIAL_CHECKPOINT_S3_SECRET_KEY");
    var pg = Required("PERITIAL_CHECKPOINT_POSTGRES");

    using var s3 = new AmazonS3Client(
        new BasicAWSCredentials(access, secret),
        new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });
    await using var db = NpgsqlDataSource.Create(pg);

    if (args[0] == "capture")
    {
        if (File.Exists(checkpointFile))
            throw new InvalidOperationException("Refusing to overwrite existing checkpoint.");

        var manifest = await ObjectRecoveryCheckpoint.CaptureAsync(db, s3, bucket);
        var partialFile = checkpointFile + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(
                partialFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(partialFile,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
        await ObjectRecoveryCheckpoint.VerifyAsync(db, s3, bucket, manifest);
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
