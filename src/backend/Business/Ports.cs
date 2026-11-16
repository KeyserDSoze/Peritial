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

/// <summary>Tenant-filtered sinistri store. All mutations include successful audit in one database transaction.</summary>
public interface ISinistroRepository
{
    Task<IReadOnlyList<Sinistro>> ListAsync(TenantId tenant, int limit = 50, CancellationToken ct = default);
    Task<Sinistro?> FindAsync(TenantId tenant, Guid id, CancellationToken ct = default);
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
