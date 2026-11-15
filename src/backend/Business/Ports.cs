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
}

public interface IObjectStore
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);
}

public interface IDocumentRepository
{
    Task AddAsync(DocumentRecord record, CancellationToken cancellationToken = default);
}

public sealed record DocumentRecord(
    Guid Id, TenantId TenantId, Guid? PeriziaId, string FileName, string ContentType,
    long Length, string StorageKey, DateTime CreatedAtUtc);
