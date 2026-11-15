using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Peritial.Business;

namespace Peritial.Infrastructures;

public sealed class AzureBlobObjectStore : IObjectStore
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
}
