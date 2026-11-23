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
        string fileName = "signed.pdf", string contentType = "application/pdf")
    {
        var name = fileName;
        var key = storageKey ?? DocumentKeys.For(tenant, id, name);
        var bytes = "%PDF-1.7\nTest\n"u8.ToArray();
        var location = new DocumentDownloadLocation(
            new DocumentSummaryDto(id, null, name, contentType,
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


    [Theory]
    [InlineData("signed.pdf", "image/png")]
    [InlineData("signed.pdf", "application/octet-stream")]
    [InlineData("signed.pdf", "text/html")]
    [InlineData("signed.xml", "application/pdf")]
    public async Task Persisted_mime_extension_mismatch_fails_before_blob_read_or_audit(
        string fileName, string storedMime)
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var catalog = new Catalog();
        Add(catalog, tenant, id, fileName: fileName, contentType: storedMime);
        var blobs = new Blobs();
        var audit = new Audit();
        var service = new DocumentDownloadService(new Actor(tenant), catalog, blobs, audit);

        await Assert.ThrowsAsync<DocumentContentUnavailableException>(() => service.ReadAsync(id));
        Assert.Equal(0, blobs.Reads);
        Assert.Empty(audit.Events);
    }

    [Theory]
    [InlineData("signed.pdf", "application/pdf", "NOT-A-PDF")]
    [InlineData("signed.xml", "application/xml", "<a><b></a>")]
    [InlineData("signed.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "PK\u0003\u0004not-a-zip")]
    public async Task Stored_bytes_that_fail_content_validation_are_not_downloaded_or_success_audited(
        string fileName, string contentType, string storedBytes)
    {
        // Same length as PostgreSQL metadata: a byte-length check alone
        // cannot detect a substituted blob or a truncated XML/ZIP object.
        var bytes = System.Text.Encoding.UTF8.GetBytes(storedBytes);
        var tenant = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var catalog = new Catalog();
        Add(catalog, tenant, id, fileName: fileName,
            contentType: contentType, length: bytes.LongLength);
        var blobs = new Blobs { Bytes = bytes };
        var audit = new Audit();
        var service = new DocumentDownloadService(new Actor(tenant), catalog, blobs, audit);

        await Assert.ThrowsAsync<DocumentContentUnavailableException>(() => service.ReadAsync(id));
        Assert.Equal(1, blobs.Reads);
        Assert.Empty(audit.Events);
    }

    [Theory]
    [InlineData("application/xml")]
    [InlineData("text/xml")]
    public async Task Well_formed_xml_download_remains_valid_under_content_verification(string contentType)
    {
        var bytes = "<?xml version=\"1.0\"?><document/>"u8.ToArray();
        var tenant = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var catalog = new Catalog();
        Add(catalog, tenant, id, fileName: "report.xml", contentType: contentType,
            length: bytes.LongLength);
        var blobs = new Blobs { Bytes = bytes };
        var audit = new Audit();
        var service = new DocumentDownloadService(new Actor(tenant), catalog, blobs, audit);

        var result = await service.ReadAsync(id);
        Assert.Equal(bytes, result.Bytes);
        Assert.Equal("application/octet-stream", result.ContentType);
        Assert.Equal("succeeded", Assert.Single(audit.Events).Outcome);
    }

    [Fact]
    public async Task Previously_admitted_docx_can_still_be_downloaded_after_zip_revalidation()
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
                   stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, xml) in new[]
            {
                ("[Content_Types].xml", "<Types/>"),
                ("word/document.xml", "<document><body/></document>")
            })
            {
                var member = archive.CreateEntry(name);
                using var writer = new StreamWriter(member.Open());
                writer.Write(xml);
            }
        }
        var bytes = stream.ToArray();
        var tenant = TenantId.From(Guid.NewGuid());
        var id = Guid.NewGuid();
        var catalog = new Catalog();
        Add(catalog, tenant, id, fileName: "safe.docx",
            contentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            length: bytes.LongLength);
        var blobs = new Blobs { Bytes = bytes };
        var audit = new Audit();
        var service = new DocumentDownloadService(new Actor(tenant), catalog, blobs, audit);

        var downloaded = await service.ReadAsync(id);
        Assert.Equal(bytes, downloaded.Bytes);
        Assert.Equal("safe.docx", downloaded.FileName);
        Assert.Equal("application/octet-stream", downloaded.ContentType);
        Assert.Equal("succeeded", Assert.Single(audit.Events).Outcome);
    }

    [Fact]
    public async Task Previously_admitted_jpeg_and_png_still_download_after_signature_checks()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        foreach (var (fileName, mime, bytes) in new[]
        {
            ("photo.jpg", "image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0, 1, 2 }),
            ("photo.png", "image/png", new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0 })
        })
        {
            var id = Guid.NewGuid();
            var catalog = new Catalog();
            Add(catalog, tenant, id, fileName: fileName,
                contentType: mime, length: bytes.LongLength);
            var blobs = new Blobs { Bytes = bytes };
            var audit = new Audit();
            var service = new DocumentDownloadService(new Actor(tenant), catalog, blobs, audit);
            var downloaded = await service.ReadAsync(id);
            Assert.Equal(bytes, downloaded.Bytes);
            Assert.Equal(fileName, downloaded.FileName);
            Assert.Equal("succeeded", Assert.Single(audit.Events).Outcome);
        }
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
