using Peritial.Domains;

namespace Peritial.Business;

public sealed record DocumentDto(Guid Id, Guid? PeriziaId, string FileName, string ContentType, long Length);

public sealed class FileIngestionService(
    ITenantContext context, IPeriziaRepository perizie, IDocumentRepository documents, IObjectStore objects)
{
    public const long MaxUploadBytes = 25 * 1024 * 1024;
    public const int MaxMcpBytes = 4 * 1024 * 1024;
    private static readonly Dictionary<string, string[]> AllowedMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = ["application/pdf"],
        [".xml"] = ["application/xml", "text/xml"],
        [".jpg"] = ["image/jpeg"],
        [".jpeg"] = ["image/jpeg"],
        [".png"] = ["image/png"],
        [".docx"] = ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"]
    };

    public async Task<DocumentDto> IngestAsync(
        string name, string contentType, Stream content, long size, Guid? periziaId = null,
        CancellationToken ct = default)
    {
        if (!context.HasScope("peritial.write")) throw new UnauthorizedAccessException("Insufficient OAuth scope.");
        if (size is <= 0 or > MaxUploadBytes) throw new ArgumentException("Invalid file size.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255) throw new ArgumentException("Invalid file name.");
        var ext = Path.GetExtension(name);
        if (!AllowedMimes.TryGetValue(ext, out var mimes) ||
            !mimes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("File type does not match the allowed media types.");
        if (periziaId.HasValue && await perizie.FindAsync(context.TenantId, periziaId.Value, ct) is null)
            throw new KeyNotFoundException("Perizia not found.");

        var id = Guid.NewGuid();
        var key = DocumentKeys.For(context.TenantId, id, name);
        // Storage keys never incorporate the uploaded file name.
        await objects.PutAsync(key, content, contentType, ct);
        var record = new DocumentRecord(id, context.TenantId, periziaId, Path.GetFileName(name),
            contentType, size, key, DateTime.UtcNow);
        await documents.AddAsync(record, ct);
        return new DocumentDto(id, periziaId, record.FileName, contentType, size);
    }

    public async Task<DocumentDto> IngestBase64Async(
        string name, string contentType, string base64, Guid? periziaId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > ((MaxMcpBytes + 2) / 3 * 4 + 8))
            throw new ArgumentException("MCP upload exceeds the limit.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new ArgumentException("Invalid base64 content."); }
        if (bytes.Length > MaxMcpBytes) throw new ArgumentException("MCP upload exceeds the limit.");
        using var stream = new MemoryStream(bytes, writable: false);
        return await IngestAsync(name, contentType, stream, bytes.LongLength, periziaId, ct);
    }
}
