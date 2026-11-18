using Npgsql;
using Peritial.Business;
using Peritial.Domains;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// Isolated real PostgreSQL 17 schema, real least-privileged app role and
/// actual transaction-local tenant context. No mock RLS or privileged reads.
/// </summary>
public sealed class SinistroPersistenceTests
{
    [Fact]
    public async Task V003_creates_forced_tenant_rls_with_least_privilege_and_idempotent_journal()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        Assert.Equal(0, await SchemaMigrator.ApplyAsync(db.Admin));

        await using var conn = await db.Admin.OpenConnectionAsync();
        await using (var policy = new NpgsqlCommand(
            "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class " +
            "WHERE oid = to_regclass('sinistri')", conn))
            Assert.True((bool)(await policy.ExecuteScalarAsync())!);
        await using (var role = new NpgsqlCommand(
            "SELECT has_table_privilege('peritial_app','sinistri','SELECT') " +
            "AND has_table_privilege('peritial_app','sinistri','INSERT') " +
            "AND has_table_privilege('peritial_app','sinistri','UPDATE') " +
            "AND NOT has_table_privilege('peritial_app','sinistri','DELETE') " +
            "AND NOT has_table_privilege('peritial_recovery','sinistri','SELECT')", conn))
            Assert.True((bool)(await role.ExecuteScalarAsync())!);
        await using (var versions = new NpgsqlCommand(
            "SELECT array_agg(version ORDER BY version) FROM schema_migrations", conn))
            Assert.Equal(new[] {1, 2, 3, 4}, (int[])(await versions.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Repository_read_write_isolation_is_enforced_by_RLS_and_pool_context_does_not_leak()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var first = Sinistro.Create(a, "SIN-SHARED", new DateOnly(2026, 11, 2));
        var second = Sinistro.Create(b, "SIN-SHARED", new DateOnly(2026, 11, 3));

        await repo.AddAuditedAsync(first, Event(a, first.Id, "sinistro.create"));
        await repo.AddAuditedAsync(second, Event(b, second.Id, "sinistro.create"));
        Assert.Equal(first.Id, (await repo.ListAsync(a)).Single().Id);
        Assert.Equal(second.Id, (await repo.ListAsync(b)).Single().Id);
        Assert.Null(await repo.FindAsync(a, second.Id));
        Assert.Null(await repo.FindAsync(b, first.Id));
        Assert.Equal(1L, first.PersistedVersion);
        Assert.Equal(1L, (await repo.FindAsync(a, first.Id))!.PersistedVersion);

        // The same pooled app connection has no cross-request tenant context.
        await using var conn = await db.App.OpenConnectionAsync();
        await using (var noTenant = new NpgsqlCommand(
            "SELECT count(*) FROM sinistri WHERE id IN (@a,@b)", conn))
        {
            noTenant.Parameters.AddWithValue("a", first.Id);
            noTenant.Parameters.AddWithValue("b", second.Id);
            Assert.Equal(0L, (long)(await noTenant.ExecuteScalarAsync())!);
        }
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand(
            "SELECT set_config('peritial.tenant_id', @tenant, true)", conn, tx))
        {
            set.Parameters.AddWithValue("tenant", a.Value.ToString("D"));
            await set.ExecuteNonQueryAsync();
        }
        await using (var crossTenantWrite = new NpgsqlCommand(
            "INSERT INTO sinistri (tenant_id,id,external_reference,occurred_on,created_at_utc) " +
            "VALUES (@tenant,@id,'RLS-DENIED',DATE '2026-11-01',now())", conn, tx))
        {
            crossTenantWrite.Parameters.AddWithValue("tenant", b.Value);
            crossTenantWrite.Parameters.AddWithValue("id", Guid.NewGuid());
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => crossTenantWrite.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Stale_update_cannot_overwrite_newer_sinistro_or_append_success_audit()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var a = TenantId.From(Guid.NewGuid());
        var initial = Sinistro.Create(a, "SIN-CONCURRENT", new DateOnly(2026, 10, 1));
        await repo.AddAuditedAsync(initial, Event(a, initial.Id, "sinistro.create"));
        var one = (await repo.FindAsync(a, initial.Id))!;
        var stale = (await repo.FindAsync(a, initial.Id))!;

        one.Update(a, "SIN-CURRENT", new DateOnly(2026, 10, 2));
        await repo.UpdateAuditedAsync(one, Event(a, one.Id, "sinistro.update"));
        Assert.Equal(2L, one.PersistedVersion);

        stale.Update(a, "SIN-STALE", new DateOnly(2026, 10, 3));
        await Assert.ThrowsAsync<SinistroConcurrencyConflictException>(() =>
            repo.UpdateAuditedAsync(stale, Event(a, stale.Id, "sinistro.update")));
        var restored = (await repo.FindAsync(a, initial.Id))!;
        Assert.Equal("SIN-CURRENT", restored.ExternalReference);
        Assert.Equal(2L, restored.PersistedVersion);

        await using var count = db.Admin.CreateCommand(
            "SELECT count(*) FROM security_audit WHERE tenant_id=@tenant " +
            "AND resource_id=@id AND action='sinistro.update' AND outcome='succeeded'");
        count.Parameters.AddWithValue("tenant", a.Value);
        count.Parameters.AddWithValue("id", initial.Id);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_both_insert_and_update_and_wrong_tenant_is_rejected()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var created = Sinistro.Create(a, "SIN-AUDIT", new DateOnly(2026, 9, 1));
        var previousAudit = Event(a, created.Id, "sinistro.create");
        await new PgSecurityAuditTrail(db.App).AppendAsync(previousAudit);
        var conflict = await Assert.ThrowsAsync<PostgresException>(() =>
            repo.AddAuditedAsync(created, previousAudit));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, conflict.SqlState);
        Assert.Null(await repo.FindAsync(a, created.Id));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            repo.AddAuditedAsync(created, Event(b, created.Id, "sinistro.create")));
        await repo.AddAuditedAsync(created, Event(a, created.Id, "sinistro.create"));
        var staleVersion = created.PersistedVersion;

        created.Update(a, "SIN-AUDIT-UPDATED", new DateOnly(2026, 9, 2));
        var duplicate = previousAudit with {Action = "sinistro.update"};
        var failedUpdate = await Assert.ThrowsAsync<PostgresException>(() =>
            repo.UpdateAuditedAsync(created, duplicate));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, failedUpdate.SqlState);
        Assert.Equal("SIN-AUDIT", (await repo.FindAsync(a, created.Id))!.ExternalReference);
        Assert.Equal(staleVersion, created.PersistedVersion);
        await repo.UpdateAuditedAsync(created, Event(a, created.Id, "sinistro.update"));
        Assert.Equal("SIN-AUDIT-UPDATED", (await repo.FindAsync(a, created.Id))!.ExternalReference);
    }

    [Fact]
    public async Task Search_filters_inside_postgres_before_limit_and_escapes_ilike_wildcards_with_RLS()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var entities = new[]
        {
            Sinistro.Create(a, "FRESH-NONMATCH", new DateOnly(2026, 12, 1)),
            Sinistro.Create(a, "CL_%Case-1", new DateOnly(2026, 10, 5)),
            Sinistro.Create(a, "cl_%case-2", new DateOnly(2026, 10, 7)),
            Sinistro.Create(a, "CL123-FALSE-POSITIVE", new DateOnly(2026, 11, 20)),
            Sinistro.Create(b, "CL_%PRIVATE", new DateOnly(2026, 10, 6)),
            Sinistro.Create(a, "CL_%OUTSIDE-DATES", new DateOnly(2026, 8, 1)),
        };
        foreach (var entity in entities)
            await repo.AddAuditedAsync(entity, Event(entity.TenantId, entity.Id, "sinistro.create"));

        // Literal '_' and '%' MUST NOT match arbitrary chars or records.
        var aResults = await repo.SearchAsync(a, "CL_%", new DateOnly(2026, 10, 1),
            new DateOnly(2026, 11, 30), limit: 2);
        Assert.Equal(new[] {entities[2].Id, entities[1].Id}, aResults.Select(x => x.Id));
        Assert.All(aResults, x => Assert.Equal(a, x.TenantId));
        Assert.Empty(await repo.SearchAsync(b, "FRESH"));
        Assert.Equal(entities[4].Id, (await repo.SearchAsync(b, "cL_%")).Single().Id);

        // Also escape a literal escape character (!) in the prefix.
        var escape = Sinistro.Create(a, "ESC!_%EXACT", new DateOnly(2026, 10, 8));
        await repo.AddAuditedAsync(escape, Event(a, escape.Id, "sinistro.create"));
        Assert.Equal(escape.Id, (await repo.SearchAsync(a, "esc!_%")).Single().Id);
    }

    [Fact]
    public async Task Filtered_keyset_search_pages_in_sql_with_literal_wildcards_date_bounds_and_RLS()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var tenant = TenantId.From(Guid.NewGuid());
        var foreign = TenantId.From(Guid.NewGuid());
        var date = new DateOnly(2026, 11, 18);
        var ids = Enumerable.Range(1, 3).Select(i =>
            Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}")).ToArray();
        var items = new[]
        {
            Sinistro.Restore(ids[1], tenant, "PG_%SECOND", date, null, DateTime.UtcNow),
            Sinistro.Restore(ids[0], tenant, "PG_%FIRST", date, null, DateTime.UtcNow),
            Sinistro.Restore(ids[2], tenant, "PG_%OLDER", date.AddDays(-1), null, DateTime.UtcNow),
            Sinistro.Create(tenant, "PGZZDECOY", date.AddDays(3)),
            Sinistro.Create(tenant, "PG_%OUTSIDE", date.AddDays(-30)),
            Sinistro.Create(foreign, "PG_%SECRET", date.AddDays(2)),
        };
        foreach (var item in items)
            await repo.AddAuditedAsync(item, Event(item.TenantId, item.Id, "sinistro.create"));

        var filter = SinistroSearchFilter.Create(" pg_% ", date.AddDays(-2), date.AddDays(1), 2);
        var first = await repo.SearchPageAsync(tenant, filter, null, fetchLimit: 3);
        Assert.Equal(ids, first.Select(x => x.Id));
        Assert.All(first, item => Assert.Equal(tenant, item.TenantId));
        var second = await repo.SearchPageAsync(tenant, filter,
            new SinistroPageBoundary(date, ids[1]), fetchLimit: 3);
        Assert.Equal(new[] {ids[2]}, second.Select(x => x.Id));
        Assert.Empty(await repo.SearchPageAsync(foreign, filter, null, fetchLimit: 3));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.SearchPageAsync(tenant, filter, null, fetchLimit: 52));
    }

    [Fact]
    public async Task Keyset_page_uses_tenant_RLS_and_date_id_boundary_in_postgres_not_offset()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var date = new DateOnly(2026, 11, 18);
        var ids = Enumerable.Range(1, 4).Select(i =>
            Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}")).ToArray();
        var entities = new[]
        {
            Sinistro.Restore(ids[1], a, "A-SECOND", date, null, DateTime.UtcNow),
            Sinistro.Restore(ids[0], a, "A-FIRST", date, null, DateTime.UtcNow),
            Sinistro.Restore(ids[2], a, "A-OLD", date.AddDays(-1), null, DateTime.UtcNow),
            Sinistro.Restore(ids[3], a, "A-NEW", date.AddDays(1), null, DateTime.UtcNow),
            Sinistro.Create(b, "B-PRIVATE", date.AddDays(3))
        };
        foreach (var item in entities)
            await repo.AddAuditedAsync(item, Event(item.TenantId, item.Id, "sinistro.create"));

        var first = await repo.PageAsync(a, null, fetchLimit: 3);
        Assert.Equal(new[] {ids[3], ids[0], ids[1]}, first.Select(x => x.Id));
        var second = await repo.PageAsync(a, new SinistroPageBoundary(date, ids[1]), fetchLimit: 3);
        Assert.Equal(new[] {ids[2]}, second.Select(x => x.Id));
        Assert.All(first.Concat(second), x => Assert.Equal(a, x.TenantId));
        Assert.Equal(new[] {entities[4].Id},
            (await repo.PageAsync(b, null, fetchLimit: 3)).Select(x => x.Id));
        Assert.Empty(await repo.PageAsync(a,
            new SinistroPageBoundary(date.AddDays(-2), ids[2]), fetchLimit: 3));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repo.PageAsync(a, null, fetchLimit: 52));
    }

    [Fact]
    public async Task V004_links_perizie_to_own_sinistro_with_tenant_composite_FK_and_atomic_audit()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var tenantA = TenantId.From(Guid.NewGuid());
        var tenantB = TenantId.From(Guid.NewGuid());
        var sinistri = new PgSinistroRepository(db.App);
        var perizie = new PgPeriziaRepository(db.App);
        var links = new PgPeriziaSinistroLinkRepository(db.App);
        var a = Sinistro.Create(tenantA, "SIN-LINK-A", new DateOnly(2026, 10, 1));
        var b = Sinistro.Create(tenantB, "SIN-LINK-B", new DateOnly(2026, 10, 2));
        await sinistri.AddAuditedAsync(a, Event(tenantA, a.Id, "sinistro.create"));
        await sinistri.AddAuditedAsync(b, Event(tenantB, b.Id, "sinistro.create"));
        var periziaA = Perizia.Create(tenantA, "PERIZIA-LINK-A-" + Guid.NewGuid().ToString("N"));
        var periziaB = Perizia.Create(tenantB, "PERIZIA-LINK-B-" + Guid.NewGuid().ToString("N"));
        await perizie.AddAuditedAsync(periziaA, Event(tenantA, periziaA.Id, "perizia.create"));
        await perizie.AddAuditedAsync(periziaB, Event(tenantB, periziaB.Id, "perizia.create"));

        Assert.Empty(await links.ListPeriziaIdsAsync(tenantA, a.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            links.LinkAuditedAsync(tenantA, periziaA.Id, b.Id,
                Event(tenantA, periziaA.Id, "perizia.link-sinistro")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            links.LinkAuditedAsync(tenantB, periziaA.Id, b.Id,
                Event(tenantB, periziaA.Id, "perizia.link-sinistro")));
        Assert.Empty(await links.ListPeriziaIdsAsync(tenantA, a.Id));

        await links.LinkAuditedAsync(tenantA, periziaA.Id, a.Id,
            Event(tenantA, periziaA.Id, "perizia.link-sinistro"));
        Assert.Equal(new[] { periziaA.Id }, await links.ListPeriziaIdsAsync(tenantA, a.Id));
        Assert.Empty(await links.ListPeriziaIdsAsync(tenantB, b.Id));
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            links.LinkAuditedAsync(tenantA, periziaA.Id, a.Id,
                Event(tenantA, periziaA.Id, "perizia.link-sinistro")));
        Assert.Equal(1L, await CountLinkAuditAsync(db.Admin, tenantA, periziaA.Id));

        // Direct elevated SQL must still respect the FK: A's perizia cannot
        // refer to B's sinistro even if the application role is bypassed.
        await using var sql = db.Admin.CreateCommand(
            "UPDATE perizie SET sinistro_id=@sinistro WHERE tenant_id=@tenant AND id=@perizia");
        sql.Parameters.AddWithValue("sinistro", b.Id);
        sql.Parameters.AddWithValue("tenant", tenantA.Value);
        sql.Parameters.AddWithValue("perizia", periziaA.Id);
        var fk = await Assert.ThrowsAsync<PostgresException>(() => sql.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, fk.SqlState);
        Assert.Equal(new[] { periziaA.Id }, await links.ListPeriziaIdsAsync(tenantA, a.Id));
    }

    [Fact]
    public async Task V004_association_read_returns_unlinked_or_linked_and_hides_foreign_perizia_as_missing()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var perizie = new PgPeriziaRepository(db.App);
        var links = new PgPeriziaSinistroLinkRepository(db.App);
        var owned = Perizia.Create(a, "LINK-READ-" + Guid.NewGuid().ToString("N"));
        var foreign = Perizia.Create(b, "LINK-FOREIGN-" + Guid.NewGuid().ToString("N"));
        await perizie.AddAuditedAsync(owned, Event(a, owned.Id, "perizia.create"));
        await perizie.AddAuditedAsync(foreign, Event(b, foreign.Id, "perizia.create"));
        Assert.Null(await links.FindSinistroIdForPeriziaAsync(a, owned.Id));
        Assert.Null(await links.FindSinistroIdForPeriziaAsync(b, foreign.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            links.FindSinistroIdForPeriziaAsync(a, foreign.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            links.FindSinistroIdForPeriziaAsync(a, Guid.NewGuid()));

        var sinistro = Sinistro.Create(a, "LINK-READ-SINISTRO", new DateOnly(2026, 11, 16));
        await new PgSinistroRepository(db.App).AddAuditedAsync(
            sinistro, Event(a, sinistro.Id, "sinistro.create"));
        await links.LinkAuditedAsync(a, owned.Id, sinistro.Id,
            Event(a, owned.Id, "perizia.link-sinistro"));
        Assert.Equal(sinistro.Id, await links.FindSinistroIdForPeriziaAsync(a, owned.Id));
        Assert.Equal(1L, await CountLinkAuditAsync(db.Admin, a, owned.Id));
    }

    [Fact]
    public async Task V004_failed_audit_rolls_back_link_and_does_not_generate_success()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var sinistro = Sinistro.Create(tenant, "SIN-ROLLBACK", new DateOnly(2026, 10, 9));
        await new PgSinistroRepository(db.App).AddAuditedAsync(
            sinistro, Event(tenant, sinistro.Id, "sinistro.create"));
        var perizia = Perizia.Create(tenant, "LINK-ROLLBACK-" + Guid.NewGuid().ToString("N"));
        await new PgPeriziaRepository(db.App).AddAuditedAsync(
            perizia, Event(tenant, perizia.Id, "perizia.create"));
        var repository = new PgPeriziaSinistroLinkRepository(db.App);
        var repeated = Event(tenant, perizia.Id, "perizia.link-sinistro");
        await new PgSecurityAuditTrail(db.App).AppendAsync(repeated);

        var conflict = await Assert.ThrowsAsync<PostgresException>(() =>
            repository.LinkAuditedAsync(tenant, perizia.Id, sinistro.Id, repeated));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, conflict.SqlState);
        Assert.Empty(await repository.ListPeriziaIdsAsync(tenant, sinistro.Id));
        Assert.Equal(1L, await CountLinkAuditAsync(db.Admin, tenant, perizia.Id));

        await repository.LinkAuditedAsync(tenant, perizia.Id, sinistro.Id,
            Event(tenant, perizia.Id, "perizia.link-sinistro"));
        Assert.Equal(new[] { perizia.Id }, await repository.ListPeriziaIdsAsync(tenant, sinistro.Id));
        Assert.Equal(2L, await CountLinkAuditAsync(db.Admin, tenant, perizia.Id));
    }

    private static async Task<long> CountLinkAuditAsync(NpgsqlDataSource db, TenantId tenant, Guid id)
    {
        await using var cmd = db.CreateCommand(
            "SELECT count(*) FROM security_audit WHERE tenant_id=@tenant AND resource_id=@id " +
            "AND action='perizia.link-sinistro' AND outcome='succeeded'");
        cmd.Parameters.AddWithValue("tenant", tenant.Value);
        cmd.Parameters.AddWithValue("id", id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static SecurityAuditEvent Event(TenantId tenant, Guid id, string action) =>
        new(Guid.NewGuid(), tenant, "signed-subject", action, "succeeded", id, DateTime.UtcNow);

    private sealed class IsolatedDatabases(NpgsqlDataSource admin, NpgsqlDataSource app,
        NpgsqlDataSource root, string schema) : IAsyncDisposable
    {
        public NpgsqlDataSource Admin { get; } = admin;
        public NpgsqlDataSource App { get; } = app;

        public static async Task<IsolatedDatabases> CreateAsync()
        {
            var cs = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
                ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required.");
            var schema = "sinistro_test_" + Guid.NewGuid().ToString("N");
            var root = NpgsqlDataSource.Create(cs);
            NpgsqlDataSource? admin = null;
            NpgsqlDataSource? app = null;
            try
            {
                await using (var conn = await root.OpenConnectionAsync())
                await using (var command = new NpgsqlCommand("CREATE SCHEMA " + schema, conn))
                    await command.ExecuteNonQueryAsync();
                admin = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(cs)
                { SearchPath = schema }.ConnectionString);
                Assert.Equal(4, await SchemaMigrator.ApplyAsync(admin));
                await TestDbRoleProvisioner.ConfigureAsync(admin);
                await using (var conn = await root.OpenConnectionAsync())
                await using (var grant = new NpgsqlCommand(
                    "GRANT USAGE ON SCHEMA " + schema + " TO peritial_app", conn))
                    await grant.ExecuteNonQueryAsync();
                app = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(cs)
                {
                    SearchPath = schema, Username = "peritial_app",
                    Password = "local-only-app", MaxPoolSize = 1
                }.ConnectionString);
                return new IsolatedDatabases(admin, app, root, schema);
            }
            catch
            {
                if (app is not null) await app.DisposeAsync();
                if (admin is not null) await admin.DisposeAsync();
                await using (var conn = await root.OpenConnectionAsync())
                await using (var drop = new NpgsqlCommand(
                    "DROP SCHEMA IF EXISTS " + schema + " CASCADE", conn))
                    await drop.ExecuteNonQueryAsync();
                await root.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Admin.DisposeAsync();
            await using (var conn = await root.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand(
                "DROP SCHEMA IF EXISTS " + schema + " CASCADE", conn))
                await command.ExecuteNonQueryAsync();
            await root.DisposeAsync();
        }
    }
}
