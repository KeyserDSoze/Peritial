using Npgsql;
using Peritial.Business;
using Peritial.Domains;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class SecurityAuditRepositoryTests
{
    [Fact]
    public async Task Restricted_app_role_can_only_append_own_tenant_audit_and_never_read_or_update_it()
    {
        var adminConnection = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
            ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required");
        await using var admin = NpgsqlDataSource.Create(adminConnection);
        await SchemaBootstrap.ApplyDevelopmentSchemaAsync(admin);
        await TestDbRoleProvisioner.ConfigureAsync(admin);

        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var appConnection = new NpgsqlConnectionStringBuilder(adminConnection)
        {
            Username = "peritial_app",
            Password = "local-only-app",
            MaxPoolSize = 1
        }.ConnectionString;
        await using var app = NpgsqlDataSource.Create(appConnection);
        var repo = new PgSecurityAuditTrail(app);
        await repo.AppendAsync(new SecurityAuditEvent(
            id, a, "test-subject", "perizia.create", "succeeded", Guid.NewGuid(), DateTime.UtcNow));
        await repo.AppendAsync(new SecurityAuditEvent(
            Guid.NewGuid(), b, "test-subject", "perizia.assign", "denied", null, DateTime.UtcNow));

        await using var readAdmin = admin.CreateCommand(
            "SELECT tenant_id, subject, action, outcome FROM security_audit WHERE id = @id");
        readAdmin.Parameters.AddWithValue("id", id);
        await using (var reader = await readAdmin.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(a.Value, reader.GetGuid(0));
            Assert.Equal("test-subject", reader.GetString(1));
            Assert.Equal("perizia.create", reader.GetString(2));
            Assert.Equal("succeeded", reader.GetString(3));
        }

        await using var conn = await app.OpenConnectionAsync();
        await using var query = new NpgsqlCommand("SELECT * FROM security_audit", conn);
        var readFailure = await Assert.ThrowsAsync<PostgresException>(
            async () => await query.ExecuteReaderAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, readFailure.SqlState);

        await using var policy = admin.CreateCommand(
            "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE relname='security_audit'");
        Assert.True((bool)(await policy.ExecuteScalarAsync())!);

        await using var noContext = new NpgsqlCommand(
            "INSERT INTO security_audit (tenant_id,id,subject,action,outcome,occurred_at_utc) " +
            "VALUES (@tenant,@id,'test','perizia.create','succeeded',now())",conn);
        noContext.Parameters.AddWithValue("tenant", a.Value);
        noContext.Parameters.AddWithValue("id", Guid.NewGuid());
        var writeFailure = await Assert.ThrowsAsync<PostgresException>(
            async () => await noContext.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, writeFailure.SqlState);
    }
}
