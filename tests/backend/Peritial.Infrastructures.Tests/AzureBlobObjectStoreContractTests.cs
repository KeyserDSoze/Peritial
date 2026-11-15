using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// Executes against Azurite Blob storage in the CI Compose azure-test profile.
/// Not an assertion of live Azure Managed Identity or cloud deployment.
/// </summary>
public sealed class AzureBlobObjectStoreContractTests
{
    private const string DevelopmentAccountKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    [Fact]
    public async Task Azure_provider_private_put_delete_and_repeated_delete_are_idempotent()
    {
        var endpoint = Environment.GetEnvironmentVariable("PERITIAL_TEST_AZURITE_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
            return; // Backend job does not start Azurite; Compose job runs it explicitly.

        var connectionString =
            "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
            "AccountKey=" + DevelopmentAccountKey + ";BlobEndpoint=" + endpoint + ";";
        var containerName = "contract-" + Guid.NewGuid().ToString("N");
        var container = new BlobContainerClient(connectionString, containerName);
        await container.CreateIfNotExistsAsync(publicAccessType: PublicAccessType.None);

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Storage:Azure:ConnectionString"] = connectionString,
                ["Storage:Azure:Container"] = containerName
            }).Build();

        var store = new AzureBlobObjectStore(config);
        var key = "contract-tests/" + Guid.NewGuid().ToString("N") + ".pdf";
        byte[] bytes = "%PDF-1.7\nAzure Blob contract test\n%%EOF\n"u8.ToArray();
        var blob = container.GetBlobClient(key);

        try
        {
            var properties = (await container.GetPropertiesAsync()).Value;
            Assert.Equal(PublicAccessType.None, properties.PublicAccess);

            using var stream = new MemoryStream(bytes, writable: false);
            await store.PutAsync(key, stream, "application/pdf");
            Assert.True((await blob.ExistsAsync()).Value);
            using var downloaded = new MemoryStream();
            await blob.DownloadToAsync(downloaded);
            Assert.Equal(bytes, downloaded.ToArray());

            await store.DeleteAsync(key);
            await store.DeleteAsync(key); // Deleting already absent blob must succeed.
            Assert.False((await blob.ExistsAsync()).Value);
        }
        finally
        {
            await store.DeleteAsync(key);
            await container.DeleteIfExistsAsync();
        }
    }
}
