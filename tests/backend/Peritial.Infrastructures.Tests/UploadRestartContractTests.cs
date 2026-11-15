using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Peritial.Business;
using Peritial.Domains;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// The Compose workflow SIGKILLs the API, seeds an abandoned PUT with actual S3
/// and PostgreSQL while the worker is down, restarts it, then verifies cleanup.
/// This is real worker restart/recovery, not an in-flight HTTP crash injection.
/// </summary>
public sealed class UploadRestartContractTests
{
    private static readonly Guid Tenant = Guid.Parse("55f4fddc-dca2-4c30-bef7-a8fcaf4ae611");
    private static readonly Guid DocumentId = Guid.Parse("eb02935f-119a-4ed2-8a60-144eed92e6a9");
    private static readonly string Key = $"{Tenant:N}/{DocumentId:N}.pdf";
    private const string RetryKey = "e2e-worker-restart-retry-0001";
    private static readonly byte[] Digest = Enumerable.Repeat((byte)0x37, 32).ToArray();

    [Fact]
    public async Task Seed_abandoned_object_after_api_is_abruptly_stopped()
    {
        if (!Enabled()) return;
        await using var admin = NpgsqlDataSource.Create(AdminConnection());
        await using var app = NpgsqlDataSource.Create(AppConnection());
        var tenant = TenantId.From(Tenant);
        var reservation = await new PgUploadIdempotencyStore(app).ReserveAsync(
            tenant, "worker-restart-subject", RetryKey, Digest, DocumentId);
        Assert.Equal(UploadIdempotencyState.Acquired, reservation.State);
        await new PgUploadIntentLedger(app).RecordAsync(tenant, DocumentId, Key);

        using var store = Store();
        using var stream = new MemoryStream("%PDF-1.7\norphan after crash\n%%EOF\n"u8.ToArray());
        await store.PutAsync(Key, stream, "application/pdf");

        // Time travel only the orphan's database state; never wait an hour in CI.
        // The API is killed, so no live writer can renew the lease or commit.
        await using var update = admin.CreateCommand(
            "UPDATE upload_intents SET created_at_utc=now()-interval '2 hours', " +
            "lease_expires_at_utc=now()-interval '1 minute' " +
            "WHERE tenant_id=@tenant AND document_id=@id");
        update.Parameters.AddWithValue("tenant", Tenant);
        update.Parameters.AddWithValue("id", DocumentId);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Restarted_worker_removed_orphan_and_released_pending_key()
    {
        if (!Enabled()) return;
        await using var admin = NpgsqlDataSource.Create(AdminConnection());
        await using (var cmd = admin.CreateCommand(
            "SELECT " +
            "(SELECT count(*) FROM upload_intents WHERE tenant_id=@tenant AND document_id=@id), " +
            "(SELECT count(*) FROM upload_request_keys WHERE tenant_id=@tenant AND document_id=@id), " +
            "(SELECT count(*) FROM documents WHERE tenant_id=@tenant AND id=@id)"))
        {
            cmd.Parameters.AddWithValue("tenant", Tenant);
            cmd.Parameters.AddWithValue("id", DocumentId);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
            Assert.Equal(0L, reader.GetInt64(2));
        }
        var endpoint = Environment.GetEnvironmentVariable("PERITIAL_TEST_S3_ENDPOINT")!;
        using var client = new AmazonS3Client(
            new BasicAWSCredentials("localminio", "local-only-minio-secret"),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });
        var missing = await Assert.ThrowsAnyAsync<AmazonS3Exception>(async () =>
            await client.GetObjectAsync(new GetObjectRequest { BucketName = "peritial", Key = Key }));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static bool Enabled() =>
        Environment.GetEnvironmentVariable("PERITIAL_TEST_RESTART") == "1" &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PERITIAL_TEST_S3_ENDPOINT"));

    private static string AdminConnection() =>
        Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES") ??
        throw new InvalidOperationException("Test PostgreSQL connection missing.");

    private static string AppConnection() =>
        new NpgsqlConnectionStringBuilder(AdminConnection())
        {
            Username = "peritial_app",
            Password = "local-only-app"
        }.ConnectionString;

    private static MinioObjectStore Store()
    {
        var endpoint = Environment.GetEnvironmentVariable("PERITIAL_TEST_S3_ENDPOINT") ??
            throw new InvalidOperationException("Test S3 endpoint missing.");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Storage:Minio:Endpoint"] = endpoint,
                ["Storage:Minio:AccessKey"] = "localminio",
                ["Storage:Minio:SecretKey"] = "local-only-minio-secret",
                ["Storage:Minio:Bucket"] = "peritial"
            }).Build();
        return new MinioObjectStore(configuration);
    }
}
