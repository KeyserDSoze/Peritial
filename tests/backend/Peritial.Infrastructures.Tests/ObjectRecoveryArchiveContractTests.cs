using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>Real independent PostgreSQL and SeaweedFS servers in Compose CI.</summary>
public sealed class ObjectRecoveryArchiveContractTests
{
    [Fact]
    public async Task Export_and_import_document_bytes_to_separate_S3_and_Postgres_rejects_tampering_and_overwrite()
    {
        var sourceEndpoint = Environment.GetEnvironmentVariable("PERITIAL_TEST_S3_ENDPOINT");
        var destEndpoint = Environment.GetEnvironmentVariable("PERITIAL_TEST_S3_RESTORE_ENDPOINT");
        var sourcePg = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES");
        var destPg = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES_RESTORE");
        if (string.IsNullOrWhiteSpace(sourceEndpoint) ||
            string.IsNullOrWhiteSpace(destEndpoint) ||
            string.IsNullOrWhiteSpace(sourcePg) || string.IsNullOrWhiteSpace(destPg))
            return;

        var schema = "archive_" + Guid.NewGuid().ToString("N");
        var archive = Path.Combine(Path.GetTempPath(), "archive-" + Guid.NewGuid().ToString("N"));
        var tenantOne = Guid.NewGuid();
        var tenantTwo = Guid.NewGuid();
        var docOne = Guid.NewGuid();
        var docTwo = Guid.NewGuid();
        var keyOne = "archive-contract/" + Guid.NewGuid().ToString("N") + ".pdf";
        var keyTwo = "archive-contract/" + Guid.NewGuid().ToString("N") + ".pdf";
        byte[] first = "%PDF-1.7\nfirst tenant\n%%EOF\n"u8.ToArray();
        byte[] second = "%PDF-1.7\nsecond tenant\n%%EOF\n"u8.ToArray();
        const string bucket = "peritial";

        await using var sourceAdmin = NpgsqlDataSource.Create(sourcePg);
        await using var destAdmin = NpgsqlDataSource.Create(destPg);
        using var original = Client(sourceEndpoint);
        using var restored = Client(destEndpoint);

        foreach (var pg in new[] { sourceAdmin, destAdmin })
        {
            await using var conn = await pg.OpenConnectionAsync();
            await using var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", conn);
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var sourceBuilder = new NpgsqlConnectionStringBuilder(sourcePg) { SearchPath = schema };
            var destBuilder = new NpgsqlConnectionStringBuilder(destPg) { SearchPath = schema };
            await using var sourceDb = NpgsqlDataSource.Create(sourceBuilder.ConnectionString);
            await using var destDb = NpgsqlDataSource.Create(destBuilder.ConnectionString);
            await SchemaBootstrap.ApplyDevelopmentSchemaAsync(sourceDb);
            await SchemaBootstrap.ApplyDevelopmentSchemaAsync(destDb);
            foreach (var pg in new[] { sourceDb, destDb })
            {
                await using var conn = await pg.OpenConnectionAsync();
                await using var add = new NpgsqlCommand(
                    """
                    INSERT INTO documents(tenant_id,id,perizia_id,file_name,content_type,
                                          length_bytes,storage_key,created_at_utc)
                    VALUES (@tenant1,@id1,NULL,'first.pdf','application/pdf',@len1,@key1,now()),
                           (@tenant2,@id2,NULL,'second.pdf','application/pdf',@len2,@key2,now())
                    """, conn);
                add.Parameters.AddWithValue("tenant1", tenantOne);
                add.Parameters.AddWithValue("tenant2", tenantTwo);
                add.Parameters.AddWithValue("id1", docOne);
                add.Parameters.AddWithValue("id2", docTwo);
                add.Parameters.AddWithValue("len1", (long)first.Length);
                add.Parameters.AddWithValue("len2", (long)second.Length);
                add.Parameters.AddWithValue("key1", keyOne);
                add.Parameters.AddWithValue("key2", keyTwo);
                await add.ExecuteNonQueryAsync();
            }

            await PutAsync(original, bucket, keyOne, first);
            await PutAsync(original, bucket, keyTwo, second);
            var exported = await ObjectRecoveryArchive.ExportAsync(sourceDb, original, bucket, archive);
            Assert.Equal(2, exported.Checkpoint.Documents.Count);
            Assert.Equal(1, exported.ArchiveVersion);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryArchive.ExportAsync(sourceDb, original, bucket, archive));

            var blob0 = Path.Combine(archive, "objects", "00000000.blob");
            var bytes = await File.ReadAllBytesAsync(blob0);
            bytes[7] ^= 0x20; // same length; invalid digest
            await File.WriteAllBytesAsync(blob0, bytes);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryArchive.ImportAsync(destDb, restored, bucket, archive));
            var empty = await restored.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
            Assert.True(empty.S3Objects is null || empty.S3Objects.Count == 0); // SDK v4 returns null for no keys

            bytes[7] ^= 0x20;
            await File.WriteAllBytesAsync(blob0, bytes);

            // A single forged source/storage reference in a manifest is not
            // permitted to write any objects to the new S3 destination.
            var mpath = Path.Combine(archive, "manifest.json");
            var manifestBytes = await File.ReadAllBytesAsync(mpath);
            var altered = System.Text.Encoding.UTF8.GetString(manifestBytes)
                .Replace(keyOne, "forged-" + keyOne, StringComparison.Ordinal);
            Assert.NotEqual(System.Text.Encoding.UTF8.GetString(manifestBytes), altered);
            await File.WriteAllTextAsync(mpath, altered);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryArchive.ImportAsync(destDb, restored, bucket, archive));
            await File.WriteAllBytesAsync(mpath, manifestBytes);

            var imported = await ObjectRecoveryArchive.ImportAsync(destDb, restored, bucket, archive);
            Assert.Equal(2, imported);
            await ObjectRecoveryCheckpoint.VerifyAsync(destDb, restored, bucket, exported.Checkpoint);

            // A second import must not overwrite any previously restored bytes.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryArchive.ImportAsync(destDb, restored, bucket, archive));

            // Destination corruption is detected from persisted bytes, not ETags.
            var corrupt = first.ToArray();
            corrupt[9] ^= 0x20;
            await PutAsync(restored, bucket, keyOne, corrupt);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryCheckpoint.VerifyAsync(destDb, restored, bucket, exported.Checkpoint));
        }
        finally
        {
            foreach (var (s3, key) in new[]
            {
                (original, keyOne), (original, keyTwo),
                (restored, keyOne), (restored, keyTwo)
            })
                await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
            foreach (var pg in new[] { sourceAdmin, destAdmin })
            {
                await using var conn = await pg.OpenConnectionAsync();
                await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", conn);
                await drop.ExecuteNonQueryAsync();
            }
            if (Directory.Exists(archive))
                Directory.Delete(archive, true);
        }
    }

    private static AmazonS3Client Client(string endpoint) =>
        new(new BasicAWSCredentials("localminio", "local-only-minio-secret"),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });

    private static async Task PutAsync(IAmazonS3 s3, string bucket, string key, byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = key, InputStream = stream,
            ContentType = "application/pdf", AutoCloseStream = false
        });
    }
}
