using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class SecurityAuditUseCaseTests
{
    [Fact]
    public async Task Successful_mutations_are_audited_without_reference_or_payload()
    {
        var tenant = new ActingTenant(true);
        var audit = new SpyAudit();
        var perizie = new MemoryPerizie(audit);
        var service = new PeriziaService(tenant, perizie, audit);

        var created = await service.CreateAsync("PRIVATE-CLAIM-REF");
        await service.AssignAsync(created.Id, "PRIVATE-ASSIGNEE");
        await service.AdvanceAsync(created.Id, PeriziaStatus.ToBeRevised);
        var cancelled = await service.CancelAsync(created.Id);
        Assert.Equal(PeriziaStatus.Cancelled.ToString(), cancelled.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(created.Id));

        Assert.Equal(new[] { "perizia.create", "perizia.assign", "perizia.advance", "perizia.cancel" },
            audit.Events.Select(e => e.Action));
        Assert.All(audit.Events, e =>
        {
            Assert.Equal(tenant.TenantId, e.TenantId);
            Assert.Equal(tenant.Subject, e.Subject);
            Assert.Equal("succeeded", e.Outcome);
            Assert.Equal(created.Id, e.ResourceId);
            Assert.DoesNotContain("PRIVATE", e.ToString());
        });
    }

    [Fact]
    public async Task Get_perizia_allows_read_only_or_write_scope_without_mutation_audit()
    {
        foreach (var actor in new[] { new ActingTenant(false, true), new ActingTenant(true) })
        {
            var audit = new SpyAudit();
            var perizie = new MemoryPerizie(audit);
            var owned = Perizia.Create(actor.TenantId, "PRIVATE-READ");
            perizie.Items.Add(owned.Id, owned);
            var service = new PeriziaService(actor, perizie, audit);

            var found = await service.GetAsync(owned.Id);
            Assert.Equal(owned.Id, found.Id);
            Assert.Equal("PRIVATE-READ", found.Reference);
            Assert.Equal(PeriziaStatus.Registered.ToString(), found.Status);
            Assert.Empty(audit.Events);
            Assert.Equal(PeriziaStatus.Registered, perizie.Items[owned.Id].Status);
        }
    }

    [Fact]
    public async Task Get_perizia_denies_missing_scope_and_hides_missing_or_foreign_tenant_ids()
    {
        var readonlyActor = new ActingTenant(false, true);
        var audit = new SpyAudit();
        var perizie = new MemoryPerizie(audit);
        var foreign = Perizia.Create(TenantId.From(Guid.NewGuid()), "FOREIGN");
        perizie.Items.Add(foreign.Id, foreign);
        var service = new PeriziaService(readonlyActor, perizie, audit);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAsync(foreign.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAsync(Guid.Empty));
        Assert.Empty(audit.Events);

        var unscoped = new PeriziaService(new ActingTenant(false), perizie, audit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => unscoped.GetAsync(foreign.Id));
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Denied_mcp_mutation_is_recorded_and_does_not_change_data()
    {
        var actor = new ActingTenant(false);
        var audit = new SpyAudit();
        var perizie = new MemoryPerizie(audit);
        var service = new PeriziaService(actor, perizie, audit);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync("PRIVATE-DENIED"));
        Assert.Empty(perizie.Items);
        var entry = Assert.Single(audit.Events);
        Assert.Equal("perizia.create", entry.Action);
        Assert.Equal("denied", entry.Outcome);
        Assert.Null(entry.ResourceId);
        Assert.DoesNotContain("PRIVATE", entry.ToString());
    }

    private sealed class ActingTenant(bool allowWrite, bool allowRead = false) : ITenantContext
    {
        public TenantId TenantId { get; } = TenantId.From(Guid.NewGuid());
        public string Subject => "test-subject";
        public bool HasScope(string scope) => (allowWrite && scope == "peritial.write") ||
            (allowRead && scope == "peritial.read");
    }

    private sealed class MemoryPerizie(SpyAudit audit) : IPeriziaRepository
    {
        public Dictionary<Guid, Perizia> Items { get; } = [];
        public Task<IReadOnlyList<Perizia>> ListAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Perizia>>(Items.Values.Where(x => x.TenantId == tenant).ToArray());
        public Task<Perizia?> FindAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            Task.FromResult(Items.GetValueOrDefault(id) is { } item && item.TenantId == tenant ? item : null);
        public Task AddAsync(Perizia item, CancellationToken ct = default)
        {
            Items.Add(item.Id, item);
            return Task.CompletedTask;
        }
        public Task UpdateAsync(Perizia item, CancellationToken ct = default)
        {
            Items[item.Id] = item;
            return Task.CompletedTask;
        }

        public async Task AddAuditedAsync(Perizia item, SecurityAuditEvent entry, CancellationToken ct = default)
        {
            await audit.AppendAsync(entry, ct);
            await AddAsync(item, ct);
        }

        public async Task UpdateAuditedAsync(Perizia item, SecurityAuditEvent entry, CancellationToken ct = default)
        {
            await audit.AppendAsync(entry, ct);
            await UpdateAsync(item, ct);
        }
    }

    private sealed class SpyAudit : ISecurityAuditTrail
    {
        public List<SecurityAuditEvent> Events { get; } = [];
        public Task AppendAsync(SecurityAuditEvent entry, CancellationToken ct = default)
        {
            Events.Add(entry);
            return Task.CompletedTask;
        }
    }
}
