using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class DocumentCatalogTests
{
    private sealed class Actor(TenantId tenant, bool read, bool write) : ITenantContext
    {
        public TenantId TenantId => tenant;
        public string Subject => "signed-actor";
        public bool HasScope(string scope) =>
            (read && scope == "peritial.read") || (write && scope == "peritial.write");
    }

    private sealed class Catalog : IDocumentCatalogRepository
    {
        public Dictionary<Guid, (TenantId Tenant, DocumentSummaryDto Document)> Items { get; } = [];
        public int RequestedLimit { get; private set; }
        public Task<IReadOnlyList<DocumentSummaryDto>> ListAsync(
            TenantId tenant, Guid? periziaId, int limit, CancellationToken ct = default)
        {
            RequestedLimit = limit;
            return Task.FromResult<IReadOnlyList<DocumentSummaryDto>>(Items.Values
                .Where(x => x.Tenant == tenant && (!periziaId.HasValue || x.Document.PeriziaId == periziaId))
                .Select(x => x.Document)
                .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
                .Take(limit).ToArray());
        }

        public Task<DocumentSummaryDto?> FindAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            Task.FromResult(Items.GetValueOrDefault(id) is var row && row.Tenant == tenant
                ? row.Document : null);
    }

    private static DocumentSummaryDto Record(Guid? periziaId = null) =>
        new(Guid.NewGuid(), periziaId, "tenant-file.pdf", "application/pdf", 123, DateTime.UtcNow);

    [Fact]
    public async Task Read_only_and_write_scopes_may_list_and_get_metadata_without_storage_secrets()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var repository = new Catalog();
        var doc = Record();
        repository.Items.Add(doc.Id, (tenant, doc));

        foreach (var actor in new[] {new Actor(tenant, true, false), new Actor(tenant, false, true)})
        {
            var service = new DocumentCatalogService(actor, repository);
            Assert.Equal(doc, Assert.Single(await service.ListAsync()));
            Assert.Equal(50, repository.RequestedLimit);
            Assert.Equal(doc, await service.GetAsync(doc.Id));
        }

        Assert.DoesNotContain(typeof(DocumentSummaryDto).GetProperties(),
            prop => prop.Name.Contains("Storage", StringComparison.OrdinalIgnoreCase)
                || prop.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)
                || prop.Name.Contains("Tenant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Foreign_and_missing_document_ids_are_indistinguishable_and_no_scope_denied()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var other = TenantId.From(Guid.NewGuid());
        var repo = new Catalog();
        var doc = Record();
        repo.Items.Add(doc.Id, (other, doc));
        var reader = new DocumentCatalogService(new Actor(tenant, true, false), repo);
        Assert.Empty(await reader.ListAsync());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => reader.GetAsync(doc.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => reader.GetAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => reader.GetAsync(Guid.Empty));

        var noScope = new DocumentCatalogService(new Actor(tenant, false, false), repo);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => noScope.ListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => noScope.GetAsync(doc.Id));
    }

    [Fact]
    public async Task List_is_bounded_and_filtered_by_perizia()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var periziaId = Guid.NewGuid();
        var repo = new Catalog();
        var wanted = Record(periziaId);
        var unrelated = Record(Guid.NewGuid());
        repo.Items.Add(wanted.Id, (tenant, wanted));
        repo.Items.Add(unrelated.Id, (tenant, unrelated));
        var service = new DocumentCatalogService(new Actor(tenant, true, false), repo);

        Assert.Equal(wanted, Assert.Single(await service.ListAsync(periziaId, 1)));
        Assert.Equal(1, repo.RequestedLimit);
        Assert.Equal(2, (await service.ListAsync(limit: 100)).Count);
        Assert.Equal(100, repo.RequestedLimit);
        foreach (var invalid in new[] {0, -1, 101, int.MaxValue})
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => service.ListAsync(limit: invalid));
    }
}
