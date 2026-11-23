using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class DocumentDownloadTests
{
    private sealed class Actor(TenantId tenant, bool allowed = true) : ITenantContext
    {
        public TenantId TenantId => tenant;
        public string Subject => "signed-download-reader";
        public bool HasScope(string scope) => allowed && scope == "peritial.read";
    }

    private sealed class Catalog : IDocumentDownloadRepository
    {
        public Dictionary<Guid, (TenantId Tenant, DocumentDownloadLocation Location)> Rows { get; } = [];
        public int Reads { get; private set; }
        public Task<DocumentDownloadLocation?> FindForDownloadAsync(
            TenantId tenant, Guid documentId, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(Rows.TryGetValue(documentId, out var found) && found.Tenant == tenant
                ? found.Location : null);
        }
    }

    private sealed class Blobs : IDocumentBlobReader
    {
        public int Reads { get; private set; }
        public byte[] Bytes { get; set; } = "%PDF-1.7\nTest\n"u8.ToArray();
        public Task<byte[]> ReadAsync(string key, long expectedLength, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(Bytes);
        }
    }

    private sealed class Audit : ISecurityAuditTrail
    {
        public List<SecurityAuditEvent> Events { get; } = [];
        public bool Fail { get; set; }
        public Task AppendAsync(SecurityAuditEvent item, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("Audit unavailable.");
            Events.Add(item);
            return Task.CompletedTask;
        }
    }

    private static DocumentDownloadLocation Add(Catalog repo, TenantId tenant,
        Guid id, long? length = null, string? storageKey = null,
        string fileName = "signed.pdf")
    {
        var name = fileName;
        var key = storageKey ?? DocumentKeys.For(tenant, id, name);
        var bytes = "%PDF-1.7\nTest\n"u8.ToArray();
        var location = new DocumentDownloadLocation(
            new DocumentSummaryDto(id, null, name, "application/pdf",
                length ?? bytes.Length, DateTime.UtcNow), key);
        repo.Rows[id] = (tenant, location);
        return location;
    }

    [Fact]
    public async Task Authorized_reader_receives_bounded_bytes_only_after_successful_audit()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var repo = new Catalog();
        Add(repo, tenant, id);
        var blobs = new Blobs();
        var audit = new Audit();
        var service = new DocumentDownloadService(new Actor(tenant), repo, blobs, audit);
        var response = await service.ReadAsync(id);

        Assert.Equal(blobs.Bytes, response.Bytes);
        Assert.Equal("signed.pdf", response.FileName);
        Assert.Equal("application/octet-stream", response.ContentType);
        Assert.Equal(1, blobs.Reads);
        var entry = Assert.Single(audit.Events);
        Assert.Equal("document.download", entry.Action);
        Assert.Equal("succeeded", entry.Outcome);
        Assert.Equal(id, entry.ResourceId);
        Assert.Equal(tenant, entry.TenantId);
        Assert.Equal("signed-download-reader", entry.Subject);
        Assert.DoesNotContain("StorageKey", System.Text.Json.JsonSerializer.Serialize(response));
        Assert.DoesNotContain("TenantId", System.Text.Json.JsonSerializer.Serialize(response));
    }

    [Fact]
    public async Task No_scope_is_denied_and_audited_before_query_or_storage()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var repo = new Catalog();
        var blobs = new Blobs();
        var audit = new Audit();
        var service = new DocumentDownloadService(new Actor(tenant, false), repo, blobs, audit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(Guid.NewGuid()));
        Assert.Equal(0, repo.Reads);
        Assert.Equal(0, blobs.Reads);
        var entry = Assert.Single(audit.Events);
        Assert.Equal("document.download", entry.Action);
        Assert.Equal("denied", entry.Outcome);
        Assert.Null(entry.ResourceId);
    }

    [Fact]
    public async Task Foreign_and_missing_documents_do_not_expose_storage()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var other = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var repo = new Catalog();
        Add(repo, other, id);
        var blobs = new Blobs();
        var audit = new Audit();
        var service = new DocumentDownloadService(new Actor(tenant), repo, blobs, audit);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ReadAsync(id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ReadAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ReadAsync(Guid.Empty));
        Assert.Equal(0, blobs.Reads);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Corrupt_storage_key_or_excessive_length_fails_closed_before_blob_fetch()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var repo = new Catalog();
        var id = Guid.NewGuid();
        Add(repo, tenant, id, storageKey:"another-tenant/private.pdf");
        var bigId = Guid.NewGuid();
        Add(repo, tenant, bigId, length:FileIngestionService.MaxUploadBytes + 1);
        var blobs = new Blobs();
        var audit = new Audit();
        var service = new DocumentDownloadService(new Actor(tenant), repo, blobs, audit);
        await Assert.ThrowsAsync<DocumentContentUnavailableException>(() => service.ReadAsync(id));
        await Assert.ThrowsAsync<DocumentContentUnavailableException>(() => service.ReadAsync(bigId));
        Assert.Equal(0, blobs.Reads);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Noncanonical_persisted_file_name_is_not_a_download_header()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var catalog = new Catalog();
        Add(catalog, tenant, id, fileName:"../untrusted.pdf");
        var blobs = new Blobs();
        var audit = new Audit();
        var service = new DocumentDownloadService(new Actor(tenant), catalog, blobs, audit);
        await Assert.ThrowsAsync<DocumentContentUnavailableException>(() => service.ReadAsync(id));
        Assert.Equal(0, blobs.Reads);
        Assert.Empty(audit.Events);
    }

    [Theory]
    [InlineData("NUL.pdf")]
    [InlineData("COM1.pdf")]
    [InlineData("invoice:alternate.pdf")]
    [InlineData("invoice|draft.pdf")]
    [InlineData("invoice\u202Efdp.pdf")]
    public async Task Persisted_unsafe_file_name_cannot_reach_download_headers_or_blob_reader(
        string fileName)
    {
        // Defense in depth for metadata written by previous releases or
        // corruption: never stream bytes or emit Content-Disposition with an
        // invalid filename, even when tenant/id/storage key match.
        var tenant = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var catalog = new Catalog();
        Add(catalog, tenant, id, fileName: fileName);
        var blobs = new Blobs();
        var audit = new Audit();
        var service = new DocumentDownloadService(new Actor(tenant), catalog, blobs, audit);

        await Assert.ThrowsAsync<DocumentContentUnavailableException>(
            () => service.ReadAsync(id));
        Assert.Equal(0, blobs.Reads);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Mismatched_storage_length_or_failed_audit_never_returns_bytes()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var repo = new Catalog();
        Add(repo, tenant, id);
        var blobs = new Blobs { Bytes = "SHORT"u8.ToArray() };
        var audit = new Audit();
        var service = new DocumentDownloadService(new Actor(tenant), repo, blobs, audit);
        await Assert.ThrowsAsync<DocumentContentUnavailableException>(() => service.ReadAsync(id));
        Assert.Empty(audit.Events);

        blobs.Bytes = "%PDF-1.7\nTest\n"u8.ToArray();
        audit.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadAsync(id));
        Assert.Empty(audit.Events);
    }
}
