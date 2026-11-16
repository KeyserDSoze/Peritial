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
            Assert.Equal(new[] {1, 2, 3}, (int[])(await versions.ExecuteScalarAsync())!);
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
                Assert.Equal(3, await SchemaMigrator.ApplyAsync(admin));
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
