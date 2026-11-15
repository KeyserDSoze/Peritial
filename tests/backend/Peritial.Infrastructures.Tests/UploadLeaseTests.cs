using Npgsql;
using Peritial.Business;
using Peritial.Domains;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class UploadLeaseTests
{
    [Fact]
    public async Task Worker_does_not_delete_old_blob_while_its_upload_lease_is_live()
    {
        await using var db = await OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var key = $"{tenant.Value:N}/{id:N}.pdf";
        var store = new FakeStore();
        store.Keys.Add(key);
        var ledger = new PgUploadIntentLedger(db.App);
        await ledger.RecordAsync(tenant, id, key);

        // An upload may take longer than the original one-hour grace period.
        await db.ExecuteAsync(
            "UPDATE upload_intents SET created_at_utc=now()-interval '2 hours' " +
            "WHERE tenant_id=@tenant AND document_id=@id", tenant.Value, id);
        try
        {
            Assert.False(await new PgUploadReconciler(db.Recovery, store)
                .ReconcileOneAsync(DateTime.UtcNow.AddMinutes(5)));
            Assert.Contains(key, store.Keys);
            Assert.Equal(0, store.Deletes);
        }
        finally
        {
            await ledger.ForgetAsync(tenant, id);
        }
    }

    [Fact]
    public async Task Expired_lease_can_be_recovered_after_grace_period()
    {
        await using var db = await OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var key = $"{tenant.Value:N}/{id:N}.pdf";
        var store = new FakeStore();
        store.Keys.Add(key);
        await new PgUploadIntentLedger(db.App).RecordAsync(tenant, id, key);
        await db.ExecuteAsync(
            "UPDATE upload_intents SET created_at_utc=now()-interval '2 hours', " +
            "lease_expires_at_utc=now()-interval '1 minute' " +
            "WHERE tenant_id=@tenant AND document_id=@id", tenant.Value, id);

        Assert.True(await new PgUploadReconciler(db.Recovery, store)
            .ReconcileOneAsync(DateTime.UtcNow));
        Assert.Empty(store.Keys);
        Assert.Equal(1, store.Deletes);
    }

    [Fact]
    public async Task Lease_renewal_is_tenant_scoped_and_refuses_expired_or_removed_intents()
    {
        await using var db = await OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var foreign = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var ledger = new PgUploadIntentLedger(db.App);
        await ledger.RecordAsync(tenant, id, $"{tenant.Value:N}/{id:N}.pdf");
        await db.ExecuteAsync(
            "UPDATE upload_intents SET lease_expires_at_utc=now()+interval '10 seconds' " +
            "WHERE tenant_id=@tenant AND document_id=@id", tenant.Value, id);

        Assert.False(await ledger.RenewAsync(foreign, id));
        Assert.True(await ledger.RenewAsync(tenant, id));
        await using (var cmd = db.Admin.CreateCommand(
            "SELECT lease_expires_at_utc > now()+interval '1 minute' " +
            "FROM upload_intents WHERE tenant_id=@tenant AND document_id=@id"))
        {
            cmd.Parameters.AddWithValue("tenant", tenant.Value);
            cmd.Parameters.AddWithValue("id", id);
            Assert.Equal(true, await cmd.ExecuteScalarAsync());
        }
        await ledger.ForgetAsync(tenant, id);
        Assert.False(await ledger.RenewAsync(tenant, id));

        var next = Guid.NewGuid();
        await ledger.RecordAsync(tenant, next, $"{tenant.Value:N}/{next:N}.pdf");
        try
        {
            await db.ExecuteAsync(
                "UPDATE upload_intents SET lease_expires_at_utc=now()-interval '1 minute' " +
                "WHERE tenant_id=@tenant AND document_id=@id", tenant.Value, next);
            Assert.False(await ledger.RenewAsync(tenant, next));
        }
        finally { await ledger.ForgetAsync(tenant, next); }
    }

    [Fact]
    public async Task Expired_lease_prevents_late_metadata_and_audit_commit()
    {
        await using var db = await OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var key = $"{tenant.Value:N}/{id:N}.pdf";
        await new PgUploadIntentLedger(db.App).RecordAsync(tenant, id, key);
        try
        {
            await db.ExecuteAsync(
                "UPDATE upload_intents SET lease_expires_at_utc=now()-interval '1 minute' " +
                "WHERE tenant_id=@tenant AND document_id=@id", tenant.Value, id);
            var doc = new DocumentRecord(id, tenant, null, "proof.pdf",
                "application/pdf", 12, key, DateTime.UtcNow);
            var audit = new SecurityAuditEvent(Guid.NewGuid(), tenant, "actor",
                "document.ingest", "succeeded", id, DateTime.UtcNow);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new PgDocumentRepository(db.App).AddAuditedAsync(doc, audit));
            await using var check = db.Admin.CreateCommand(
                "SELECT count(*) FROM documents WHERE tenant_id=@tenant AND id=@id");
            check.Parameters.AddWithValue("tenant", tenant.Value);
            check.Parameters.AddWithValue("id", id);
            Assert.Equal(0L, (long)(await check.ExecuteScalarAsync())!);
        }
        finally { await new PgUploadIntentLedger(db.App).ForgetAsync(tenant, id); }
    }

    private sealed class FakeStore : IObjectStore
    {
        public HashSet<string> Keys { get; } = [];
        public int Deletes { get; private set; }
        public Task PutAsync(string key, Stream stream, string mime, CancellationToken ct = default)
        { Keys.Add(key); return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken ct = default)
        { Deletes++; Keys.Remove(key); return Task.CompletedTask; }
    }

    private sealed class Db(NpgsqlDataSource admin, NpgsqlDataSource app, NpgsqlDataSource recovery)
        : IAsyncDisposable
    {
        public NpgsqlDataSource Admin { get; } = admin;
        public NpgsqlDataSource App { get; } = app;
        public NpgsqlDataSource Recovery { get; } = recovery;

        public async Task ExecuteAsync(string sql, Guid tenant, Guid id)
        {
            await using var cmd = Admin.CreateCommand(sql);
            cmd.Parameters.AddWithValue("tenant", tenant);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        public static async Task<Db> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
                ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required.");
            var admin = NpgsqlDataSource.Create(cs);
            try
            {
                await SchemaBootstrap.ApplyDevelopmentSchemaAsync(admin);
                await TestDbRoleProvisioner.ConfigureAsync(admin);
                var app = new NpgsqlConnectionStringBuilder(cs)
                    { Username="peritial_app", Password="local-only-app", MaxPoolSize=1 };
                var recovery = new NpgsqlConnectionStringBuilder(cs)
                    { Username="peritial_recovery", Password="local-only-recovery", MaxPoolSize=1 };
                return new Db(admin, NpgsqlDataSource.Create(app.ConnectionString),
                    NpgsqlDataSource.Create(recovery.ConnectionString));
            }
            catch { await admin.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            await Recovery.DisposeAsync();
            await App.DisposeAsync();
            await Admin.DisposeAsync();
        }
    }
}
