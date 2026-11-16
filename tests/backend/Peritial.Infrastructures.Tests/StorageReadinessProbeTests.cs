using System.Net;
using System.Net.Sockets;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>Production must not silently reuse mutating storage credentials.</summary>
public sealed class StorageReadinessProbeTests
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    [Fact]
    public void S3_production_requires_separate_read_credentials()
    {
        var config = S3Configuration("http://localhost:8333");
        Assert.Throws<InvalidOperationException>(() => StorageReadinessProbe.Create(config));
        Assert.Throws<InvalidOperationException>(() => StorageReadinessProbe.Create(
            S3Configuration("http://localhost:8333", readerKey: "localminio", readerSecret: "local-only-minio-secret")));
    }

    [Fact]
    public void Azure_production_requires_separate_read_identity()
    {
        var config = AzureConfiguration("http://localhost:10000/devstoreaccount1");
        Assert.Throws<InvalidOperationException>(() => StorageReadinessProbe.Create(config));
    }

    [Fact]
    public void Unknown_storage_provider_fails_closed()
    {
        var config = Config(new Dictionary<string, string?> { ["Storage:Provider"] = "Other" });
        Assert.Throws<InvalidOperationException>(() => StorageReadinessProbe.Create(config, allowSharedDevelopmentCredentials: true));
    }

    [Fact]
    public async Task S3_bucket_metadata_is_read_only_and_unavailable_server_is_not_ready()
    {
        var endpoint = Environment.GetEnvironmentVariable("PERITIAL_TEST_S3_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
            return;
        using var probe = StorageReadinessProbe.Create(S3Configuration(endpoint), allowSharedDevelopmentCredentials: true);
        Assert.True(await probe.CheckAsync());

        using var missingBucket = StorageReadinessProbe.Create(
            S3Configuration(endpoint, bucket: "missing-readiness-" + Guid.NewGuid().ToString("N")),
            allowSharedDevelopmentCredentials: true);
        Assert.False(await missingBucket.CheckAsync());

        var deadPort = UnusedPort();
        using var down = StorageReadinessProbe.Create(S3Configuration("http://127.0.0.1:" + deadPort),
            allowSharedDevelopmentCredentials: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        Assert.False(await down.CheckAsync(timeout.Token));
    }

    [Fact]
    public async Task Azure_container_metadata_is_read_only_and_missing_container_is_not_ready()
    {
        var endpoint = Environment.GetEnvironmentVariable("PERITIAL_TEST_AZURITE_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
            return;
        var connectionString = AzuriteConnection(endpoint);
        var bucket = "probe" + Guid.NewGuid().ToString("N");
        var container = new BlobContainerClient(connectionString, bucket);
        await container.CreateIfNotExistsAsync(PublicAccessType.None);
        try
        {
            using var probe = StorageReadinessProbe.Create(
                AzureConfiguration(endpoint, bucket), allowSharedDevelopmentCredentials: true);
            Assert.True(await probe.CheckAsync());

            using var absent = StorageReadinessProbe.Create(
                AzureConfiguration(endpoint, "absent" + Guid.NewGuid().ToString("N")),
                allowSharedDevelopmentCredentials: true);
            Assert.False(await absent.CheckAsync());
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static IConfiguration S3Configuration(string endpoint, string? bucket = null,
        string? readerKey = null, string? readerSecret = null) =>
        Config(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Minio",
            ["Storage:Minio:Endpoint"] = endpoint,
            ["Storage:Minio:Bucket"] = bucket ?? "peritial",
            ["Storage:Minio:AccessKey"] = "localminio",
            ["Storage:Minio:SecretKey"] = "local-only-minio-secret",
            ["Storage:Readiness:S3:AccessKey"] = readerKey,
            ["Storage:Readiness:S3:SecretKey"] = readerSecret
        });

    private static IConfiguration AzureConfiguration(string endpoint, string? bucket = null) =>
        Config(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Azure",
            ["Storage:Azure:Container"] = bucket ?? "documents",
            ["Storage:Azure:ConnectionString"] = AzuriteConnection(endpoint)
        });

    private static string AzuriteConnection(string endpoint) =>
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=" +
        AzuriteKey + ";BlobEndpoint=" + endpoint + ";";

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
