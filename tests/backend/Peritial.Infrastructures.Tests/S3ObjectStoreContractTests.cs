using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// Runs against the actual Compose SeaweedFS S3 endpoint when
/// PERITIAL_TEST_S3_ENDPOINT is present. CI invokes this test explicitly.
/// </summary>
public sealed class S3ObjectStoreContractTests
{
    [Fact]
    public async Task S3_provider_put_delete_and_repeated_delete_are_idempotent()
    {
        var endpoint = Environment.GetEnvironmentVariable("PERITIAL_TEST_S3_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
            return; // Backend unit test job has PostgreSQL but not the S3 emulator.

        const string bucket = "peritial";
        const string access = "localminio";
        const string secret = "local-only-minio-secret";
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Storage:Minio:Endpoint"] = endpoint,
                ["Storage:Minio:AccessKey"] = access,
                ["Storage:Minio:SecretKey"] = secret,
                ["Storage:Minio:Bucket"] = bucket
            }).Build();

        using var store = new MinioObjectStore(config);
        using var inspect = new AmazonS3Client(
            new BasicAWSCredentials(access, secret),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });

        var key = "contract-tests/" + Guid.NewGuid().ToString("N") + ".pdf";
        byte[] content = "%PDF-1.7\nS3 contract test\n%%EOF\n"u8.ToArray();
        try
        {
            using var input = new MemoryStream(content, writable: false);
            await store.PutAsync(key, input, "application/pdf");
            using var read = await inspect.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket, Key = key
            });
            using var saved = new MemoryStream();
            await read.ResponseStream.CopyToAsync(saved);
            Assert.Equal(content, saved.ToArray());

            await store.DeleteAsync(key);
            await store.DeleteAsync(key); // Missing object must be safe to delete.
            var missing = await Assert.ThrowsAnyAsync<AmazonS3Exception>(async () =>
                await inspect.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await store.DeleteAsync(key); // Also clean up if an assertion fails.
        }
    }
}
