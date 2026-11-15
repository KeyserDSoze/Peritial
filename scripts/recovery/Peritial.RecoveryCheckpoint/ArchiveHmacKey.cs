/// <summary>
/// Import the HMAC key from a separate administrator-controlled private file.
/// Never include it in the archive, pass it in process arguments or log it.
/// </summary>
internal static class ArchiveHmacKey
{
    internal static byte[] Load()
    {
        var path = Environment.GetEnvironmentVariable("PERITIAL_ARCHIVE_HMAC_KEY_FILE");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("A private absolute HMAC key file is required.");

        var file = new FileInfo(path);
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            file.Length != 32)
            throw new InvalidOperationException("HMAC key file must be a regular 32-byte file.");
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode allowed = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if ((File.GetUnixFileMode(path) & ~allowed) != 0)
                throw new InvalidOperationException("HMAC key file must be owner-only (0600 or tighter).");
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != 32)
            throw new InvalidOperationException("HMAC key file size changed during read.");
        return bytes;
    }
}
