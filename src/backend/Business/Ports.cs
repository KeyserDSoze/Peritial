using Peritial.Domains;

namespace Peritial.Business;

public interface ITenantContext
{
    TenantId TenantId { get; }
    string Subject { get; }
    bool HasScope(string scope);
}

public interface IPeriziaRepository
{
    Task<IReadOnlyList<Perizia>> ListAsync(TenantId tenant, CancellationToken cancellationToken = default);
    Task<Perizia?> FindAsync(TenantId tenant, Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(Perizia perizia, CancellationToken cancellationToken = default);
    Task UpdateAsync(Perizia perizia, CancellationToken cancellationToken = default);
    // These operations MUST write perizia + success audit in the same database
    // transaction. Separate AddAsync/UpdateAsync are legacy/test-only seams.
    Task AddAuditedAsync(Perizia perizia, SecurityAuditEvent audit, CancellationToken cancellationToken = default);
    Task UpdateAuditedAsync(Perizia perizia, SecurityAuditEvent audit, CancellationToken cancellationToken = default);
}

/// <summary>
/// V004: optional one-to-many Sinistro -> Perizie relation. The repository
/// verifies both endpoints in the signed tenant, uses SQL CAS for the link,
/// and commits the link and its successful security audit atomically.
/// </summary>
public sealed record PeriziaSinistroState(Guid? SinistroId, long LinkVersion);

public interface IPeriziaSinistroLinkRepository
{
    // Explicit compare-and-swap unlink: the association still must match
    // the caller's last observed Sinistro UUID. Audit and mutation commit
    // atomically; missing/foreign perizia yields 404, stale/unlinked 409.
    // Atomic reparent never creates a transient null association. A stale
    // source produces 409 and the new target must exist in the signed tenant.
    // Every write requires a positive last-seen revision and returns the
    // increased revision. An unchanged UUID after an ABA cycle is NOT enough.
    // All three operations must append both audit and revision-history rows
    // in the same transaction as the versioned CAS SQL mutation.
    Task<long> ReparentAuditedAsync(TenantId tenant, Guid periziaId,
        Guid expectedSinistroId, Guid newSinistroId, long expectedLinkVersion,
        SecurityAuditEvent audit, CancellationToken ct = default);
    Task<long> UnlinkAuditedAsync(TenantId tenant, Guid periziaId, Guid expectedSinistroId,
        long expectedLinkVersion, SecurityAuditEvent audit, CancellationToken ct = default);
    Task<long> LinkAuditedAsync(TenantId tenant, Guid periziaId, Guid sinistroId,
        long expectedLinkVersion, SecurityAuditEvent audit, CancellationToken ct = default);
    Task<PeriziaSinistroState> ReadAssociationAsync(TenantId tenant, Guid periziaId,
        CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListPeriziaIdsAsync(TenantId tenant, Guid sinistroId,
        CancellationToken ct = default);
    // Null means a visible unlinked perizia; missing/foreign IDs throw 404.
    Task<Guid?> FindSinistroIdForPeriziaAsync(TenantId tenant, Guid periziaId,
        CancellationToken ct = default);
}

/// <summary>Tenant-filtered sinistri store. All mutations include successful audit in one database transaction.</summary>
public interface ISinistroRepository
{
    Task<IReadOnlyList<Sinistro>> ListAsync(TenantId tenant, int limit = 50, CancellationToken ct = default);
    // Deterministic keyset page. Fetch limit+1 to detect a next page;
    // never use OFFSET over a mutable tenant catalog.
    Task<IReadOnlyList<Sinistro>> PageAsync(TenantId tenant,
        SinistroPageBoundary? after, int fetchLimit, CancellationToken ct = default);
    Task<Sinistro?> FindAsync(TenantId tenant, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Sinistro>> SearchAsync(TenantId tenant, string prefix,
        DateOnly? occurredFrom = null, DateOnly? occurredTo = null, int limit = 10,
        CancellationToken ct = default);

    // Search filters and the keyset boundary are evaluated in PostgreSQL
    // before LIMIT. fetchLimit includes exactly one lookahead row.
    Task<IReadOnlyList<Sinistro>> SearchPageAsync(TenantId tenant,
        SinistroSearchFilter filter, SinistroPageBoundary? after,
        int fetchLimit, CancellationToken ct = default);

    Task DeleteAuditedAsync(TenantId tenant, Guid id, long expectedVersion,
        SecurityAuditEvent audit, CancellationToken ct = default);
    Task AddAuditedAsync(Sinistro sinistro, SecurityAuditEvent audit, CancellationToken ct = default);
    Task UpdateAuditedAsync(Sinistro sinistro, SecurityAuditEvent audit, CancellationToken ct = default);
}

public interface IObjectStore
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);
    // Idempotent removal of a possibly partially uploaded object, for compensation.
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public interface IDocumentRepository
{
    Task AddAsync(DocumentRecord record, CancellationToken cancellationToken = default);
    // A successful ingestion MUST commit metadata + audit in the same PG transaction.
    Task AddAuditedAsync(DocumentRecord record, SecurityAuditEvent audit, CancellationToken cancellationToken = default);
}

public sealed record DocumentRecord(
    Guid Id, TenantId TenantId, Guid? PeriziaId, string FileName, string ContentType,
    long Length, string StorageKey, DateTime CreatedAtUtc, bool RequiresIdempotency = false);


/// <summary>Durable reservation before PUT, not a client-provided storage key.</summary>
public interface IUploadIntentLedger
{
    Task RecordAsync(TenantId tenant, Guid documentId, string storageKey, CancellationToken ct = default);
    // Returns false if intent was removed/expired. Never resurrect an expired lease.
    Task<bool> RenewAsync(TenantId tenant, Guid documentId, CancellationToken ct = default);
    Task ForgetAsync(TenantId tenant, Guid documentId, CancellationToken ct = default);
}
