using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class PeriziaSinistroLinkServiceTests
{
    [Fact]
    public async Task Signed_writer_links_once_and_reader_only_sees_own_sinistro()
    {
        var tenantA = TenantId.From(Guid.NewGuid());
        var tenantB = TenantId.From(Guid.NewGuid());
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var otherTarget = Guid.NewGuid();
        var audit = new SpyAudit();
        var repo = new FakeLinks(audit);
        repo.OwnedSinistri[target] = tenantA;
        repo.OwnedSinistri[otherTarget] = tenantB;
        repo.OwnedPerizie[source] = tenantA;
        var writer = new PeriziaSinistroLinkService(new Actor(tenantA, write: true), repo, audit);
        var reader = new PeriziaSinistroLinkService(new Actor(tenantA, read: true), repo, audit);

        var result = await writer.LinkAsync(source, target);
        Assert.Equal(source, result.PeriziaId);
        Assert.Equal(target, result.SinistroId);
        Assert.Equal(new[] {source}, await reader.ListPeriziaIdsAsync(target));
        Assert.DoesNotContain("TenantId", System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Single(audit.Events);
        Assert.Equal("perizia.link-sinistro", audit.Events[0].Action);
        Assert.Equal("succeeded", audit.Events[0].Outcome);
        Assert.Equal(source, audit.Events[0].ResourceId);
        Assert.Equal(tenantA, audit.Events[0].TenantId);

        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            writer.LinkAsync(source, target));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            writer.LinkAsync(source, otherTarget));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            reader.ListPeriziaIdsAsync(otherTarget));
        Assert.Single(audit.Events);
    }

    [Fact]
    public async Task Missing_write_scope_is_audited_before_any_write_and_missing_read_scope_is_denied()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var perizia = Guid.NewGuid();
        var sinistro = Guid.NewGuid();
        var audit = new SpyAudit();
        var repo = new FakeLinks(audit);
        repo.OwnedPerizie[perizia] = tenant;
        repo.OwnedSinistri[sinistro] = tenant;
        var readonlyService = new PeriziaSinistroLinkService(new Actor(tenant, read: true), repo, audit);
        var noScopeService = new PeriziaSinistroLinkService(new Actor(tenant), repo, audit);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            readonlyService.LinkAsync(perizia, sinistro));
        Assert.Single(audit.Events);
        Assert.Equal("denied", audit.Events[0].Outcome);
        Assert.Null(audit.Events[0].ResourceId);
        Assert.Empty(await readonlyService.ListPeriziaIdsAsync(sinistro));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            noScopeService.ListPeriziaIdsAsync(sinistro));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new PeriziaSinistroLinkService(new Actor(tenant, write: true), repo, audit)
                .LinkAsync(Guid.Empty, sinistro));
        Assert.False(repo.Linked.ContainsKey(perizia));
        Assert.Single(audit.Events);
    }

    private sealed class Actor(TenantId tenant, bool read = false, bool write = false) : ITenantContext
    {
        public TenantId TenantId => tenant;
        public string Subject => "signed-actor";
        public bool HasScope(string scope) => scope == "peritial.read" && read ||
                                              scope == "peritial.write" && write;
    }

    private sealed class SpyAudit : ISecurityAuditTrail
    {
        public List<SecurityAuditEvent> Events { get; } = [];
        public Task AppendAsync(SecurityAuditEvent value, CancellationToken ct = default)
        {
            Events.Add(value);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLinks(SpyAudit audit) : IPeriziaSinistroLinkRepository
    {
        public Dictionary<Guid,TenantId> OwnedPerizie { get; } = [];
        public Dictionary<Guid,TenantId> OwnedSinistri { get; } = [];
        public Dictionary<Guid,Guid> Linked { get; } = [];

        public async Task LinkAuditedAsync(TenantId tenant, Guid periziaId, Guid sinistroId,
            SecurityAuditEvent entry, CancellationToken ct = default)
        {
            if (!OwnedSinistri.TryGetValue(sinistroId, out var st) || st != tenant ||
                !OwnedPerizie.TryGetValue(periziaId, out var pt) || pt != tenant)
                throw new KeyNotFoundException("Resource not found.");
            if (Linked.ContainsKey(periziaId))
                throw new PeriziaConcurrencyConflictException();
            await audit.AppendAsync(entry, ct);
            Linked[periziaId] = sinistroId;
        }

        public Task<IReadOnlyList<Guid>> ListPeriziaIdsAsync(TenantId tenant, Guid sinistroId,
            CancellationToken ct = default)
        {
            if (!OwnedSinistri.TryGetValue(sinistroId, out var st) || st != tenant)
                throw new KeyNotFoundException("Resource not found.");
            return Task.FromResult<IReadOnlyList<Guid>>(Linked
                .Where(x => x.Value == sinistroId && OwnedPerizie[x.Key] == tenant)
                .Select(x => x.Key).OrderBy(x => x).ToArray());
        }
    }
}
