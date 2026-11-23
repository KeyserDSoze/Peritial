using Peritial.Domains;

namespace Peritial.Business;

/// <summary>
/// Internal storage locator returned only after a tenant-scoped PostgreSQL
/// lookup; never serialized by REST/MCP. The public result has no storage key.
/// </summary>
public sealed record DocumentDownloadLocation(DocumentSummaryDto Document, string StorageKey);

public interface IDocumentDownloadRepository
{
    Task<DocumentDownloadLocation?> FindForDownloadAsync(
        TenantId tenant, Guid documentId, CancellationToken ct = default);
}

/// <summary>Private object-store reader; implementations enforce the declared byte budget.</summary>
public interface IDocumentBlobReader
{
    Task<byte[]> ReadAsync(string key, long expectedLength, CancellationToken ct = default);
}

/// <summary>Only file bytes, a canonical file name and a forced attachment MIME.</summary>
public sealed record DocumentDownloadResult(string FileName, string ContentType, byte[] Bytes);

/// <summary>
/// Read a previously ClamAV-admitted object only for the signed tenant,
/// with a bounded blob read and a committed security audit before bytes leave
/// the service. HTTP delivery can still fail after this pre-send success audit.
/// </summary>
public sealed class DocumentDownloadService(
    ITenantContext actor, IDocumentDownloadRepository repository,
    IDocumentBlobReader blobs, ISecurityAuditTrail audit)
{
    public async Task<DocumentDownloadResult> ReadAsync(Guid id, CancellationToken ct = default)
    {
        if (!actor.HasScope("peritial.read") && !actor.HasScope("peritial.write"))
        {
            await audit.AppendAsync(Event("denied", null), ct);
            throw new UnauthorizedAccessException("Insufficient OAuth scope.");
        }
        if (id == Guid.Empty)
            throw new KeyNotFoundException("Document not found.");

        var location = await repository.FindForDownloadAsync(actor.TenantId, id, ct)
            ?? throw new KeyNotFoundException("Document not found.");
        var item = location.Document;
        if (item.Id != id ||
            item.Length is <= 0 or > FileIngestionService.MaxUploadBytes ||
            string.IsNullOrWhiteSpace(item.FileName))
            throw new DocumentContentUnavailableException();

        // Defense in depth against a corrupted/stale PostgreSQL storage_key.
        // The user may choose only the UUID; never accept a blob path or
        // tenant selector from HTTP/MCP.
        string expectedKey;
        try { expectedKey = DocumentKeys.For(actor.TenantId, id, item.FileName); }
        catch (ArgumentException) { throw new DocumentContentUnavailableException(); }
        if (!string.Equals(location.StorageKey, expectedKey, StringComparison.Ordinal))
            throw new DocumentContentUnavailableException();

        byte[] content;
        try { content = await blobs.ReadAsync(expectedKey, item.Length, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new DocumentContentUnavailableException(); }
        if (content.LongLength != item.Length)
            throw new DocumentContentUnavailableException();

        // Audit is a hard admission gate. Do not return the bytes if the
        // append-only PostgreSQL record cannot be committed. This is an audit
        // of authorized preparation, NOT proof of completed HTTP delivery.
        await audit.AppendAsync(Event("succeeded", id), ct);
        return new DocumentDownloadResult(item.FileName, "application/octet-stream", content);
    }

    private SecurityAuditEvent Event(string outcome, Guid? resource) =>
        new(Guid.NewGuid(), actor.TenantId, actor.Subject, "document.download",
            outcome, resource, DateTime.UtcNow);
}

/// <summary>No storage/provider details or private object key in API errors.</summary>
public sealed class DocumentContentUnavailableException()
    : Exception("Document contents are temporarily unavailable.")
{
}
