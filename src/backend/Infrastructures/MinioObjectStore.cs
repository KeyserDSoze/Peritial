using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Peritial.Business;

namespace Peritial.Infrastructures;

/// <summary>Uses S3-compatible MinIO. Bucket provisioning is handled by docker/minio-init.</summary>
public sealed class MinioObjectStore : IObjectStore, IDocumentBlobReader, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    public MinioObjectStore(IConfiguration configuration)
    {
        var endpoint = configuration["Storage:Minio:Endpoint"] ?? throw new InvalidOperationException("MinIO endpoint missing.");
        var access = configuration["Storage:Minio:AccessKey"] ?? throw new InvalidOperationException("MinIO key missing.");
        var secret = configuration["Storage:Minio:SecretKey"] ?? throw new InvalidOperationException("MinIO secret missing.");
        _bucket = configuration["Storage:Minio:Bucket"] ?? "peritial";
        var options = new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true };
        _client = new AmazonS3Client(new BasicAWSCredentials(access, secret), options);
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket, Key = key, InputStream = content,
            ContentType = contentType, AutoCloseStream = false
        }, ct);
    }

    public async Task<byte[]> ReadAsync(string key, long expectedLength, CancellationToken ct = default)
    {
        // The Business layer has already resolved the key through a tenant-
        // scoped PostgreSQL row. S3 response owns the stream lifetime.
        using var response = await _client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _bucket, Key = key
        }, ct);
        if (response.ContentLength != expectedLength)
            throw new InvalidDataException("Stored document length does not match metadata.");
        return await BoundedDocumentRead.ReadExactAsync(response.ResponseStream, expectedLength, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        // S3 DeleteObject is idempotent when the key no longer exists.
        await _client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _bucket,
            Key = key
        }, ct);
    }

    public void Dispose() => _client.Dispose();
}
