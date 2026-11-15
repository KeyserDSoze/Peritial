using Peritial.Domains;

namespace Peritial.Business;

public sealed record DocumentDto(Guid Id, Guid? PeriziaId, string FileName, string ContentType, long Length);

public sealed class FileIngestionService(
    ITenantContext context, IPeriziaRepository perizie, IDocumentRepository documents, IObjectStore objects,
    ISecurityAuditTrail audit, IFileMalwareScanner scanner, IUploadIntentLedger uploadIntents)
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
        if (!context.HasScope("peritial.write"))
        {
            await audit.AppendAsync(new SecurityAuditEvent(
                Guid.NewGuid(), context.TenantId, context.Subject, "document.ingest",
                "denied", null, DateTime.UtcNow), ct);
            throw new UnauthorizedAccessException("Insufficient OAuth scope.");
        }

        if (size is <= 0 or > MaxUploadBytes)
            throw new ArgumentException("Invalid file size.", nameof(size));

        var safeName = UploadedDocumentValidation.CanonicalFileName(name);
        var ext = Path.GetExtension(safeName);
        if (!AllowedMimes.TryGetValue(ext, out var allowedMimes) ||
            !allowedMimes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("File extension and media type do not match.", nameof(contentType));

        // Authorization and ownership checks precede reading or writing the file.
        if (periziaId.HasValue &&
            await perizie.FindAsync(context.TenantId, periziaId.Value, ct) is null)
            throw new KeyNotFoundException("Perizia not found.");

        // Bound memory and verify the actual stream length, including non-seekable streams.
        // Read at most declared size + 1 byte, never unbounded CopyToAsync into object storage.
        using var buffer = new MemoryStream((int)Math.Min(size, 1024 * 1024));
        var chunk = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var toRead = (int)Math.Min(chunk.Length, size - total + 1);
            var read = await content.ReadAsync(chunk.AsMemory(0, toRead), ct);
            if (read == 0) break;
            total += read;
            if (total > size)
                throw new ArgumentException("File stream contains more bytes than declared.", nameof(size));
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        if (total != size)
            throw new ArgumentException("File stream is shorter than declared.", nameof(size));

        UploadedDocumentValidation.ValidateContent(ext, buffer.GetBuffer().AsSpan(0, (int)total));

        // No unscanned file is allowed in permanent object storage, metadata,
        // or the success audit. No fallbacks when clamd is down or inconclusive.
        var verdict = await scanner.ScanAsync(buffer.GetBuffer().AsMemory(0, (int)total), ct);
        if (verdict == MalwareScanResult.Infected)
        {
            // No file name, content or signature details enter the audit trail.
            await audit.AppendAsync(new SecurityAuditEvent(
                Guid.NewGuid(), context.TenantId, context.Subject, "document.scan",
                "denied", null, DateTime.UtcNow), ct);
            throw new FileThreatDetectedException();
        }
        if (verdict != MalwareScanResult.Clean)
            throw new MalwareScannerUnavailableException();

        buffer.Position = 0;

        var id = Guid.NewGuid();
        var key = DocumentKeys.For(context.TenantId, id, safeName);
        // Commit a durable reservation BEFORE PUT. If the process dies at any
        // point afterwards, the recovery role can find this storage key.
        await uploadIntents.RecordAsync(context.TenantId, id, key, ct);
        try
        {
            // A PUT error cannot have committed metadata (metadata is written
            // only after this call returns). Synchronous cleanup is safe here.
            await objects.PutAsync(key, buffer, contentType, ct);
        }
        catch (Exception putError)
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await objects.DeleteAsync(key, cleanupTimeout.Token);
                await uploadIntents.ForgetAsync(context.TenantId, id, cleanupTimeout.Token);
            }
            catch (Exception cleanupError)
            {
                // Leave the durable reservation for automatic future retry.
                throw new AggregateException(
                    "Object upload failed and cleanup is pending recovery.",
                    putError, cleanupError);
            }
            throw;
        }

        // The repository consumes the reservation in the SAME transaction as
        // document metadata + audit. If the database commit acknowledgement
        // is lost, deleting this blob here would be dangerous: it may already
        // be a valid, referenced document. Keep it for row-locked recovery.
        var record = new DocumentRecord(id, context.TenantId, periziaId, safeName,
            contentType, total, key, DateTime.UtcNow);
        await documents.AddAuditedAsync(record, new SecurityAuditEvent(
            Guid.NewGuid(), context.TenantId, context.Subject, "document.ingest",
            "succeeded", id, DateTime.UtcNow), ct);
        return new DocumentDto(id, periziaId, safeName, contentType, total);
    }

    public async Task<DocumentDto> IngestBase64Async(
        string name, string contentType, string base64, Guid? periziaId = null, CancellationToken ct = default)
    {
        if (!context.HasScope("peritial.write"))
        {
            await audit.AppendAsync(new SecurityAuditEvent(
                Guid.NewGuid(), context.TenantId, context.Subject, "document.ingest",
                "denied", null, DateTime.UtcNow), ct);
            throw new UnauthorizedAccessException("Insufficient OAuth scope.");
        }
        if (string.IsNullOrEmpty(base64) || base64.Length > ((MaxMcpBytes + 2) / 3 * 4 + 8))
            throw new ArgumentException("MCP upload exceeds the limit.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException ex) { throw new ArgumentException("Invalid base64 content.", nameof(base64), ex); }
        if (bytes.Length > MaxMcpBytes)
            throw new ArgumentException("MCP upload exceeds the limit.");

        using var stream = new MemoryStream(bytes, writable: false);
        return await IngestAsync(name, contentType, stream, bytes.LongLength, periziaId, ct);
    }
}
