using System.Security.Cryptography;
using Peritial.Infrastructures;
using Amazon.S3;
using Amazon.Runtime;

// Manual security boundary: two independent 32-byte keys in secret-managed
// files outside the backup; no plaintext secrets in argv, logs or Git.
if (args.Length != 1 || args[0] is not ("seal" or "unseal" or "push-sealed" or "pull-sealed" or "pull-historic"))
{
    Console.Error.WriteLine("Usage: dotnet run --project scripts/recovery/Peritial.RecoveryBundle -- seal|unseal|push-sealed|pull-sealed|pull-historic");
    return 2;
}
static string Required(string key) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Missing recovery configuration.");

static byte[] PrivateKey(string envName)
{
    var path = Required(envName);
    if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        throw new InvalidOperationException("A private absolute key file is required.");
    var file = new FileInfo(path);
    if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length != 32)
        throw new InvalidOperationException("Recovery key is not a regular 32-byte file.");
    if (!OperatingSystem.IsWindows())
    {
        const UnixFileMode allowed = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if ((File.GetUnixFileMode(path) & ~allowed) != 0)
            throw new InvalidOperationException("Recovery key file permissions are too broad.");
    }
    var result = File.ReadAllBytes(path);
    if (result.Length != 32)
        throw new InvalidOperationException("Recovery key changed during read.");
    return result;
}

byte[]? master = null;
byte[]? s3SigningKey = null;
try
{
    var expectedConfirmation = args[0] switch
    {
        "seal" => "WRITERS_STOPPED",
        "unseal" => "ISOLATED_RESTORE",
        "push-sealed" => "REMOTE_ENCRYPTED_UPLOAD",
        "pull-historic" => "ISOLATED_HISTORICAL_DOWNLOAD",
        _ => "ISOLATED_ENCRYPTED_DOWNLOAD"
    };
    if (Environment.GetEnvironmentVariable("PERITIAL_BUNDLE_CONFIRM") != expectedConfirmation)
        throw new InvalidOperationException("Explicit offline recovery confirmation required.");

    // Validate before loading keys or constructing remote clients.
    var maxPlaintextBytes = SealedRecoveryBudget.ParsePlaintextLimit(
        Environment.GetEnvironmentVariable("PERITIAL_BUNDLE_MAX_PLAINTEXT_BYTES"));

    master = PrivateKey("PERITIAL_BUNDLE_AES_KEY_FILE");
    s3SigningKey = PrivateKey("PERITIAL_ARCHIVE_HMAC_KEY_FILE");
    if (CryptographicOperations.FixedTimeEquals(master, s3SigningKey))
        throw new InvalidOperationException("Independent encryption and S3 signing keys required.");

    var bundle = Required("PERITIAL_BUNDLE_DIR");
    if (args[0] == "seal")
    {
        await SealedRecoveryBundle.SealAsync(
            Required("PERITIAL_BUNDLE_POSTGRES_DUMP"),
            Required("PERITIAL_ARCHIVE_DIR"),
            bundle, master, s3SigningKey, maxPlaintextBytes: maxPlaintextBytes);
        Console.WriteLine("Private, authenticated encrypted PostgreSQL+S3 recovery bundle sealed.");
    }
    else if (args[0] == "unseal")
    {
        await SealedRecoveryBundle.UnsealAsync(
            bundle, Required("PERITIAL_BUNDLE_UNSEAL_DIR"),
            master, s3SigningKey, maxPlaintextBytes: maxPlaintextBytes);
        Console.WriteLine("Encrypted recovery bundle authenticated and unsealed to isolated private directory.");
    }
    else
    {
        var endpoint = Required("PERITIAL_OFFSITE_S3_ENDPOINT");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException("Remote encrypted backup endpoint is invalid.");
        // Credentials should NEVER traverse HTTP on any real remote.
        // The only exception is a specifically named disposable CI container.
        var demoRequested =
            Environment.GetEnvironmentVariable("PERITIAL_OFFSITE_CI_HTTP") ==
                "DISPOSABLE_TEST_ONLY";
        var disposableDemo = demoRequested && uri.Scheme == "http" &&
            uri.Host == "seaweedfs-backup";
        if (demoRequested && !disposableDemo)
            throw new InvalidOperationException(
                "Disposable retention bypass is restricted to the named CI server.");
        if (uri.Scheme != "https" && !disposableDemo)
            throw new InvalidOperationException("Remote encrypted backups require HTTPS.");
        var bucket = Required("PERITIAL_OFFSITE_S3_BUCKET");
        var backupId = Required("PERITIAL_OFFSITE_BACKUP_ID");
        using var remote = new AmazonS3Client(
            new BasicAWSCredentials(
                Required("PERITIAL_OFFSITE_S3_ACCESS_KEY"),
                Required("PERITIAL_OFFSITE_S3_SECRET_KEY")),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });
        if (args[0] == "push-sealed")
        {
            // Separate credentials at the S3 provider boundary: writer may
            // PUT but cannot GET ciphertext. The verifier can GET/inspect
            // Object Lock versions but has no PUT or DELETE permission.
            using var verifier = disposableDemo ? null : new AmazonS3Client(
                new BasicAWSCredentials(
                    Required("PERITIAL_OFFSITE_VERIFY_S3_ACCESS_KEY"),
                    Required("PERITIAL_OFFSITE_VERIFY_S3_SECRET_KEY")),
                new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });
            await SealedBundleOffsite.UploadAsync(bundle, remote, bucket, backupId,
                master, disposableUnprotectedDemo: disposableDemo,
                verificationSource: verifier, maxPlaintextBytes: maxPlaintextBytes);
            Console.WriteLine(disposableDemo
                ? "Disposable demo encrypted backup copied and verified without Object Lock or distinct S3 reader."
                : "Encrypted backup copied, integrity verified by an independent S3 reader, and remote completion marker published.");
        }
        else if (args[0] == "pull-sealed")
        {
            await SealedBundleOffsite.DownloadAsync(remote, bucket, backupId,
                Required("PERITIAL_BUNDLE_DOWNLOAD_DIR"), master,
                disposableUnprotectedDemo: disposableDemo, maxPlaintextBytes: maxPlaintextBytes);
            Console.WriteLine("Remote encrypted backup authenticated and recovered to a private directory.");
        }
        else
        {
            // Never choose historical versions implicitly, and NEVER allow
            // a WORM-history restore via the unprotected CI demo bypass.
            if (disposableDemo)
                throw new InvalidOperationException(
                    "Historical Object Lock recovery is unavailable on the unprotected demo.");
            var selected = SealedBundleOffsite.LoadHistoricalVersionSelection(
                Required("PERITIAL_OFFSITE_VERSION_SELECTION_FILE"));
            await SealedBundleOffsite.DownloadHistoricalAsync(remote, bucket, backupId,
                Required("PERITIAL_BUNDLE_DOWNLOAD_DIR"), master, selected,
                maxPlaintextBytes: maxPlaintextBytes);
            Console.WriteLine("Explicitly pinned historical encrypted S3 versions authenticated and recovered.");
        }
    }
    return 0;
}
catch (Exception ex)
{
    // Never log paths, database or S3 references, plaintext or secret material.
    Console.Error.WriteLine("Recovery bundle failed closed (" + ex.GetType().Name + ").");
    return 1;
}
finally
{
    if (master is not null)
        CryptographicOperations.ZeroMemory(master);
    if (s3SigningKey is not null)
        CryptographicOperations.ZeroMemory(s3SigningKey);
}
