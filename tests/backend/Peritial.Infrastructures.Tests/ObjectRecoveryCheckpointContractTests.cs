using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// Real SeaweedFS+PostgreSQL test, run explicitly from the Compose CI job.
/// Each test uses an isolated schema and a unique S3 key; never a tenant's data.
/// </summary>
public sealed class ObjectRecoveryCheckpointContractTests
{
    [Fact]
    public async Task Offline_checkpoint_verifies_content_hash_and_refuses_missing_blob_or_db_drift()
    {
        var endpoint = Environment.GetEnvironmentVariable("PERITIAL_TEST_S3_ENDPOINT");
        var cs = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(cs))
            return;

        var schema = "checkpoint_" + Guid.NewGuid().ToString("N");
        const string bucket = "peritial";
        var key = "checkpoint-contract/" + Guid.NewGuid().ToString("N") + ".pdf";
        var tenant = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var pending = Guid.NewGuid();
        byte[] original = "%PDF-1.7\ncheckpoint-content\n%%EOF\n"u8.ToArray();
        byte[] tampered = original.ToArray();
        tampered[15] ^= 0x20; // same length but different SHA-256
        await using var admin = NpgsqlDataSource.Create(cs);
        using var storage = new AmazonS3Client(
            new BasicAWSCredentials("localminio", "local-only-minio-secret"),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });

        await using (var conn = await admin.OpenConnectionAsync())
        await using (var ddl = new NpgsqlCommand($"CREATE SCHEMA {schema}", conn))
            await ddl.ExecuteNonQueryAsync();
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(cs) { SearchPath = schema };
            await using var db = NpgsqlDataSource.Create(builder.ConnectionString);
            await SchemaBootstrap.ApplyDevelopmentSchemaAsync(db);
            await using (var conn = await db.OpenConnectionAsync())
            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO documents(tenant_id,id,perizia_id,file_name,content_type,
                                      length_bytes,storage_key,created_at_utc)
                VALUES(@tenant,@doc,NULL,'checkpoint.pdf','application/pdf',
                       @length,@key,now())
                """, conn))
            {
                insert.Parameters.AddWithValue("tenant", tenant);
                insert.Parameters.AddWithValue("doc", doc);
                insert.Parameters.AddWithValue("length", (long)original.Length);
                insert.Parameters.AddWithValue("key", key);
                await insert.ExecuteNonQueryAsync();
            }

            // Pending object PUTs cannot be treated as a consistent recovery point.
            await using (var conn = await db.OpenConnectionAsync())
            await using (var pendingInsert = new NpgsqlCommand(
                "INSERT INTO upload_intents(tenant_id,document_id,storage_key) " +
                "VALUES (@tenant,@id,'checkpoint-contract/pending')", conn))
            {
                pendingInsert.Parameters.AddWithValue("tenant", tenant);
                pendingInsert.Parameters.AddWithValue("id", pending);
                await pendingInsert.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryCheckpoint.CaptureAsync(db, storage, bucket));
            await using (var conn = await db.OpenConnectionAsync())
            await using (var clear = new NpgsqlCommand("DELETE FROM upload_intents", conn))
                await clear.ExecuteNonQueryAsync();

            // Referenced object missing: fail, do not report a recoverable point.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryCheckpoint.CaptureAsync(db, storage, bucket));

            await PutAsync(storage, bucket, key, original);
            var checkpoint = await ObjectRecoveryCheckpoint.CaptureAsync(db, storage, bucket);
            Assert.Equal(1, checkpoint.FormatVersion);
            Assert.Single(checkpoint.Documents);
            Assert.Equal(tenant, checkpoint.Documents[0].TenantId);
            Assert.Equal(doc, checkpoint.Documents[0].DocumentId);
            await ObjectRecoveryCheckpoint.VerifyAsync(db, storage, bucket, checkpoint);

            await PutAsync(storage, bucket, key, tampered);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryCheckpoint.VerifyAsync(db, storage, bucket, checkpoint));
            await PutAsync(storage, bucket, key, original);
            await ObjectRecoveryCheckpoint.VerifyAsync(db, storage, bucket, checkpoint);

            // A DB-only restore missing metadata must never pass the checkpoint.
            await using (var conn = await db.OpenConnectionAsync())
            await using (var delete = new NpgsqlCommand("DELETE FROM documents", conn))
                await delete.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryCheckpoint.VerifyAsync(db, storage, bucket, checkpoint));

            // Reject unsupported checkpoint versions as well.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryCheckpoint.VerifyAsync(db, storage, bucket,
                    checkpoint with { FormatVersion = 999 }));
        }
        finally
        {
            await storage.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
            await using var conn = await admin.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task PutAsync(IAmazonS3 storage, string bucket, string key, byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        await storage.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = input,
            ContentType = "application/pdf",
            AutoCloseStream = false
        });
    }
}
