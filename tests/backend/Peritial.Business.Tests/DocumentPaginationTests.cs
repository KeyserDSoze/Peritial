using System.Text.Json;
using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class DocumentPaginationTests
{
    [Fact]
    public async Task Page_traverses_tenant_and_perizia_scoped_history_without_duplicates()
    {
        var actor = new Actor();
        var repo = new Catalog();
        var service = new DocumentCatalogService(actor, repo);
        var perizia = Guid.NewGuid();
        var other = Guid.NewGuid();
        var now = new DateTime(2026, 11, 19, 20, 0, 0, DateTimeKind.Utc);
        var ids = Enumerable.Range(1, 4).Select(i =>
            Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}")).ToArray();
        repo.Add(actor.TenantId, Record(ids[0], perizia, now));
        repo.Add(actor.TenantId, Record(ids[1], perizia, now));
        repo.Add(actor.TenantId, Record(ids[2], perizia, now.AddSeconds(-1)));
        repo.Add(actor.TenantId, Record(ids[3], perizia, now.AddSeconds(-2)));
        repo.Add(actor.TenantId, Record(Guid.NewGuid(), other, now.AddSeconds(3)));
        repo.Add(TenantId.From(Guid.NewGuid()),
            Record(Guid.NewGuid(), perizia, now.AddSeconds(4)));

        var first = await service.PageAsync(perizia, limit: 2);
        Assert.Equal(new[] { ids[1], ids[0] }, first.Items.Select(x => x.Id));
        Assert.NotNull(first.NextCursor);
        Assert.DoesNotContain("TenantId", JsonSerializer.Serialize(first));
        Assert.DoesNotContain("Storage", JsonSerializer.Serialize(first));

        // Newer INSERT cannot move the seek boundary to create duplicates.
        repo.Add(actor.TenantId, Record(Guid.NewGuid(), perizia, now.AddSeconds(5)));
        var second = await service.PageAsync(perizia, 2, first.NextCursor);
        Assert.Equal(new[] { ids[2], ids[3] }, second.Items.Select(x => x.Id));
        Assert.Null(second.NextCursor);
        Assert.Equal(2, repo.PageQueries);

        var unfiltered = await service.PageAsync(limit: 2);
        Assert.Equal(2, unfiltered.Items.Count);
        Assert.NotNull(unfiltered.NextCursor);
    }

    [Fact]
    public async Task Cursor_is_canonical_filter_bound_and_denied_before_query()
    {
        var actor = new Actor();
        var repo = new Catalog();
        var service = new DocumentCatalogService(actor, repo);
        var perizia = Guid.NewGuid();
        var boundary = new DocumentPageBoundary(
            new DateTime(2026, 11, 19, 20, 0, 0, DateTimeKind.Utc),
            Guid.Parse("00000000-0000-0000-0000-000000000003"));
        var encoded = DocumentPageCursor.Encode(boundary, perizia);
        Assert.Equal(boundary, DocumentPageCursor.Decode(encoded, perizia));
        Assert.Throws<ArgumentException>(() => DocumentPageCursor.Decode(encoded, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => DocumentPageCursor.Decode(encoded, null));
        Assert.Throws<ArgumentException>(() => DocumentPageCursor.Decode(encoded + "=" , perizia));
        Assert.Throws<ArgumentException>(() => DocumentPageCursor.Decode("bad!", perizia));
        Assert.Throws<ArgumentException>(() => DocumentPageCursor.Decode(new string('a', 500), perizia));
        Assert.Throws<ArgumentException>(() =>
            DocumentPageCursor.Encode(new DocumentPageBoundary(DateTime.SpecifyKind(boundary.CreatedAtUtc, DateTimeKind.Local), boundary.Id), perizia));

        foreach (var invalid in new[] {0, -1, 51, int.MaxValue})
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.PageAsync(perizia, invalid));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.PageAsync(perizia, 2, "invalid-cursor"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.PageAsync(Guid.NewGuid(), 2, encoded));
        Assert.Equal(0, repo.PageQueries);

        var forbidden = new DocumentCatalogService(new Actor(false), repo);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            forbidden.PageAsync(perizia));
        Assert.Equal(0, repo.PageQueries);
    }

    private sealed class Actor(bool allowed = true) : ITenantContext
    {
        public TenantId TenantId { get; } = TenantId.From(Guid.NewGuid());
        public string Subject => "signed-reader";
        public bool HasScope(string scope) => allowed && scope == "peritial.read";
    }

    private sealed class Catalog : IDocumentCatalogRepository
    {
        private readonly List<(TenantId Tenant, DocumentSummaryDto Document)> documents = [];
        public int PageQueries { get; private set; }
        public void Add(TenantId tenant, DocumentSummaryDto doc) => documents.Add((tenant, doc));

        public Task<IReadOnlyList<DocumentSummaryDto>> PageAsync(
            TenantId tenant, Guid? periziaId, DocumentPageBoundary? after,
            int fetchLimit, CancellationToken ct = default)
        {
            PageQueries++;
            return Task.FromResult<IReadOnlyList<DocumentSummaryDto>>(documents
                .Where(x => x.Tenant == tenant &&
                    (!periziaId.HasValue || x.Document.PeriziaId == periziaId) &&
                    (after is null || x.Document.CreatedAtUtc < after.CreatedAtUtc ||
                        (x.Document.CreatedAtUtc == after.CreatedAtUtc &&
                            x.Document.Id.CompareTo(after.Id) < 0)))
                .Select(x => x.Document)
                .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
                .Take(fetchLimit).ToArray());
        }

        public Task<IReadOnlyList<DocumentSummaryDto>> ListAsync(
            TenantId tenant, Guid? periziaId, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DocumentSummaryDto>>(documents
                .Where(x => x.Tenant == tenant && (!periziaId.HasValue || x.Document.PeriziaId == periziaId))
                .Select(x => x.Document).OrderByDescending(x => x.CreatedAtUtc)
                .ThenByDescending(x => x.Id).Take(limit).ToArray());

        public Task<DocumentSummaryDto?> FindAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            Task.FromResult(documents.Where(x => x.Tenant == tenant && x.Document.Id == id)
                .Select(x => x.Document).FirstOrDefault());
    }

    private static DocumentSummaryDto Record(Guid id, Guid? periziaId, DateTime when) =>
        new(id, periziaId, "safe.pdf", "application/pdf", 42, when);
}
