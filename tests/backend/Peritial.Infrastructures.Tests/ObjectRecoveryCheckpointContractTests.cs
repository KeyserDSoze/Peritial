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
        var secondTenant = Guid.NewGuid();
        var secondDocument = Guid.NewGuid();
        var secondKey = "checkpoint-contract/" + Guid.NewGuid().ToString("N") + ".pdf";
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

            // An ordinary application login must not be allowed to capture an
            // apparently empty subset of a FORCE-RLS tenant database.
            var ordinaryApp = new NpgsqlConnectionStringBuilder(cs)
            {
                Username = "peritial_app",
                Password = "local-only-app",
                SearchPath = schema
            };
            await using (var restricted = NpgsqlDataSource.Create(ordinaryApp.ConnectionString))
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    ObjectRecoveryCheckpoint.CaptureAsync(restricted, storage, bucket));

            await using (var conn = await db.OpenConnectionAsync())
            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO documents(tenant_id,id,perizia_id,file_name,content_type,
                                      length_bytes,storage_key,created_at_utc,content_sha256)
                VALUES(@tenant,@doc,NULL,'checkpoint.pdf','application/pdf',
                       @length,@key,now(),@sha),
                      (@tenant2,@doc2,NULL,'second-checkpoint.pdf','application/pdf',
                       @length,@key2,now(),@sha)
                """, conn))
            {
                insert.Parameters.AddWithValue("tenant", tenant);
                insert.Parameters.AddWithValue("doc", doc);
                insert.Parameters.AddWithValue("tenant2", secondTenant);
                insert.Parameters.AddWithValue("doc2", secondDocument);
                insert.Parameters.AddWithValue("length", (long)original.Length);
                // This fixture represents newly accepted documents under V014,
                // not a pre-SHA legacy row. Both share the exact S3 body bytes.
                insert.Parameters.Add("sha", NpgsqlTypes.NpgsqlDbType.Bytea).Value =
                    System.Security.Cryptography.SHA256.HashData(original);
                insert.Parameters.AddWithValue("key", key);
                insert.Parameters.AddWithValue("key2", secondKey);
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

            // In-progress idempotency records also mean the checkpoint is unsafe.
            await using (var conn = await db.OpenConnectionAsync())
            await using (var pendingKey = new NpgsqlCommand(
                """
                INSERT INTO upload_request_keys(
                    tenant_id,subject_digest,key_digest,request_digest,document_id,status)
                VALUES (@tenant, decode(repeat('aa',32),'hex'),
                        decode(repeat('bb',32),'hex'), decode(repeat('cc',32),'hex'),
                        @id, 'pending')
                """, conn))
            {
                pendingKey.Parameters.AddWithValue("tenant", tenant);
                pendingKey.Parameters.AddWithValue("id", pending);
                await pendingKey.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryCheckpoint.CaptureAsync(db, storage, bucket));
            await using (var conn = await db.OpenConnectionAsync())
            await using (var clear = new NpgsqlCommand("DELETE FROM upload_request_keys", conn))
                await clear.ExecuteNonQueryAsync();

            // Referenced object missing: fail, do not report a recoverable point.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryCheckpoint.CaptureAsync(db, storage, bucket));

            await PutAsync(storage, bucket, key, original);
            await PutAsync(storage, bucket, secondKey, original);

            // The first checkpoint must NOT bless already substituted S3
            // bytes. The ZIP/PDF may still be valid and the length equal,
            // but PostgreSQL holds the SHA-256 from the admitted upload.
            await PutAsync(storage, bucket, key, tampered);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryCheckpoint.CaptureAsync(db, storage, bucket));
            await PutAsync(storage, bucket, key, original);

            var checkpoint = await ObjectRecoveryCheckpoint.CaptureAsync(db, storage, bucket);
            Assert.Equal(1, checkpoint.FormatVersion);
            Assert.Equal(2, checkpoint.Documents.Count);
            Assert.Contains(checkpoint.Documents, x => x.TenantId == tenant && x.DocumentId == doc);
            Assert.Contains(checkpoint.Documents, x => x.TenantId == secondTenant && x.DocumentId == secondDocument);
            await ObjectRecoveryCheckpoint.VerifyAsync(db, storage, bucket, checkpoint);

            // Even a syntactically correct manifest digest cannot silently
            // disagree with the admitted PostgreSQL checksum. Validate the
            // metadata before any restored object write or S3 verification.
            var falseDigest = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(tampered)).ToLowerInvariant();
            var alteredCheckpoint = checkpoint with
            {
                Documents = checkpoint.Documents.Select((x, i) =>
                    i == 0 ? x with { Sha256 = falseDigest } : x).ToArray()
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryCheckpoint.VerifyMetadataAsync(db, alteredCheckpoint));

            await PutAsync(storage, bucket, key, tampered);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryCheckpoint.VerifyAsync(db, storage, bucket, checkpoint));
            await PutAsync(storage, bucket, key, original);
            await ObjectRecoveryCheckpoint.VerifyAsync(db, storage, bucket, checkpoint);

            // Identical S3 key reused across tenants is ambiguous even though
            // PostgreSQL uniqueness is scoped by tenant; reject the checkpoint.
            await using (var conn = await db.OpenConnectionAsync())
            await using (var collide = new NpgsqlCommand(
                "UPDATE documents SET storage_key=@same WHERE tenant_id=@tenant AND id=@id", conn))
            {
                collide.Parameters.AddWithValue("same", key);
                collide.Parameters.AddWithValue("tenant", secondTenant);
                collide.Parameters.AddWithValue("id", secondDocument);
                Assert.Equal(1, await collide.ExecuteNonQueryAsync());
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryCheckpoint.CaptureAsync(db, storage, bucket));
            await using (var conn = await db.OpenConnectionAsync())
            await using (var recover = new NpgsqlCommand(
                "UPDATE documents SET storage_key=@original WHERE tenant_id=@tenant AND id=@id", conn))
            {
                recover.Parameters.AddWithValue("original", secondKey);
                recover.Parameters.AddWithValue("tenant", secondTenant);
                recover.Parameters.AddWithValue("id", secondDocument);
                await recover.ExecuteNonQueryAsync();
            }

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
            await storage.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = secondKey });
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
