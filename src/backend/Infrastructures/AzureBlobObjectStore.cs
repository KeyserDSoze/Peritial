using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Peritial.Business;

namespace Peritial.Infrastructures;

public sealed class AzureBlobObjectStore : IObjectStore, IDocumentBlobReader
{
    private readonly BlobContainerClient _container;

    public AzureBlobObjectStore(IConfiguration configuration)
    {
        var name = configuration["Storage:Azure:Container"] ?? "documents";
        var connectionString = configuration["Storage:Azure:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(connectionString))
            _container = new BlobContainerClient(connectionString, name);
        else
        {
            var uri = configuration["Storage:Azure:ServiceUri"] ?? throw new InvalidOperationException("Storage Azure ServiceUri missing.");
            _container = new BlobServiceClient(new Uri(uri), new DefaultAzureCredential()).GetBlobContainerClient(name);
        }
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(key);
        await blob.UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        }, ct);
    }

    public async Task<byte[]> ReadAsync(string key, long expectedLength, CancellationToken ct = default)
    {
        // No public blob URL or SAS is returned. Azure downloads are read
        // into bounded memory and verified against PostgreSQL byte length.
        var response = await _container.GetBlobClient(key).DownloadStreamingAsync(
            cancellationToken: ct);
        await using var stream = response.Value.Content;
        return await BoundedDocumentRead.ReadExactAsync(stream, expectedLength, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        // DeleteIfExists is safe to retry; also remove snapshots if present.
        await _container.GetBlobClient(key).DeleteIfExistsAsync(
            DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
    }
}
