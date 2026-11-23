using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Peritial.Business;
using Peritial.Domains;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// Actual PostgreSQL FORCE RLS + real SeaweedFS/Azurite provider reads.
/// This is a downstream document download test, not an ETag comparison.
/// The Compose workflow must opt-in with the provider endpoints.
/// </summary>
public sealed class RealDocumentDigestDownloadContractTests
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private sealed class SignedReader(TenantId tenant) : ITenantContext
    {
        public TenantId TenantId => tenant;
        public string Subject => "compose-storage-integrity-test";
        public bool HasScope(string scope) => scope == "peritial.read";
    }

    [Fact]
    public async Task SeaweedFS_equal_length_valid_PDF_tampering_is_denied_before_success_audit()
    {
        var endpoint = Environment.GetEnvironmentVariable("PERITIAL_TEST_S3_ENDPOINT");
        var postgres = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(postgres))
            return; // Only Compose has the real SeaweedFS endpoint.

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Storage:Minio:Endpoint"] = endpoint,
                ["Storage:Minio:AccessKey"] = "localminio",
                ["Storage:Minio:SecretKey"] = "local-only-minio-secret",
                ["Storage:Minio:Bucket"] = "peritial"
            }).Build();
        using var store = new MinioObjectStore(config);
        await using var admin = NpgsqlDataSource.Create(postgres);
        await AssertRestoredDigestAgainstRealStorageAsync(admin, store, store);
    }

    [Fact]
    public async Task Azurite_equal_length_valid_PDF_tampering_is_denied_before_success_audit()
    {
        var endpoint = Environment.GetEnvironmentVariable("PERITIAL_TEST_AZURITE_ENDPOINT");
        var postgres = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(postgres))
            return; // Only Compose has the real Azurite endpoint.

        var connectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
            "AccountKey=" + AzuriteKey + ";BlobEndpoint=" + endpoint + ";";
        var containerName = "digest-" + Guid.NewGuid().ToString("N");
        var container = new BlobContainerClient(connectionString, containerName);
        await container.CreateIfNotExistsAsync(publicAccessType: PublicAccessType.None);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Storage:Azure:ConnectionString"] = connectionString,
                    ["Storage:Azure:Container"] = containerName
                }).Build();
            var store = new AzureBlobObjectStore(config);
            await using var admin = NpgsqlDataSource.Create(postgres);
            await AssertRestoredDigestAgainstRealStorageAsync(admin, store, store);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    private static async Task AssertRestoredDigestAgainstRealStorageAsync(
        NpgsqlDataSource admin, IObjectStore writer, IDocumentBlobReader reader)
    {
        // Both bodies are valid-looking PDFs of identical length. Length,
        // MIME and magic bytes alone cannot distinguish the substitution.
        byte[] original = "%PDF-1.7\nORIGINAL\n%%EOF\n"u8.ToArray();
        byte[] swapped = "%PDF-1.7\nREPLACED\n%%EOF\n"u8.ToArray();
        Assert.Equal(original.Length, swapped.Length);
        Assert.NotEqual(DocumentDigest.Compute(original), DocumentDigest.Compute(swapped));

        await SchemaBootstrap.ApplyDevelopmentSchemaAsync(admin);
        await TestDbRoleProvisioner.ConfigureAsync(admin);
        var tenant = TenantId.From(Guid.NewGuid());
        var other = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var fileName = "integrity.pdf";
        var storageKey = DocumentKeys.For(tenant, id, fileName);

        var appString = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
        {
            Username = "peritial_app", Password = "local-only-app", MaxPoolSize = 1
        };
        await using var app = NpgsqlDataSource.Create(appString.ConnectionString);
        var repo = new PgDocumentCatalogRepository(app);
        var audit = new PgSecurityAuditTrail(app);
        var downloads = new DocumentDownloadService(new SignedReader(tenant), repo, reader, audit);
        var anotherTenant = new DocumentDownloadService(new SignedReader(other), repo, reader, audit);
        var record = new DocumentRecord(id, tenant, null, fileName, "application/pdf",
            original.LongLength, storageKey, DateTime.UtcNow,
            ContentSha256: DocumentDigest.Compute(original));

        try
        {
            await new PgDocumentRepository(admin).AddAsync(record); // test-only, validated V014 row
            using (var body = new MemoryStream(original, writable: false))
                await writer.PutAsync(storageKey, body, "application/pdf");

            // Private locator resolves the checksum under real FORCE RLS,
            // never exposing the S3/Azure key to a foreign signed tenant.
            await Assert.ThrowsAsync<KeyNotFoundException>(() => anotherTenant.ReadAsync(id));
            Assert.Equal(0L, await DownloadAuditCountAsync(admin, tenant, id));

            var first = await downloads.ReadAsync(id);
            Assert.Equal(original, first.Bytes);
            Assert.Equal("application/octet-stream", first.ContentType);
            Assert.Equal(1L, await DownloadAuditCountAsync(admin, tenant, id));

            using (var body = new MemoryStream(swapped, writable: false))
                await writer.PutAsync(storageKey, body, "application/pdf");
            var exception = await Assert.ThrowsAsync<DocumentContentUnavailableException>(
                () => downloads.ReadAsync(id));
            Assert.DoesNotContain(storageKey, exception.Message);
            Assert.Equal(1L, await DownloadAuditCountAsync(admin, tenant, id));

            using (var body = new MemoryStream(original, writable: false))
                await writer.PutAsync(storageKey, body, "application/pdf");
            var restored = await downloads.ReadAsync(id);
            Assert.Equal(original, restored.Bytes);
            Assert.Equal(2L, await DownloadAuditCountAsync(admin, tenant, id));
        }
        finally
        {
            // This is a shared disposable Compose database. Never leave a
            // dangling PostgreSQL reference after deleting its S3/Azure
            // object: later offline checkpoint and archive tests enumerate
            // every documents row and would correctly reject the orphan.
            await using (var cleanup = admin.CreateCommand(
                "DELETE FROM documents WHERE tenant_id=@tenant AND id=@id"))
            {
                cleanup.Parameters.AddWithValue("tenant", tenant.Value);
                cleanup.Parameters.AddWithValue("id", id);
                await cleanup.ExecuteNonQueryAsync();
            }
            await writer.DeleteAsync(storageKey);
        }
    }

    private static async Task<long> DownloadAuditCountAsync(
        NpgsqlDataSource admin, TenantId tenant, Guid id)
    {
        await using var cmd = admin.CreateCommand(
            "SELECT count(*) FROM security_audit " +
            "WHERE tenant_id=@tenant AND resource_id=@id " +
            "AND action='document.download' AND outcome='succeeded'");
        cmd.Parameters.AddWithValue("tenant", tenant.Value);
        cmd.Parameters.AddWithValue("id", id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
