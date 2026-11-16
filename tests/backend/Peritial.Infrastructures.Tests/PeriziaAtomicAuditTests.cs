using Npgsql;
using Peritial.Business;
using Peritial.Domains;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class PeriziaAtomicAuditTests
{
    [Fact]
    public async Task Create_and_assign_commit_aggregate_and_audit_in_one_transaction()
    {
        await using var databases = await TestDatabases.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var perizia = Perizia.Create(tenant, "ATOMIC-SUCCESS-" + Guid.NewGuid().ToString("N"));
        var repo = new PgPeriziaRepository(databases.App);
        await repo.AddAuditedAsync(perizia, Entry(tenant, perizia.Id, "perizia.create"));

        perizia.Assign(tenant, "operator");
        await repo.UpdateAuditedAsync(perizia, Entry(tenant, perizia.Id, "perizia.assign"));

        await using var cmd = databases.Admin.CreateCommand(
            "SELECT count(*) FROM security_audit WHERE tenant_id=@tenant AND resource_id=@id AND outcome='succeeded'");
        cmd.Parameters.AddWithValue("tenant", tenant.Value);
        cmd.Parameters.AddWithValue("id", perizia.Id);
        Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync())!);
        var restored = await repo.FindAsync(tenant, perizia.Id);
        Assert.NotNull(restored);
        Assert.Equal(PeriziaStatus.Assigned, restored.Status);
    }

    [Fact]
    public async Task Cancel_is_atomic_and_records_only_one_success_event()
    {
        await using var databases = await TestDatabases.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var perizia = Perizia.Create(tenant, "CANCEL-ATOMIC-" + Guid.NewGuid().ToString("N"));
        var repo = new PgPeriziaRepository(databases.App);
        await repo.AddAuditedAsync(perizia, Entry(tenant, perizia.Id, "perizia.create"));

        perizia.Cancel(tenant);
        await repo.UpdateAuditedAsync(perizia, Entry(tenant, perizia.Id, "perizia.cancel"));

        Assert.Equal(PeriziaStatus.Cancelled, (await repo.FindAsync(tenant, perizia.Id))!.Status);
        await using var audit = databases.Admin.CreateCommand(
            "SELECT count(*) FROM security_audit WHERE tenant_id=@tenant AND resource_id=@id AND action='perizia.cancel' AND outcome='succeeded'");
        audit.Parameters.AddWithValue("tenant", tenant.Value);
        audit.Parameters.AddWithValue("id", perizia.Id);
        Assert.Equal(1L, (long)(await audit.ExecuteScalarAsync())!);

        // Replayed cancellation cannot add another audit success event.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            repo.UpdateAuditedAsync(perizia, Entry(tenant, perizia.Id, "perizia.cancel")));
        Assert.Equal(1L, (long)(await audit.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Cancel_audit_failure_rolls_back_the_status()
    {
        await using var databases = await TestDatabases.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var perizia = Perizia.Create(tenant, "CANCEL-ROLLBACK-" + Guid.NewGuid().ToString("N"));
        var repo = new PgPeriziaRepository(databases.App);
        var create = Entry(tenant, perizia.Id, "perizia.create");
        await repo.AddAuditedAsync(perizia, create);

        perizia.Cancel(tenant);
        var duplicated = create with { Action = "perizia.cancel" };
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            repo.UpdateAuditedAsync(perizia, duplicated));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal(PeriziaStatus.Registered, (await repo.FindAsync(tenant, perizia.Id))!.Status);
    }

    [Fact]
    public async Task Cancel_cannot_modify_other_tenant_even_by_direct_repository_call()
    {
        await using var databases = await TestDatabases.OpenAsync();
        var owner = TenantId.From(Guid.NewGuid());
        var other = TenantId.From(Guid.NewGuid());
        var perizia = Perizia.Create(owner, "CANCEL-TENANT-" + Guid.NewGuid().ToString("N"));
        var repo = new PgPeriziaRepository(databases.App);
        await repo.AddAuditedAsync(perizia, Entry(owner, perizia.Id, "perizia.create"));

        Assert.Null(await repo.FindAsync(other, perizia.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            repo.UpdateAuditedAsync(perizia, Entry(other, perizia.Id, "perizia.cancel")));
        Assert.Equal(PeriziaStatus.Registered, (await repo.FindAsync(owner, perizia.Id))!.Status);
    }

    [Fact]
    public async Task Duplicate_audit_id_undoes_perizia_creation_then_new_request_can_succeed()
    {
        await using var databases = await TestDatabases.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var perizia = Perizia.Create(tenant, "ATOMIC-ROLLBACK-" + Guid.NewGuid().ToString("N"));
        var repo = new PgPeriziaRepository(databases.App);
        var reused = Entry(tenant, perizia.Id, "perizia.create");
        await new PgSecurityAuditTrail(databases.App).AppendAsync(reused);

        var error = await Assert.ThrowsAsync<PostgresException>(
            async () => await repo.AddAuditedAsync(perizia, reused));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Null(await repo.FindAsync(tenant, perizia.Id));

        await repo.AddAuditedAsync(perizia, Entry(tenant, perizia.Id, "perizia.create"));
        Assert.NotNull(await repo.FindAsync(tenant, perizia.Id));
    }

    [Fact]
    public async Task Duplicate_audit_id_rolls_back_status_change_without_a_success_event()
    {
        await using var databases = await TestDatabases.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var perizia = Perizia.Create(tenant, "ATOMIC-UPDATE-" + Guid.NewGuid().ToString("N"));
        var repo = new PgPeriziaRepository(databases.App);
        var createEvent = Entry(tenant, perizia.Id, "perizia.create");
        await repo.AddAuditedAsync(perizia, createEvent);

        perizia.Assign(tenant, "assignee");
        var duplicated = createEvent with { Action = "perizia.assign" };
        var error = await Assert.ThrowsAsync<PostgresException>(
            async () => await repo.UpdateAuditedAsync(perizia, duplicated));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal(PeriziaStatus.Registered, (await repo.FindAsync(tenant, perizia.Id))!.Status);

        await repo.UpdateAuditedAsync(perizia, Entry(tenant, perizia.Id, "perizia.assign"));
        Assert.Equal(PeriziaStatus.Assigned, (await repo.FindAsync(tenant, perizia.Id))!.Status);
    }

    [Fact]
    public async Task Audit_tenant_mismatch_never_commits_business_data()
    {
        await using var databases = await TestDatabases.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var foreign = TenantId.From(Guid.NewGuid());
        var item = Perizia.Create(tenant, "ATOMIC-FOREIGN-" + Guid.NewGuid().ToString("N"));
        var repo = new PgPeriziaRepository(databases.App);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => repo.AddAuditedAsync(item, Entry(foreign, item.Id, "perizia.create")));
        Assert.Null(await repo.FindAsync(tenant, item.Id));
    }

    private static SecurityAuditEvent Entry(TenantId tenant, Guid id, string action) =>
        new(Guid.NewGuid(), tenant, "signed-subject", action, "succeeded", id, DateTime.UtcNow);

    private sealed class TestDatabases(NpgsqlDataSource admin, NpgsqlDataSource app) : IAsyncDisposable
    {
        public NpgsqlDataSource Admin { get; } = admin;
        public NpgsqlDataSource App { get; } = app;

        public static async Task<TestDatabases> OpenAsync()
        {
            var connection = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
                ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required.");
            var admin = NpgsqlDataSource.Create(connection);
            try
            {
                await SchemaBootstrap.ApplyDevelopmentSchemaAsync(admin);
                await TestDbRoleProvisioner.ConfigureAsync(admin);
                var builder = new NpgsqlConnectionStringBuilder(connection)
                {
                    Username = "peritial_app",
                    Password = "local-only-app",
                    MaxPoolSize = 1
                };
                var app = NpgsqlDataSource.Create(builder.ConnectionString);
                return new TestDatabases(admin, app);
            }
            catch
            {
                await admin.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Admin.DisposeAsync();
        }
    }
}
