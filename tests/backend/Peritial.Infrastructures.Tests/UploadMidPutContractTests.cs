using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// Invoked only after the CI chaos gate forwarded real S3 PUT body bytes,
/// SIGKILLed the API, expired the dead lease and restarted its worker.
/// </summary>
public sealed class UploadMidPutContractTests
{
    [Fact]
    public async Task Interrupted_real_S3_PUT_has_no_document_audit_object_or_pending_key()
    {
        if (Environment.GetEnvironmentVariable("PERITIAL_TEST_MID_PUT") != "1")
            return;

        var key = Environment.GetEnvironmentVariable("PERITIAL_TEST_CHAOS_KEY")
            ?? throw new InvalidOperationException("Missing chaos S3 key");
        var rawId = Environment.GetEnvironmentVariable("PERITIAL_TEST_CHAOS_ID")
            ?? throw new InvalidOperationException("Missing chaos document ID");
        var id = Guid.Parse(rawId);
        const string tenant = "11111111-1111-1111-1111-111111111111";

        await using var admin = NpgsqlDataSource.Create(
            Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES") ??
            throw new InvalidOperationException("Missing PostgreSQL test database"));
        await using (var cmd = admin.CreateCommand(
            "SELECT " +
            "(SELECT count(*) FROM upload_intents WHERE tenant_id=@tenant AND document_id=@id), " +
            "(SELECT count(*) FROM upload_request_keys WHERE tenant_id=@tenant AND document_id=@id), " +
            "(SELECT count(*) FROM documents WHERE tenant_id=@tenant AND id=@id), " +
            "(SELECT count(*) FROM security_audit WHERE tenant_id=@tenant " +
              "AND resource_id=@id AND action='document.ingest' AND outcome='succeeded')"))
        {
            cmd.Parameters.AddWithValue("tenant", Guid.Parse(tenant));
            cmd.Parameters.AddWithValue("id", id);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            for (var col = 0; col < 4; col++)
                Assert.Equal(0L, reader.GetInt64(col));
        }

        var endpoint = Environment.GetEnvironmentVariable("PERITIAL_TEST_S3_ENDPOINT")
            ?? throw new InvalidOperationException("Missing S3 test endpoint");
        using var s3 = new AmazonS3Client(
            new BasicAWSCredentials("localminio", "local-only-minio-secret"),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });
        var missing = await Assert.ThrowsAnyAsync<AmazonS3Exception>(async () =>
            await s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = "peritial", Key = key
            }));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
