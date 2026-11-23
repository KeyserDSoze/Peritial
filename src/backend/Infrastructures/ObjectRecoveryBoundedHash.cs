using System.Security.Cryptography;

namespace Peritial.Infrastructures;

/// <summary>
/// One bounded streaming implementation for offline S3 checkpoint reads,
/// archive exports, and archive pre-restore hashing. The trusted size comes
/// from PostgreSQL or a checkpoint already compared with PostgreSQL.
/// </summary>
internal static class ObjectRecoveryBoundedHash
{
    internal static async Task<(long Length, string Sha256)> CopyHashAsync(
        Stream input, Stream output, long expectedLength, CancellationToken ct = default)
    {
        if (expectedLength <= 0)
            throw new InvalidOperationException("Recovery requires a positive committed document length.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long length = 0;
        while (true)
        {
            // Read at most one byte past the persisted size to detect extra
            // bytes. Do not accept the unbounded size of an untrusted S3 body.
            var remaining = expectedLength - length;
            var readLimit = (int)(remaining >= buffer.Length
                ? buffer.Length : remaining + 1);
            var read = await input.ReadAsync(buffer.AsMemory(0, readLimit), ct);
            if (read == 0) break;
            if (read > remaining)
                throw new InvalidOperationException(
                    "Recovery source exceeds the committed document length.");
            length += read;
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        if (length != expectedLength)
            throw new InvalidOperationException(
                "Recovery source is shorter than the committed document length.");

        return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}
