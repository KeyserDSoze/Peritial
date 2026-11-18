using System.Text.Json;
using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

/// <summary>Keyset paging is a scoped read, not a client-side offset or bearer credential.</summary>
public sealed class SinistroPaginationTests
{
    [Fact]
    public async Task Page_traverses_stable_date_and_id_order_without_cross_tenant_rows_or_audit()
    {
        var actor = new Reader();
        var repository = new Cases();
        var audit = new Audit();
        var service = new SinistroService(actor, repository, audit);
        var date = new DateOnly(2026, 11, 18);
        var ids = Enumerable.Range(1, 4).Select(i =>
            Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}")).ToArray();
        repository.Items.Add(Sinistro.Restore(ids[2], actor.TenantId, "THIRD", date, null, DateTime.UtcNow));
        repository.Items.Add(Sinistro.Restore(ids[0], actor.TenantId, "FIRST", date, null, DateTime.UtcNow));
        repository.Items.Add(Sinistro.Restore(ids[3], actor.TenantId, "OLD", date.AddDays(-1), null, DateTime.UtcNow));
        repository.Items.Add(Sinistro.Restore(ids[1], actor.TenantId, "SECOND", date, null, DateTime.UtcNow));
        repository.Items.Add(Sinistro.Create(TenantId.From(Guid.NewGuid()), "FOREIGN", date.AddDays(5)));

        var first = await service.PageAsync(limit: 2);
        Assert.Equal(new[] {ids[0], ids[1]}, first.Items.Select(i => i.Id));
        Assert.NotNull(first.NextCursor);
        Assert.DoesNotContain("TenantId", JsonSerializer.Serialize(first));

        // Newer inserts between requests cannot shift an offset and duplicate rows.
        repository.Items.Add(Sinistro.Create(actor.TenantId, "NEWER", date.AddDays(1)));
        var second = await service.PageAsync(limit: 2, cursor: first.NextCursor);
        Assert.Equal(new[] {ids[2], ids[3]}, second.Items.Select(i => i.Id));
        Assert.Null(second.NextCursor);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Cursor_must_be_canonical_and_page_validated_before_database_access()
    {
        var actor = new Reader();
        var repository = new Cases();
        var service = new SinistroService(actor, repository, new Audit());
        var boundary = new SinistroPageBoundary(new DateOnly(2026, 11, 18),
            Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var encoded = SinistroPageCursor.Encode(boundary);
        Assert.Equal(boundary, SinistroPageCursor.Decode(encoded));
        Assert.Throws<ArgumentException>(() => SinistroPageCursor.Decode(encoded + "="));
        Assert.Throws<ArgumentException>(() => SinistroPageCursor.Decode("a-b_c"));
        Assert.Throws<ArgumentException>(() => SinistroPageCursor.Decode(new string('A', 200)));
        Assert.Throws<ArgumentException>(() => SinistroPageCursor.Decode(""));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.PageAsync(limit: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.PageAsync(limit: 51));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PageAsync(cursor: "not-a-cursor"));
        Assert.Equal(0, repository.PageQueries);

        var forbidden = new SinistroService(new Reader(false), repository, new Audit());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => forbidden.PageAsync(limit: 2));
        Assert.Equal(0, repository.PageQueries);
    }

    private sealed class Reader(bool allowed = true) : ITenantContext
    {
        public TenantId TenantId { get; } = TenantId.From(Guid.NewGuid());
        public string Subject => "signed-reader";
        public bool HasScope(string scope) => allowed && scope == "peritial.read";
    }

    private sealed class Audit : ISecurityAuditTrail
    {
        public List<SecurityAuditEvent> Events { get; } = [];
        public Task AppendAsync(SecurityAuditEvent value, CancellationToken ct = default)
        {
            Events.Add(value);
            return Task.CompletedTask;
        }
    }

    private sealed class Cases : ISinistroRepository
    {
        public List<Sinistro> Items { get; } = [];
        public int PageQueries { get; private set; }
        public Task<IReadOnlyList<Sinistro>> PageAsync(TenantId tenant,
            SinistroPageBoundary? after, int fetchLimit, CancellationToken ct = default)
        {
            PageQueries++;
            return Task.FromResult<IReadOnlyList<Sinistro>>(Items
                .Where(i => i.TenantId == tenant && (after is null ||
                    i.OccurredOn < after.OccurredOn ||
                    (i.OccurredOn == after.OccurredOn && i.Id.CompareTo(after.Id) > 0)))
                .OrderByDescending(i => i.OccurredOn).ThenBy(i => i.Id)
                .Take(fetchLimit).ToArray());
        }
        public Task<IReadOnlyList<Sinistro>> ListAsync(TenantId tenant,
            int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Sinistro>>(Items.Where(i => i.TenantId == tenant)
                .Take(limit).ToArray());
        public Task<IReadOnlyList<Sinistro>> SearchAsync(TenantId tenant, string prefix,
            DateOnly? occurredFrom = null, DateOnly? occurredTo = null, int limit = 10,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Sinistro>>(Array.Empty<Sinistro>());
        public Task<Sinistro?> FindAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            Task.FromResult(Items.FirstOrDefault(i => i.TenantId == tenant && i.Id == id));
        public Task AddAuditedAsync(Sinistro item, SecurityAuditEvent audit, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateAuditedAsync(Sinistro item, SecurityAuditEvent audit, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
