using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;

namespace Peritial.Infrastructures;

public interface IStorageReadinessProbe
{
    Task<bool> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only metadata probe for the configured document bucket/container.
/// It does not PUT, DELETE, enumerate keys, or fetch document bytes.
/// A separate identity is required outside a deliberately opted-in local demo.
/// </summary>
public sealed class StorageReadinessProbe : IStorageReadinessProbe, IDisposable
{
    private readonly IAmazonS3? _s3;
    private readonly string? _bucket;
    private readonly BlobContainerClient? _container;
    private readonly bool _ownsS3;

    private StorageReadinessProbe(IAmazonS3 client, string bucket)
    {
        _s3 = client;
        _bucket = bucket;
        _ownsS3 = true;
    }

    private StorageReadinessProbe(BlobContainerClient container) => _container = container;

    public static StorageReadinessProbe Create(IConfiguration config,
        bool allowSharedDevelopmentCredentials = false)
    {
        var provider = config["Storage:Provider"] ??
            throw new InvalidOperationException("Storage provider required.");

        if (provider.Equals("Minio", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("S3", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = Required(config["Storage:Minio:Endpoint"]);
            var bucket = Required(config["Storage:Minio:Bucket"]);
            var readerKey = config["Storage:Readiness:S3:AccessKey"];
            var readerSecret = config["Storage:Readiness:S3:SecretKey"];
            if (string.IsNullOrWhiteSpace(readerKey) != string.IsNullOrWhiteSpace(readerSecret))
                throw new InvalidOperationException("Both independent S3 readiness credentials required.");
            if (string.IsNullOrWhiteSpace(readerKey))
            {
                if (!allowSharedDevelopmentCredentials)
                    throw new InvalidOperationException("Independent S3 readiness identity required.");
                readerKey = Required(config["Storage:Minio:AccessKey"]);
                readerSecret = Required(config["Storage:Minio:SecretKey"]);
            }
            else if (readerKey == config["Storage:Minio:AccessKey"] &&
                     readerSecret == config["Storage:Minio:SecretKey"])
                throw new InvalidOperationException("S3 readiness identity must differ from the writer.");

            var client = new AmazonS3Client(
                new BasicAWSCredentials(readerKey!, readerSecret!),
                new AmazonS3Config
                {
                    ServiceURL = endpoint, ForcePathStyle = true,
                    MaxErrorRetry = 0, Timeout = TimeSpan.FromSeconds(3)
                });
            return new StorageReadinessProbe(client, bucket);
        }

        if (provider.Equals("Azure", StringComparison.OrdinalIgnoreCase))
        {
            var name = Required(config["Storage:Azure:Container"]);
            var readConnection = config["Storage:Readiness:Azure:ConnectionString"];
            var clientId = config["Storage:Readiness:Azure:ManagedIdentityClientId"];
            if (!string.IsNullOrWhiteSpace(readConnection) && !string.IsNullOrWhiteSpace(clientId))
                throw new InvalidOperationException("Choose one Azure readiness credential.");
            if (!string.IsNullOrWhiteSpace(readConnection))
            {
                if (readConnection == config["Storage:Azure:ConnectionString"])
                    throw new InvalidOperationException("Azure readiness identity must differ from the writer.");
                return new StorageReadinessProbe(new BlobContainerClient(readConnection, name));
            }
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                var serviceUri = Required(config["Storage:Azure:ServiceUri"]);
                if (!Uri.TryCreate(serviceUri, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("Managed identity requires an HTTPS Azure Blob endpoint.");
                return new StorageReadinessProbe(
                    new BlobServiceClient(uri, new ManagedIdentityCredential(clientId))
                        .GetBlobContainerClient(name));
            }
            if (!allowSharedDevelopmentCredentials)
                throw new InvalidOperationException("Independent Azure readiness identity required.");
            var localConnection = Required(config["Storage:Azure:ConnectionString"]);
            return new StorageReadinessProbe(new BlobContainerClient(localConnection, name));
        }

        throw new InvalidOperationException("Unsupported storage provider.");
    }

    public async Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_s3 is not null)
            {
                using var response = await _s3.HeadBucketAsync(
                    new HeadBucketRequest { BucketName = _bucket! }, cancellationToken);
                return response.HttpStatusCode == HttpStatusCode.OK;
            }

            // GetContainerProperties is read-only and does not enumerate blobs.
            var result = await _container!.GetPropertiesAsync(cancellationToken: cancellationToken);
            return result.GetRawResponse().Status == (int)HttpStatusCode.OK;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Fail closed with no provider error, hostname or credential in HTTP.
            return false;
        }
    }

    private static string Required(string? value) =>
        !string.IsNullOrWhiteSpace(value) ? value :
            throw new InvalidOperationException("Storage readiness configuration incomplete.");

    public void Dispose()
    {
        if (_ownsS3)
            _s3?.Dispose();
    }
}
