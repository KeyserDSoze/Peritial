using Npgsql;
using Peritial.Business;
using Peritial.Domains;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class DocumentAtomicAuditTests
{
    [Fact]
    public async Task Document_metadata_and_success_audit_share_a_transaction()
    {
        await using var databases = await Db.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var record = Record(tenant);
        var audit = Event(tenant, record.Id);
        await new PgUploadIntentLedger(databases.App).RecordAsync(tenant, record.Id, record.StorageKey);
        await new PgDocumentRepository(databases.App).AddAuditedAsync(record, audit);

        await using var cmd = databases.Admin.CreateCommand(
            "SELECT (SELECT count(*) FROM documents WHERE tenant_id=@tenant AND id=@id), " +
            "(SELECT count(*) FROM security_audit WHERE tenant_id=@tenant AND id=@audit)");
        cmd.Parameters.AddWithValue("tenant", tenant.Value);
        cmd.Parameters.AddWithValue("id", record.Id);
        cmd.Parameters.AddWithValue("audit", audit.Id);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task Failed_audit_insertion_rolls_back_document_metadata()
    {
        await using var databases = await Db.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var record = Record(tenant);
        var audit = Event(tenant, record.Id);
        await new PgUploadIntentLedger(databases.App).RecordAsync(tenant, record.Id, record.StorageKey);
        await new PgSecurityAuditTrail(databases.App).AppendAsync(audit);

        var repo = new PgDocumentRepository(databases.App);
        var error = await Assert.ThrowsAsync<PostgresException>(
            async () => await repo.AddAuditedAsync(record, audit));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);

        await using var check = databases.Admin.CreateCommand(
            "SELECT count(*) FROM documents WHERE tenant_id=@tenant AND id=@id");
        check.Parameters.AddWithValue("tenant", tenant.Value);
        check.Parameters.AddWithValue("id", record.Id);
        Assert.Equal(0L, (long)(await check.ExecuteScalarAsync())!);

        await repo.AddAuditedAsync(record, Event(tenant, record.Id));
        Assert.Equal(1L, (long)(await check.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Cross_tenant_audit_cannot_create_document_metadata()
    {
        await using var databases = await Db.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var other = TenantId.From(Guid.NewGuid());
        var record = Record(tenant);
        var repo = new PgDocumentRepository(databases.App);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => repo.AddAuditedAsync(record, Event(other, record.Id)));

        await using var check = databases.Admin.CreateCommand(
            "SELECT count(*) FROM documents WHERE tenant_id=@tenant AND id=@id");
        check.Parameters.AddWithValue("tenant", tenant.Value);
        check.Parameters.AddWithValue("id", record.Id);
        Assert.Equal(0L, (long)(await check.ExecuteScalarAsync())!);
    }

    private static DocumentRecord Record(TenantId tenant)
    {
        var id = Guid.NewGuid();
        return new DocumentRecord(id, tenant, null, "test.pdf", "application/pdf", 5,
            tenant.Value.ToString("N") + "/" + id.ToString("N") + ".pdf",
            DateTime.UtcNow, ContentSha256: DocumentDigest.Compute("%PDF-"u8));
    }

    private static SecurityAuditEvent Event(TenantId tenant, Guid id) =>
        new(Guid.NewGuid(), tenant, "signed-subject", "document.ingest", "succeeded", id, DateTime.UtcNow);

    private sealed class Db(NpgsqlDataSource admin, NpgsqlDataSource app) : IAsyncDisposable
    {
        public NpgsqlDataSource Admin { get; } = admin;
        public NpgsqlDataSource App { get; } = app;

        public static async Task<Db> OpenAsync()
        {
            var root = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
                ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required.");
            var admin = NpgsqlDataSource.Create(root);
            try
            {
                await SchemaBootstrap.ApplyDevelopmentSchemaAsync(admin);
                await TestDbRoleProvisioner.ConfigureAsync(admin);
                var cs = new NpgsqlConnectionStringBuilder(root)
                {
                    Username = "peritial_app", Password = "local-only-app", MaxPoolSize = 1
                };
                return new Db(admin, NpgsqlDataSource.Create(cs.ConnectionString));
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
