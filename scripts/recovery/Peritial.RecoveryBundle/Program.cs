using System.Security.Cryptography;
using Peritial.Infrastructures;

// Manual security boundary: two independent 32-byte keys in secret-managed
// files outside the backup; no plaintext secrets in argv, logs or Git.
if (args.Length != 1 || args[0] is not ("seal" or "unseal"))
{
    Console.Error.WriteLine("Usage: dotnet run --project scripts/recovery/Peritial.RecoveryBundle -- seal|unseal");
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
    var expectedConfirmation = args[0] == "seal"
        ? "WRITERS_STOPPED" : "ISOLATED_RESTORE";
    if (Environment.GetEnvironmentVariable("PERITIAL_BUNDLE_CONFIRM") != expectedConfirmation)
        throw new InvalidOperationException("Explicit offline recovery confirmation required.");

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
            bundle, master, s3SigningKey);
        Console.WriteLine("Private, authenticated encrypted PostgreSQL+S3 recovery bundle sealed.");
    }
    else
    {
        await SealedRecoveryBundle.UnsealAsync(
            bundle, Required("PERITIAL_BUNDLE_UNSEAL_DIR"),
            master, s3SigningKey);
        Console.WriteLine("Encrypted recovery bundle authenticated and unsealed to isolated private directory.");
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
