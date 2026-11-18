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

        var result = await writer.LinkAsync(source, target, 1);
        Assert.Equal(source, result.PeriziaId);
        Assert.Equal(target, result.SinistroId);
        Assert.Equal(2, result.LinkVersion);
        Assert.Equal(new[] {source}, await reader.ListPeriziaIdsAsync(target));
        Assert.DoesNotContain("TenantId", System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Single(audit.Events);
        Assert.Equal("perizia.link-sinistro", audit.Events[0].Action);
        Assert.Equal("succeeded", audit.Events[0].Outcome);
        Assert.Equal(source, audit.Events[0].ResourceId);
        Assert.Equal(tenantA, audit.Events[0].TenantId);

        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            writer.LinkAsync(source, target, 1));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            writer.LinkAsync(source, otherTarget, 1));
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
            readonlyService.LinkAsync(perizia, sinistro, 1));
        Assert.Single(audit.Events);
        Assert.Equal("denied", audit.Events[0].Outcome);
        Assert.Null(audit.Events[0].ResourceId);
        Assert.Empty(await readonlyService.ListPeriziaIdsAsync(sinistro));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            noScopeService.ListPeriziaIdsAsync(sinistro));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new PeriziaSinistroLinkService(new Actor(tenant, write: true), repo, audit)
                .LinkAsync(Guid.Empty, sinistro, 1));
        Assert.False(repo.Linked.ContainsKey(perizia));
        Assert.Single(audit.Events);
    }

    [Fact]
    public async Task Signed_readers_can_check_link_state_without_enumerating_foreign_perizie_or_auditing()
    {
        var tenantA = TenantId.From(Guid.NewGuid());
        var tenantB = TenantId.From(Guid.NewGuid());
        var own = Guid.NewGuid();
        var unlinked = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var sinistro = Guid.NewGuid();
        var audit = new SpyAudit();
        var repo = new FakeLinks(audit);
        repo.OwnedPerizie[own] = tenantA;
        repo.OwnedPerizie[unlinked] = tenantA;
        repo.OwnedPerizie[foreign] = tenantB;
        repo.OwnedSinistri[sinistro] = tenantA;
        repo.Linked[own] = sinistro;
        var reader = new PeriziaSinistroLinkService(new Actor(tenantA, read: true), repo, audit);
        var writer = new PeriziaSinistroLinkService(new Actor(tenantA, write: true), repo, audit);

        var linked = await reader.GetPeriziaAssociationAsync(own);
        Assert.Equal(new PeriziaSinistroAssociationDto(own, sinistro, 1), linked);
        Assert.Equal(new PeriziaSinistroAssociationDto(unlinked, null, 1),
            await writer.GetPeriziaAssociationAsync(unlinked));
        Assert.DoesNotContain("TenantId", System.Text.Json.JsonSerializer.Serialize(linked));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => reader.GetPeriziaAssociationAsync(foreign));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => reader.GetPeriziaAssociationAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new PeriziaSinistroLinkService(new Actor(tenantA), repo, audit)
                .GetPeriziaAssociationAsync(own));
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Unlink_requires_last_observed_sinistro_and_writer_scope_then_allows_explicit_relink()
    {
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var perizia = Guid.NewGuid();
        var oldSinistro = Guid.NewGuid();
        var nextSinistro = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var audit = new SpyAudit();
        var repo = new FakeLinks(audit);
        repo.OwnedPerizie[perizia] = a;
        repo.OwnedSinistri[oldSinistro] = a;
        repo.OwnedSinistri[nextSinistro] = a;
        repo.OwnedSinistri[foreign] = b;
        var writer = new PeriziaSinistroLinkService(new Actor(a, write: true), repo, audit);
        var reader = new PeriziaSinistroLinkService(new Actor(a, read: true), repo, audit);
        await writer.LinkAsync(perizia, oldSinistro, 1);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            reader.UnlinkAsync(perizia, oldSinistro, 2));
        Assert.Equal("perizia.unlink-sinistro", audit.Events[^1].Action);
        Assert.Equal("denied", audit.Events[^1].Outcome);
        Assert.Null(audit.Events[^1].ResourceId);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.UnlinkAsync(Guid.Empty, oldSinistro, 2));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.UnlinkAsync(perizia, Guid.Empty, 2));
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            writer.UnlinkAsync(perizia, nextSinistro, 2));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            writer.UnlinkAsync(Guid.NewGuid(), oldSinistro, 2));
        Assert.Equal(oldSinistro, (await reader.GetPeriziaAssociationAsync(perizia)).SinistroId);
        Assert.Single(audit.Events.Where(e => e.Action == "perizia.link-sinistro"));

        var removed = await writer.UnlinkAsync(perizia, oldSinistro, 2);
        Assert.Equal(new PeriziaSinistroAssociationDto(perizia, null, 3), removed);
        Assert.DoesNotContain("tenantId", System.Text.Json.JsonSerializer.Serialize(removed),
            StringComparison.OrdinalIgnoreCase);
        Assert.Null((await reader.GetPeriziaAssociationAsync(perizia)).SinistroId);
        Assert.Equal("perizia.unlink-sinistro", audit.Events[^1].Action);
        Assert.Equal("succeeded", audit.Events[^1].Outcome);
        Assert.Equal(perizia, audit.Events[^1].ResourceId);
        Assert.Equal(a, audit.Events[^1].TenantId);
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            writer.UnlinkAsync(perizia, oldSinistro, 2));
        await writer.LinkAsync(perizia, nextSinistro, 3);
        Assert.Equal(nextSinistro, (await writer.GetPeriziaAssociationAsync(perizia)).SinistroId);
        Assert.Equal(1, audit.Events.Count(e =>
            e.Action == "perizia.unlink-sinistro" && e.Outcome == "succeeded"));

        var otherTenant = new PeriziaSinistroLinkService(new Actor(b, write: true), repo, audit);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            otherTenant.UnlinkAsync(perizia, nextSinistro, 4));
        Assert.Equal(nextSinistro, (await writer.GetPeriziaAssociationAsync(perizia)).SinistroId);
    }

    [Fact]
    public async Task Reparent_changes_only_observed_link_with_signed_writer_scope_and_atomic_audit()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var other = TenantId.From(Guid.NewGuid());
        var periziaId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        var audit = new SpyAudit();
        var links = new FakeLinks(audit);
        links.OwnedPerizie[periziaId] = tenant;
        links.OwnedSinistri[sourceId] = tenant;
        links.OwnedSinistri[targetId] = tenant;
        links.OwnedSinistri[foreignId] = other;
        var writer = new PeriziaSinistroLinkService(new Actor(tenant, write: true), links, audit);
        var reader = new PeriziaSinistroLinkService(new Actor(tenant, read: true), links, audit);
        await writer.LinkAsync(periziaId, sourceId, 1);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            reader.ReparentAsync(periziaId, sourceId, targetId, 2));
        Assert.Equal("perizia.reparent-sinistro", audit.Events[^1].Action);
        Assert.Equal("denied", audit.Events[^1].Outcome);
        Assert.Null(audit.Events[^1].ResourceId);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.ReparentAsync(Guid.Empty, sourceId, targetId, 2));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.ReparentAsync(periziaId, Guid.Empty, targetId, 2));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.ReparentAsync(periziaId, sourceId, Guid.Empty, 2));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.ReparentAsync(periziaId, sourceId, sourceId, 2));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            writer.ReparentAsync(periziaId, sourceId, foreignId, 2));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            writer.ReparentAsync(periziaId, sourceId, Guid.NewGuid(), 2));
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            writer.ReparentAsync(periziaId, targetId, sourceId, 2));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            writer.ReparentAsync(Guid.NewGuid(), sourceId, targetId, 2));
        Assert.Equal(sourceId, (await reader.GetPeriziaAssociationAsync(periziaId)).SinistroId);
        Assert.Equal(0, audit.Events.Count(x =>
            x.Action == "perizia.reparent-sinistro" && x.Outcome == "succeeded"));

        var moved = await writer.ReparentAsync(periziaId, sourceId, targetId, 2);
        Assert.Equal(new PeriziaSinistroLinkDto(periziaId, targetId, 3), moved);
        Assert.DoesNotContain("tenant", System.Text.Json.JsonSerializer.Serialize(moved),
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await reader.ListPeriziaIdsAsync(sourceId));
        Assert.Equal(new[] {periziaId}, await reader.ListPeriziaIdsAsync(targetId));
        Assert.Equal(targetId, (await reader.GetPeriziaAssociationAsync(periziaId)).SinistroId);
        var success = Assert.Single(audit.Events.Where(x =>
            x.Action == "perizia.reparent-sinistro" && x.Outcome == "succeeded"));
        Assert.Equal(tenant, success.TenantId);
        Assert.Equal(periziaId, success.ResourceId);
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            writer.ReparentAsync(periziaId, sourceId, targetId, 2));
        var foreignWriter = new PeriziaSinistroLinkService(new Actor(other, write: true), links, audit);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            foreignWriter.ReparentAsync(periziaId, targetId, foreignId, 3));
        Assert.Equal(targetId, (await reader.GetPeriziaAssociationAsync(periziaId)).SinistroId);
    }

    [Fact]
    public async Task V006_aba_rejects_stale_association_version_even_if_sinistro_returns_to_same_uuid()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var p = Guid.NewGuid();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var audit = new SpyAudit();
        var repo = new FakeLinks(audit);
        repo.OwnedPerizie[p] = tenant;
        repo.OwnedSinistri[source] = tenant;
        repo.OwnedSinistri[target] = tenant;
        var user1 = new PeriziaSinistroLinkService(new Actor(tenant, write:true), repo, audit);
        var user2 = new PeriziaSinistroLinkService(new Actor(tenant, write:true), repo, audit);

        var snapshot = await user1.GetPeriziaAssociationAsync(p);
        Assert.Equal(1, snapshot.LinkVersion);
        Assert.Null(snapshot.SinistroId);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            user1.LinkAsync(p, source, expectedLinkVersion:0));
        var first = await user1.LinkAsync(p, source, expectedLinkVersion:snapshot.LinkVersion);
        Assert.Equal(2, first.LinkVersion);
        var original = await user1.GetPeriziaAssociationAsync(p);
        Assert.Equal(source, original.SinistroId);

        var moved = await user2.ReparentAsync(p, source, target,
            expectedLinkVersion:first.LinkVersion);
        Assert.Equal(3, moved.LinkVersion);
        var movedBack = await user2.ReparentAsync(p, target, source,
            expectedLinkVersion:moved.LinkVersion);
        Assert.Equal(4, movedBack.LinkVersion);
        Assert.Equal(source, (await user1.GetPeriziaAssociationAsync(p)).SinistroId);

        // An old reader sees exactly the same UUID as before (ABA), but MUST
        // NOT be allowed to overwrite/unlink it with a stale link revision.
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            user1.UnlinkAsync(p, source, expectedLinkVersion:original.LinkVersion));
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            user1.ReparentAsync(p, source, target, expectedLinkVersion:original.LinkVersion));
        var current = await user1.GetPeriziaAssociationAsync(p);
        Assert.Equal(4, current.LinkVersion);
        Assert.Equal(source, current.SinistroId);
        var deleted = await user2.UnlinkAsync(p, source,
            expectedLinkVersion:current.LinkVersion);
        Assert.Equal(5, deleted.LinkVersion);
        Assert.Null(deleted.SinistroId);
        var linkedAgain = await user2.LinkAsync(p, target,
            expectedLinkVersion:deleted.LinkVersion);
        Assert.Equal(6, linkedAgain.LinkVersion);
        Assert.Equal(target, (await user1.GetPeriziaAssociationAsync(p)).SinistroId);
        Assert.Equal(5, audit.Events.Count(e => e.Outcome == "succeeded"));
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
        public Dictionary<Guid,long> Versions { get; } = [];
        private long CurrentVersion(Guid periziaId) => Versions.GetValueOrDefault(periziaId, 1);

        public async Task<long> ReparentAuditedAsync(TenantId tenant, Guid periziaId,
            Guid expectedSinistroId, Guid newSinistroId, long expectedLinkVersion,
            SecurityAuditEvent entry, CancellationToken ct = default)
        {
            if (!OwnedPerizie.TryGetValue(periziaId, out var periziaTenant) ||
                periziaTenant != tenant ||
                !OwnedSinistri.TryGetValue(newSinistroId, out var targetTenant) ||
                targetTenant != tenant)
                throw new KeyNotFoundException("Resource not found.");
            if (!Linked.TryGetValue(periziaId, out var old) || old != expectedSinistroId ||
                CurrentVersion(periziaId) != expectedLinkVersion)
                throw new PeriziaConcurrencyConflictException();
            await audit.AppendAsync(entry, ct);
            Linked[periziaId] = newSinistroId;
            return Versions[periziaId] = expectedLinkVersion + 1;
        }

        public async Task<long> UnlinkAuditedAsync(TenantId tenant, Guid periziaId,
            Guid expectedSinistroId, long expectedLinkVersion, SecurityAuditEvent entry,
            CancellationToken ct = default)
        {
            if (!OwnedPerizie.TryGetValue(periziaId, out var owner) || owner != tenant)
                throw new KeyNotFoundException("Perizia not found.");
            if (!Linked.TryGetValue(periziaId, out var actual) || actual != expectedSinistroId ||
                CurrentVersion(periziaId) != expectedLinkVersion)
                throw new PeriziaConcurrencyConflictException();
            await audit.AppendAsync(entry, ct);
            Linked.Remove(periziaId);
            return Versions[periziaId] = expectedLinkVersion + 1;
        }

        public async Task<Guid?> FindSinistroIdForPeriziaAsync(
            TenantId tenant, Guid periziaId, CancellationToken ct = default) =>
            (await ReadAssociationAsync(tenant, periziaId, ct)).SinistroId;

        public Task<PeriziaSinistroState> ReadAssociationAsync(
            TenantId tenant, Guid periziaId, CancellationToken ct = default)
        {
            if (!OwnedPerizie.TryGetValue(periziaId, out var owner) || owner != tenant)
                throw new KeyNotFoundException("Perizia not found.");
            return Task.FromResult(new PeriziaSinistroState(
                Linked.TryGetValue(periziaId, out var link) ? link : null,
                CurrentVersion(periziaId)));
        }

        public async Task<long> LinkAuditedAsync(TenantId tenant, Guid periziaId, Guid sinistroId,
            long expectedLinkVersion, SecurityAuditEvent entry, CancellationToken ct = default)
        {
            if (!OwnedSinistri.TryGetValue(sinistroId, out var st) || st != tenant ||
                !OwnedPerizie.TryGetValue(periziaId, out var pt) || pt != tenant)
                throw new KeyNotFoundException("Resource not found.");
            if (Linked.ContainsKey(periziaId) ||
                CurrentVersion(periziaId) != expectedLinkVersion)
                throw new PeriziaConcurrencyConflictException();
            await audit.AppendAsync(entry, ct);
            Linked[periziaId] = sinistroId;
            return Versions[periziaId] = expectedLinkVersion + 1;
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
