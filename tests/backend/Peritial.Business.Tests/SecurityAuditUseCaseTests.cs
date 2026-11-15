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
        var perizie = new MemoryPerizie();
        var audit = new SpyAudit();
        var service = new PeriziaService(tenant, perizie, audit);

        var created = await service.CreateAsync("PRIVATE-CLAIM-REF");
        await service.AssignAsync(created.Id, "PRIVATE-ASSIGNEE");
        await service.AdvanceAsync(created.Id, PeriziaStatus.ToBeRevised);

        Assert.Equal(new[] { "perizia.create", "perizia.assign", "perizia.advance" },
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
    public async Task Denied_mcp_mutation_is_recorded_and_does_not_change_data()
    {
        var actor = new ActingTenant(false);
        var perizie = new MemoryPerizie();
        var audit = new SpyAudit();
        var service = new PeriziaService(actor, perizie, audit);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync("PRIVATE-DENIED"));
        Assert.Empty(perizie.Items);
        var entry = Assert.Single(audit.Events);
        Assert.Equal("perizia.create", entry.Action);
        Assert.Equal("denied", entry.Outcome);
        Assert.Null(entry.ResourceId);
        Assert.DoesNotContain("PRIVATE", entry.ToString());
    }

    private sealed class ActingTenant(bool allowWrite) : ITenantContext
    {
        public TenantId TenantId { get; } = TenantId.From(Guid.NewGuid());
        public string Subject => "test-subject";
        public bool HasScope(string scope) => allowWrite && scope == "peritial.write";
    }

    private sealed class MemoryPerizie : IPeriziaRepository
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
