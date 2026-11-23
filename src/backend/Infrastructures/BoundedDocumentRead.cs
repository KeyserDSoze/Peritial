using Peritial.Business;

namespace Peritial.Infrastructures;

/// <summary>
/// Read a storage object only up to the audited metadata length, with an
/// absolute 25 MiB ceiling and an extra-byte check. Never stream unchecked
/// provider data directly into an HTTP response.
/// </summary>
internal static class BoundedDocumentRead
{
    internal static async Task<byte[]> ReadExactAsync(
        Stream stream, long expectedLength, CancellationToken ct = default)
    {
        if (expectedLength is <= 0 or > FileIngestionService.MaxUploadBytes)
            throw new InvalidDataException("Invalid stored document length.");

        var bytes = new byte[checked((int)expectedLength)];
        var received = 0;
        while (received < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(received), ct);
            if (read == 0)
                throw new InvalidDataException("Stored document is incomplete.");
            received += read;
        }
        var extra = new byte[1];
        if (await stream.ReadAsync(extra.AsMemory(), ct) != 0)
            throw new InvalidDataException("Stored document exceeds its metadata.");
        return bytes;
    }
}
