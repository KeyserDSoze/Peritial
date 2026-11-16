using Peritial.Domains;

namespace Peritial.Business;

/// <summary>
/// Public, allowlisted metadata. Object-storage keys, tenant IDs, signed
/// URLs, scan internals and file bytes are never returned to a client.
/// </summary>
public sealed record DocumentSummaryDto(
    Guid Id, Guid? PeriziaId, string FileName, string ContentType,
    long Length, DateTime CreatedAtUtc);

public interface IDocumentCatalogRepository
{
    Task<IReadOnlyList<DocumentSummaryDto>> ListAsync(
        TenantId tenant, Guid? periziaId, int limit, CancellationToken ct = default);
    Task<DocumentSummaryDto?> FindAsync(
        TenantId tenant, Guid id, CancellationToken ct = default);
}

/// <summary>
/// Both REST and MCP must pass the signed tenant through this service.
/// Metadata reads never create a mutation-success audit event.
/// </summary>
public sealed class DocumentCatalogService(ITenantContext context, IDocumentCatalogRepository repository)
{
    public async Task<IReadOnlyList<DocumentSummaryDto>> ListAsync(
        Guid? periziaId = null, int limit = 50, CancellationToken ct = default)
    {
        RequireRead();
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be from 1 to 100.");
        return await repository.ListAsync(context.TenantId, periziaId, limit, ct);
    }

    public async Task<DocumentSummaryDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        if (id == Guid.Empty) throw new KeyNotFoundException("Document not found.");
        return await repository.FindAsync(context.TenantId, id, ct)
            ?? throw new KeyNotFoundException("Document not found.");
    }

    private void RequireRead()
    {
        if (!context.HasScope("peritial.read") && !context.HasScope("peritial.write"))
            throw new UnauthorizedAccessException("Insufficient OAuth scope.");
    }
}
