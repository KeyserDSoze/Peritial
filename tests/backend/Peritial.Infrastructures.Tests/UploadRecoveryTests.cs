using Npgsql;
using Peritial.Business;
using Peritial.Domains;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class UploadRecoveryTests
{
    [Fact]
    public async Task Crashed_upload_is_deleted_after_grace_period_and_intent_is_removed()
    {
        await using var db = await Databases.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var documentId = Guid.NewGuid();
        var key = $"{tenant.Value:N}/{documentId:N}.pdf";
        var blobs = new FakeBlobs();
        blobs.Stored.Add(key);
        await new PgUploadIntentLedger(db.App).RecordAsync(tenant, documentId, key);

        var worker = new PgUploadReconciler(db.Recovery, blobs);
        Assert.True(await worker.ReconcileOneAsync(DateTime.UtcNow.AddMinutes(5)));
        Assert.DoesNotContain(key, blobs.Stored);
        Assert.Equal(1, blobs.Deletions);
        Assert.False(await worker.ReconcileOneAsync(DateTime.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public async Task Successful_document_commit_atomically_consumes_intent_and_preserves_blob()
    {
        await using var db = await Databases.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var documentId = Guid.NewGuid();
        var key = $"{tenant.Value:N}/{documentId:N}.pdf";
        var blobs = new FakeBlobs();
        blobs.Stored.Add(key);
        await new PgUploadIntentLedger(db.App).RecordAsync(tenant, documentId, key);
        await new PgDocumentRepository(db.App).AddAuditedAsync(
            Record(tenant, documentId, key), Event(tenant, documentId));
        var worker = new PgUploadReconciler(db.Recovery, blobs);
        Assert.False(await worker.ReconcileOneAsync(DateTime.UtcNow.AddMinutes(5)));
        Assert.Contains(key, blobs.Stored);
        Assert.Equal(0, blobs.Deletions);
    }

    [Fact]
    public async Task Ambiguous_commit_never_deletes_blob_if_metadata_is_present()
    {
        await using var db = await Databases.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var documentId = Guid.NewGuid();
        var key = $"{tenant.Value:N}/{documentId:N}.pdf";
        var blobs = new FakeBlobs();
        blobs.Stored.Add(key);
        await new PgUploadIntentLedger(db.App).RecordAsync(tenant, documentId, key);
        // Simulate an interrupted migration or lost-ACK state with a committed
        // metadata record AND a residual pending upload intent.
        await using (var cmd = db.Admin.CreateCommand(
            "INSERT INTO documents (tenant_id,id,perizia_id,file_name,content_type,length_bytes,storage_key,created_at_utc) " +
            "VALUES (@tenant,@id,NULL,'proof.pdf','application/pdf',10,@key,now())"))
        {
            cmd.Parameters.AddWithValue("tenant", tenant.Value);
            cmd.Parameters.AddWithValue("id", documentId);
            cmd.Parameters.AddWithValue("key", key);
            await cmd.ExecuteNonQueryAsync();
        }
        var worker = new PgUploadReconciler(db.Recovery, blobs);
        Assert.True(await worker.ReconcileOneAsync(DateTime.UtcNow.AddMinutes(5)));
        Assert.Contains(key, blobs.Stored);
        Assert.Equal(0, blobs.Deletions);
        Assert.False(await worker.ReconcileOneAsync(DateTime.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public async Task Failed_object_deletion_keeps_intent_for_safe_retry()
    {
        await using var db = await Databases.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var documentId = Guid.NewGuid();
        var key = $"{tenant.Value:N}/{documentId:N}.pdf";
        var blobs = new FakeBlobs { FailDeletion = true };
        blobs.Stored.Add(key);
        await new PgUploadIntentLedger(db.App).RecordAsync(tenant, documentId, key);
        var worker = new PgUploadReconciler(db.Recovery, blobs);
        await Assert.ThrowsAsync<IOException>(() => worker.ReconcileOneAsync(DateTime.UtcNow.AddMinutes(5)));
        Assert.Contains(key, blobs.Stored);
        blobs.FailDeletion = false;
        Assert.True(await worker.ReconcileOneAsync(DateTime.UtcNow.AddMinutes(5)));
        Assert.DoesNotContain(key, blobs.Stored);
        Assert.False(await worker.ReconcileOneAsync(DateTime.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public async Task Commit_with_missing_intent_is_rejected_to_avoid_worker_delete_race()
    {
        await using var db = await Databases.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PgDocumentRepository(db.App).AddAuditedAsync(
                Record(tenant, id, $"{tenant.Value:N}/{id:N}.pdf"), Event(tenant, id)));
    }

    private static DocumentRecord Record(TenantId tenant, Guid id, string key) =>
        new(id, tenant, null, "proof.pdf", "application/pdf", 10, key, DateTime.UtcNow);

    private static SecurityAuditEvent Event(TenantId tenant, Guid id) =>
        new(Guid.NewGuid(), tenant, "signed-subject", "document.ingest", "succeeded", id, DateTime.UtcNow);

    private sealed class FakeBlobs : IObjectStore
    {
        public HashSet<string> Stored { get; } = [];
        public int Deletions { get; private set; }
        public bool FailDeletion { get; set; }
        public Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
        {
            Stored.Add(key);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Deletions++;
            if (FailDeletion) throw new IOException("Simulated storage outage");
            Stored.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class Databases(NpgsqlDataSource admin, NpgsqlDataSource app, NpgsqlDataSource recovery)
        : IAsyncDisposable
    {
        public NpgsqlDataSource Admin { get; } = admin;
        public NpgsqlDataSource App { get; } = app;
        public NpgsqlDataSource Recovery { get; } = recovery;

        public static async Task<Databases> OpenAsync()
        {
            var root = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
                ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required.");
            var admin = NpgsqlDataSource.Create(root);
            try
            {
                await SchemaBootstrap.ApplyDevelopmentSchemaAsync(admin);
                await TestDbRoleProvisioner.ConfigureAsync(admin);
                var appConnection = new NpgsqlConnectionStringBuilder(root)
                {
                    Username = "peritial_app", Password = "local-only-app", MaxPoolSize = 1
                };
                var recoveryConnection = new NpgsqlConnectionStringBuilder(root)
                {
                    Username = "peritial_recovery", Password = "local-only-recovery", MaxPoolSize = 1
                };
                return new Databases(admin, NpgsqlDataSource.Create(appConnection.ConnectionString),
                    NpgsqlDataSource.Create(recoveryConnection.ConnectionString));
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
