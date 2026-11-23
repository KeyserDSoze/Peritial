using Npgsql;
using Peritial.Business;
using Peritial.Domains;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class UploadIdempotencyTests
{
    private static readonly byte[] Digest = Enumerable.Repeat((byte)0x41, 32).ToArray();
    private const string Key = "retry-application-request-00000001";

    [Fact]
    public async Task Concurrent_reservations_admit_exactly_one_writer_and_do_not_expose_other_subject()
    {
        await using var db = await TestDb.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var store = new PgUploadIdempotencyStore(db.App);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var pair = await Task.WhenAll(
            store.ReserveAsync(tenant, "actor-A", Key, Digest, first),
            store.ReserveAsync(tenant, "actor-A", Key, Digest, second));
        Assert.Equal(1, pair.Count(x => x.State == UploadIdempotencyState.Acquired));
        Assert.Equal(1, pair.Count(x => x.State == UploadIdempotencyState.InProgress));

        var anotherActor = await store.ReserveAsync(
            tenant, "actor-B", Key, Digest, Guid.NewGuid());
        Assert.Equal(UploadIdempotencyState.Acquired, anotherActor.State);
        var anotherTenant = await store.ReserveAsync(
            TenantId.From(Guid.NewGuid()), "actor-A", Key, Digest, Guid.NewGuid());
        Assert.Equal(UploadIdempotencyState.Acquired, anotherTenant.State);
    }

    [Fact]
    public async Task Same_key_with_different_payload_is_conflict_not_another_upload()
    {
        await using var db = await TestDb.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var store = new PgUploadIdempotencyStore(db.App);
        await store.ReserveAsync(tenant, "actor-A", Key, Digest, Guid.NewGuid());
        var different = Enumerable.Repeat((byte)0x42, 32).ToArray();
        await Assert.ThrowsAsync<UploadIdempotencyConflictException>(() =>
            store.ReserveAsync(tenant, "actor-A", Key, different, Guid.NewGuid()));
    }

    [Fact]
    public async Task Successful_atomic_metadata_and_audit_replay_the_same_document_without_new_audit()
    {
        await using var db = await TestDb.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var key = $"{tenant.Value:N}/{id:N}.pdf";
        var store = new PgUploadIdempotencyStore(db.App);
        var acquired = await store.ReserveAsync(tenant, "actor-A", Key, Digest, id);
        Assert.Equal(UploadIdempotencyState.Acquired, acquired.State);
        await new PgUploadIntentLedger(db.App).RecordAsync(tenant, id, key);
        var record = new DocumentRecord(id, tenant, null, "evidence.pdf",
            "application/pdf", 20, key, DateTime.UtcNow,
            RequiresIdempotency: true, ContentSha256: DocumentDigest.Compute("%PDF-1.7"u8));
        await new PgDocumentRepository(db.App).AddAuditedAsync(record,
            new SecurityAuditEvent(Guid.NewGuid(), tenant, "actor-A",
                "document.ingest", "succeeded", id, DateTime.UtcNow));

        var replay = await store.ReserveAsync(tenant, "actor-A", Key, Digest, Guid.NewGuid());
        Assert.Equal(UploadIdempotencyState.Completed, replay.State);
        Assert.NotNull(replay.Document);
        Assert.Equal(id, replay.Document.Id);
        Assert.Equal(record.FileName, replay.Document.FileName);

        await using var cmd = db.Admin.CreateCommand(
            "SELECT (SELECT count(*) FROM documents WHERE tenant_id=@tenant AND id=@id), " +
            "(SELECT count(*) FROM security_audit WHERE tenant_id=@tenant AND resource_id=@id AND action='document.ingest')");
        cmd.Parameters.AddWithValue("tenant", tenant.Value);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task Cleared_failed_request_can_be_retried_with_same_key_and_new_document_id()
    {
        await using var db = await TestDb.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var store = new PgUploadIdempotencyStore(db.App);
        var first = Guid.NewGuid();
        Assert.Equal(UploadIdempotencyState.Acquired,
            (await store.ReserveAsync(tenant, "actor-A", Key, Digest, first)).State);
        await store.ReleaseAsync(tenant, first);
        Assert.Equal(UploadIdempotencyState.Acquired,
            (await store.ReserveAsync(tenant, "actor-A", Key, Digest, Guid.NewGuid())).State);
    }

    [Fact]
    public async Task Recovery_releases_stale_pending_key_for_safe_retry()
    {
        await using var db = await TestDb.OpenAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var key = $"{tenant.Value:N}/{id:N}.pdf";
        var store = new PgUploadIdempotencyStore(db.App);
        Assert.Equal(UploadIdempotencyState.Acquired,
            (await store.ReserveAsync(tenant, "actor-A", Key, Digest, id)).State);
        await new PgUploadIntentLedger(db.App).RecordAsync(tenant, id, key);

        await using (var expiry = db.Admin.CreateCommand(
            "UPDATE upload_intents SET created_at_utc=now()-interval '2 hours', " +
            "lease_expires_at_utc=now()-interval '1 minute' " +
            "WHERE tenant_id=@tenant AND document_id=@id"))
        {
            expiry.Parameters.AddWithValue("tenant", tenant.Value);
            expiry.Parameters.AddWithValue("id", id);
            Assert.Equal(1, await expiry.ExecuteNonQueryAsync());
        }

        var blobs = new InMemoryBlobs();
        await blobs.PutAsync(key, new MemoryStream("%PDF-1.7"u8.ToArray()), "application/pdf");
        var worker = new PgUploadReconciler(db.Recovery, blobs);
        Assert.True(await worker.ReconcileOneAsync(DateTime.UtcNow.AddMinutes(5)));
        Assert.False(blobs.Contains(key));
        Assert.Equal(UploadIdempotencyState.Acquired,
            (await store.ReserveAsync(tenant, "actor-A", Key, Digest, Guid.NewGuid())).State);
    }

    private sealed class InMemoryBlobs : IObjectStore
    {
        private readonly HashSet<string> _keys = [];
        public bool Contains(string key) => _keys.Contains(key);
        public Task PutAsync(string key, Stream bytes, string mime, CancellationToken ct = default)
        {
            _keys.Add(key);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            _keys.Remove(key);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Recovery_role_cannot_read_subject_or_request_digest()
    {
        await using var db = await TestDb.OpenAsync();
        await using var conn = await db.Recovery.OpenConnectionAsync();
        foreach (var sql in new[]
        {
            "SELECT subject_digest FROM upload_request_keys",
            "SELECT key_digest FROM upload_request_keys",
            "SELECT request_digest FROM upload_request_keys"
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var ex = await Assert.ThrowsAsync<PostgresException>(
                async () => await cmd.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    private sealed class TestDb(NpgsqlDataSource admin, NpgsqlDataSource app, NpgsqlDataSource recovery)
        : IAsyncDisposable
    {
        public NpgsqlDataSource Admin { get; } = admin;
        public NpgsqlDataSource App { get; } = app;
        public NpgsqlDataSource Recovery { get; } = recovery;

        public static async Task<TestDb> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
                ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required");
            var admin = NpgsqlDataSource.Create(cs);
            try
            {
                await SchemaBootstrap.ApplyDevelopmentSchemaAsync(admin);
                await TestDbRoleProvisioner.ConfigureAsync(admin);
                string ForRole(string username, string password) =>
                    new NpgsqlConnectionStringBuilder(cs)
                    { Username = username, Password = password, MaxPoolSize = 3 }.ConnectionString;
                return new TestDb(admin,
                    NpgsqlDataSource.Create(ForRole("peritial_app", "local-only-app")),
                    NpgsqlDataSource.Create(ForRole("peritial_recovery", "local-only-recovery")));
            }
            catch
            {
                await admin.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Recovery.DisposeAsync();
            await App.DisposeAsync();
            await Admin.DisposeAsync();
        }
    }
}
