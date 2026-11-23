using System.Security.Cryptography;

namespace Peritial.Business;

/// <summary>
/// SHA-256 digest of the exact ClamAV-admitted bytes, not an S3/Blob ETag.
/// Always validate length before a constant-time comparison. Never include
/// digests or payload in public document DTOs or error messages.
/// </summary>
public static class DocumentDigest
{
    public const int Size = 32;
    public static byte[] Compute(ReadOnlySpan<byte> bytes) => SHA256.HashData(bytes);

    public static bool Matches(byte[]? expectedSha256, ReadOnlySpan<byte> bytes) =>
        expectedSha256 is { Length: Size } &&
        CryptographicOperations.FixedTimeEquals(expectedSha256, SHA256.HashData(bytes));
}
