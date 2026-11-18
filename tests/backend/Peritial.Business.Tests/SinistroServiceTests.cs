using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class SinistroServiceTests
{
    [Fact]
    public async Task Create_and_update_use_signed_tenant_and_atomic_audit_without_leaking_tenant_in_DTO()
    {
        var actor = new ActingTenant(write: true);
        var audit = new SpyAudit();
        var repo = new InMemorySinistri(audit);
        var service = new SinistroService(actor, repo, audit);
        var date = new DateOnly(2026, 11, 9);
        var reported = new DateOnly(2026, 11, 10);

        var created = await service.CreateAsync("  PRIVATE-CASE  ", date, reported);
        Assert.Equal("PRIVATE-CASE", created.ExternalReference);
        Assert.Equal(1L, created.Version);
        Assert.Equal(actor.TenantId, repo.Saved[created.Id].TenantId);
        Assert.Equal(created.Id, (await service.ListAsync(20)).Single().Id);
        Assert.Equal(created.Id, (await service.GetAsync(created.Id)).Id);
        Assert.DoesNotContain("TenantId", System.Text.Json.JsonSerializer.Serialize(created));

        var updated = await service.UpdateAsync(created.Id, "CHANGED", date, null, created.Version);
        Assert.Equal("CHANGED", updated.ExternalReference);
        Assert.Equal(2L, updated.Version);
        Assert.Null(updated.ReportedOn);
        Assert.Equal(new[] {"sinistro.create", "sinistro.update"}, audit.Events.Select(x => x.Action));
        Assert.All(audit.Events, entry =>
        {
            Assert.Equal(actor.TenantId, entry.TenantId);
            Assert.Equal(actor.Subject, entry.Subject);
            Assert.Equal("succeeded", entry.Outcome);
            Assert.Equal(created.Id, entry.ResourceId);
            Assert.DoesNotContain("PRIVATE", entry.ToString());
        });
    }

    [Fact]
    public async Task Read_only_scope_can_read_but_denied_mutations_are_audited_and_do_not_write()
    {
        var actor = new ActingTenant(read: true);
        var audit = new SpyAudit();
        var repo = new InMemorySinistri(audit);
        var item = Sinistro.Create(actor.TenantId, "READ-ONLY", new DateOnly(2026, 1, 2));
        repo.Saved.Add(item.Id, item);
        var service = new SinistroService(actor, repo, audit);

        Assert.Single(await service.ListAsync());
        Assert.Equal(item.Id, (await service.GetAsync(item.Id)).Id);
        Assert.Empty(audit.Events);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreateAsync("DENIED", new DateOnly(2026, 1, 2)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UpdateAsync(item.Id, "DENIED", new DateOnly(2026, 1, 2), null, 1));
        Assert.Equal("READ-ONLY", repo.Saved[item.Id].ExternalReference);
        Assert.Equal(new[] {"sinistro.create", "sinistro.update"}, audit.Events.Select(x => x.Action));
        Assert.All(audit.Events, entry =>
        {
            Assert.Equal("denied", entry.Outcome);
            Assert.Null(entry.ResourceId);
        });
    }

    [Fact]
    public async Task Foreign_and_missing_sinistri_are_indistinguishable_and_wrong_scope_is_denied_before_lookup()
    {
        var actor = new ActingTenant(read: true);
        var audit = new SpyAudit();
        var repo = new InMemorySinistri(audit);
        var foreign = Sinistro.Create(TenantId.From(Guid.NewGuid()), "FOREIGN", new DateOnly(2026, 2, 2));
        repo.Saved.Add(foreign.Id, foreign);
        var service = new SinistroService(actor, repo, audit);
        Assert.Empty(await service.ListAsync());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAsync(foreign.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAsync(Guid.NewGuid()));
        Assert.Empty(audit.Events);

        var noScope = new SinistroService(new ActingTenant(), repo, audit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => noScope.ListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => noScope.GetAsync(foreign.Id));
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Stale_expected_version_and_invalid_limit_fail_before_mutating_or_auditing_success()
    {
        var actor = new ActingTenant(write: true);
        var audit = new SpyAudit();
        var repo = new InMemorySinistri(audit);
        var service = new SinistroService(actor, repo, audit);
        var created = await service.CreateAsync("ORIGINAL", new DateOnly(2026, 10, 1));

        await Assert.ThrowsAsync<SinistroConcurrencyConflictException>(() =>
            service.UpdateAsync(created.Id, "STALE", new DateOnly(2026, 10, 2), null, created.Version + 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListAsync(101));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.UpdateAsync(created.Id, "INVALID", new DateOnly(2026, 10, 2), null, 0));
        Assert.Equal("ORIGINAL", repo.Saved[created.Id].ExternalReference);
        Assert.Equal(new[] {"sinistro.create"}, audit.Events.Select(x => x.Action));
    }

    [Fact]
    public void Hard_delete_gate_is_denied_by_default_and_can_never_be_enabled_outside_development()
    {
        Assert.False(SinistroDeletionGate.Disabled.Enabled);
        Assert.False(SinistroDeletionGate.ForEnvironment(isDevelopment: true, enabled: false).Enabled);
        Assert.False(SinistroDeletionGate.ForEnvironment(isDevelopment: false, enabled: false).Enabled);
        Assert.True(SinistroDeletionGate.ForEnvironment(isDevelopment: true, enabled: true).Enabled);
        Assert.Throws<InvalidOperationException>(() =>
            SinistroDeletionGate.ForEnvironment(isDevelopment: false, enabled: true));
    }

    [Fact]
    public async Task Delete_denies_plain_writer_and_disabled_gate_with_safe_audit_before_repository_access()
    {
        var writer = new ActingTenant(write: true);
        var audit = new SpyAudit();
        var repo = new InMemorySinistri(audit);
        var writeService = new SinistroService(writer, repo, audit,
            SinistroDeletionGate.ForEnvironment(true, true));
        var item = await writeService.CreateAsync("DENY-WRITER", new DateOnly(2026, 11, 18));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            writeService.DeleteAsync(item.Id, item.Version));

        var admin = new ActingTenant(write: true, delete: true);
        var adminService = new SinistroService(admin, repo, audit);
        var adminItem = await adminService.CreateAsync("DENY-BY-DEFAULT", new DateOnly(2026, 11, 18));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adminService.DeleteAsync(adminItem.Id, adminItem.Version));

        var disabled = new SinistroService(admin, repo, audit,
            SinistroDeletionGate.ForEnvironment(true, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            disabled.DeleteAsync(adminItem.Id, adminItem.Version));

        Assert.Equal(item.Id, (await writeService.GetAsync(item.Id)).Id);
        Assert.Equal(adminItem.Id, (await adminService.GetAsync(adminItem.Id)).Id);
        Assert.Equal(3, audit.Events.Count(e =>
            e.Action == "sinistro.delete" && e.Outcome == "denied" && e.ResourceId is null));
        Assert.DoesNotContain(audit.Events, e =>
            e.Action == "sinistro.delete" && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task Delete_requires_signed_write_and_expected_version_and_preserves_audit()
    {
        var actor = new ActingTenant(write: true, delete: true);
        var audit = new SpyAudit();
        var repo = new InMemorySinistri(audit);
        var service = new SinistroService(actor, repo, audit, SinistroDeletionGate.ForEnvironment(true, true));
        var created = await service.CreateAsync("DELETE-CASE", new DateOnly(2026, 11, 18));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.DeleteAsync(created.Id, 0));
        await Assert.ThrowsAsync<SinistroConcurrencyConflictException>(() =>
            service.DeleteAsync(created.Id, created.Version + 1));
        Assert.Equal("DELETE-CASE", (await service.GetAsync(created.Id)).ExternalReference);
        await service.DeleteAsync(created.Id, created.Version);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAsync(created.Id));
        Assert.Equal(new[] {"sinistro.create", "sinistro.delete"}, audit.Events.Select(e => e.Action));
        Assert.All(audit.Events, e => Assert.Equal("succeeded", e.Outcome));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.DeleteAsync(Guid.NewGuid(), 1));
        var foreign = Sinistro.Create(TenantId.From(Guid.NewGuid()),
            "FOREIGN-DELETE", new DateOnly(2026, 11, 18));
        repo.Saved[foreign.Id] = foreign;
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.DeleteAsync(foreign.Id, 1));
        Assert.Empty(audit.Events.Where(e => e.ResourceId == foreign.Id));

        var readonlyActor = new ActingTenant(read: true);
        var denied = new SinistroService(readonlyActor, repo, audit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            denied.DeleteAsync(foreign.Id, 1));
        Assert.Equal("sinistro.delete", audit.Events[^1].Action);
        Assert.Equal("denied", audit.Events[^1].Outcome);
        Assert.Null(audit.Events[^1].ResourceId);
        Assert.Contains(foreign.Id, repo.Saved.Keys);
    }

    [Fact]
    public async Task Delete_linked_case_never_creates_success_audit_or_removes_case()
    {
        var actor = new ActingTenant(write: true, delete: true);
        var audit = new SpyAudit();
        var repo = new InMemorySinistri(audit);
        var service = new SinistroService(actor, repo, audit, SinistroDeletionGate.ForEnvironment(true, true));
        var created = await service.CreateAsync("LINKED-DELETE", new DateOnly(2026, 11, 18));
        repo.LinkedIds.Add(created.Id);
        await Assert.ThrowsAsync<SinistroLinkedPerizieException>(() =>
            service.DeleteAsync(created.Id, created.Version));
        Assert.Equal(created.Id, (await service.GetAsync(created.Id)).Id);
        Assert.DoesNotContain(audit.Events, e => e.Action == "sinistro.delete" &&
            e.Outcome == "succeeded");
    }

    [Fact]
    public async Task Search_rejects_invalid_filters_before_repository_and_does_not_audit_a_read()
    {
        var actor = new ActingTenant(read: true);
        var audit = new SpyAudit();
        var repo = new InMemorySinistri(audit);
        var service = new SinistroService(actor, repo, audit);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync("X"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new string('R', 101)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SearchAsync("AB", limit: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SearchAsync("AB", limit: 51));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(
            "AB", new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 15)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(
            "AB", default(DateOnly), null));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(
            "AB", null, default(DateOnly)));
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Search_is_scoped_normalized_and_evaluated_before_limit()
    {
        var actor = new ActingTenant(read: true);
        var audit = new SpyAudit();
        var repo = new InMemorySinistri(audit);
        var service = new SinistroService(actor, repo, audit);

        repo.Saved.Add(Guid.NewGuid(), Sinistro.Create(actor.TenantId,
            "NO-MATCH-NEW", new DateOnly(2026, 11, 16)));
        var match = Sinistro.Create(actor.TenantId, "   AB_%Older   ", new DateOnly(2026, 10, 10));
        repo.Saved.Add(match.Id, match);
        var future = Sinistro.Create(actor.TenantId, "AB_%Future", new DateOnly(2026, 11, 17));
        repo.Saved.Add(future.Id, future);
        var foreign = Sinistro.Create(TenantId.From(Guid.NewGuid()),
            "AB_%SECRET", new DateOnly(2026, 10, 11));
        repo.Saved.Add(foreign.Id, foreign);

        var results = await service.SearchAsync(" aB_% ", new DateOnly(2026, 10, 1),
            new DateOnly(2026, 11, 16), limit: 1);
        Assert.Single(results);
        Assert.Equal(match.Id, results[0].Id);
        Assert.Equal("AB_%Older", results[0].ExternalReference.Trim());
        Assert.DoesNotContain("TenantId", System.Text.Json.JsonSerializer.Serialize(results));
        Assert.Empty(audit.Events);

        var noScope = new SinistroService(new ActingTenant(), repo, audit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => noScope.SearchAsync("AB"));
        Assert.Empty(audit.Events);
    }

    private sealed class ActingTenant(bool write = false, bool read = false, bool delete = false) : ITenantContext
    {
        public TenantId TenantId { get; } = TenantId.From(Guid.NewGuid());
        public string Subject => "signed-subject";
        public bool HasScope(string scope) =>
            (write && scope == "peritial.write") ||
            (read && scope == "peritial.read") ||
            (delete && scope == "peritial.sinistri.delete");
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

    private sealed class InMemorySinistri(SpyAudit audit) : ISinistroRepository
    {
        public Dictionary<Guid, Sinistro> Saved { get; } = [];
        public HashSet<Guid> LinkedIds { get; } = [];
        private static Sinistro Snapshot(Sinistro s) => Sinistro.Restore(
            s.Id, s.TenantId, s.ExternalReference, s.OccurredOn, s.ReportedOn, s.CreatedAtUtc, s.PersistedVersion);
        public Task<IReadOnlyList<Sinistro>> PageAsync(TenantId tenant,
            SinistroPageBoundary? after, int fetchLimit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Sinistro>>(Saved.Values
                .Where(x => x.TenantId == tenant && (after is null ||
                    x.OccurredOn < after.OccurredOn ||
                    (x.OccurredOn == after.OccurredOn && x.Id.CompareTo(after.Id) > 0)))
                .OrderByDescending(x => x.OccurredOn).ThenBy(x => x.Id)
                .Take(fetchLimit).Select(Snapshot).ToArray());
        public Task<IReadOnlyList<Sinistro>> ListAsync(TenantId tenant, int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Sinistro>>(Saved.Values.Where(x => x.TenantId == tenant)
                .Take(limit).Select(Snapshot).ToArray());
        public Task<IReadOnlyList<Sinistro>> SearchAsync(
            TenantId tenant, string prefix, DateOnly? occurredFrom = null,
            DateOnly? occurredTo = null, int limit = 10, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Sinistro>>(Saved.Values
                .Where(x => x.TenantId == tenant &&
                    x.ExternalReference.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    (!occurredFrom.HasValue || x.OccurredOn >= occurredFrom.Value) &&
                    (!occurredTo.HasValue || x.OccurredOn <= occurredTo.Value))
                .OrderByDescending(x => x.OccurredOn).ThenBy(x => x.Id)
                .Take(limit).Select(Snapshot).ToArray());
        public Task<IReadOnlyList<Sinistro>> SearchPageAsync(
            TenantId tenant, SinistroSearchFilter filter, SinistroPageBoundary? after,
            int fetchLimit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Sinistro>>(Saved.Values
                .Where(x => x.TenantId == tenant &&
                    x.ExternalReference.StartsWith(filter.Prefix, StringComparison.OrdinalIgnoreCase) &&
                    (!filter.OccurredFrom.HasValue || x.OccurredOn >= filter.OccurredFrom.Value) &&
                    (!filter.OccurredTo.HasValue || x.OccurredOn <= filter.OccurredTo.Value) &&
                    (after is null || x.OccurredOn < after.OccurredOn ||
                        (x.OccurredOn == after.OccurredOn && x.Id.CompareTo(after.Id) > 0)))
                .OrderByDescending(x => x.OccurredOn).ThenBy(x => x.Id)
                .Take(fetchLimit).Select(Snapshot).ToArray());
        public Task<Sinistro?> FindAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            Task.FromResult(Saved.TryGetValue(id, out var s) && s.TenantId == tenant ? Snapshot(s) : null);
        public async Task DeleteAuditedAsync(TenantId tenant, Guid id,
            long expectedVersion, SecurityAuditEvent entry, CancellationToken ct = default)
        {
            if (!Saved.TryGetValue(id, out var current) || current.TenantId != tenant)
                throw new KeyNotFoundException("Sinistro not found.");
            if (current.PersistedVersion != expectedVersion)
                throw new SinistroConcurrencyConflictException();
            if (LinkedIds.Contains(id)) throw new SinistroLinkedPerizieException();
            await audit.AppendAsync(entry, ct);
            Saved.Remove(id);
        }
        public async Task AddAuditedAsync(Sinistro item, SecurityAuditEvent entry, CancellationToken ct = default)
        {
            await audit.AppendAsync(entry, ct);
            Saved.Add(item.Id, Snapshot(item));
        }
        public async Task UpdateAuditedAsync(Sinistro item, SecurityAuditEvent entry, CancellationToken ct = default)
        {
            if (!Saved.TryGetValue(item.Id, out var current) || current.TenantId != item.TenantId)
                throw new KeyNotFoundException();
            if (current.PersistedVersion != item.PersistedVersion)
                throw new SinistroConcurrencyConflictException();
            await audit.AppendAsync(entry, ct);
            item.ConfirmPersistedUpdate();
            Saved[item.Id] = Snapshot(item);
        }
    }
}
