using System.Text;
using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class UploadIntentOrderingTests
{
    private static readonly byte[] Pdf =
        Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n");

    [Fact]
    public async Task Durable_intent_precedes_object_upload_and_success_commit()
    {
        var order = new List<string>();
        var intents = new FakeIntents(order);
        var store = new FakeStore(order);
        var docs = new FakeDocs(order);
        var service = Service(intents, store, docs);
        await service.IngestAsync("proof.pdf", "application/pdf", new MemoryStream(Pdf), Pdf.Length);

        Assert.Equal(new[] { "intent", "put", "commit" }, order);
        Assert.Equal(1, intents.Recorded);
    }

    [Fact]
    public async Task Ambiguous_metadata_commit_defers_cleanup_to_safe_reconciler()
    {
        var order = new List<string>();
        var intents = new FakeIntents(order);
        var store = new FakeStore(order);
        var docs = new FakeDocs(order) { FailMetadata = true };
        var service = Service(intents, store, docs);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.IngestAsync("proof.pdf", "application/pdf", new MemoryStream(Pdf), Pdf.Length));
        Assert.Equal(new[] { "intent", "put", "commit" }, order);
        Assert.Equal(0, store.Deletes);
        Assert.Equal(0, intents.Forgotten); // Crash-safe, pending recovery
    }

    [Fact]
    public async Task Failed_intent_write_does_not_start_upload()
    {
        var order = new List<string>();
        var intents = new FakeIntents(order) { FailRegistration = true };
        var store = new FakeStore(order);
        var service = Service(intents, store, new FakeDocs(order));
        await Assert.ThrowsAsync<IOException>(() =>
            service.IngestAsync("proof.pdf", "application/pdf", new MemoryStream(Pdf), Pdf.Length));
        Assert.Equal(new[] { "intent" }, order);
        Assert.Equal(0, store.Uploads);
    }

    private static FileIngestionService Service(FakeIntents intents, FakeStore store, FakeDocs docs) =>
        new(new Tenant(), new Perizie(), docs, store, new Audit(), new CleanScanner(), intents, new FakeIdempotency());

    private sealed class FakeIdempotency : IUploadIdempotencyStore
    {
        public Task<UploadIdempotencyReservation> ReserveAsync(
            TenantId tenant, string subject, string key, byte[] digest, Guid id, CancellationToken ct = default) =>
            Task.FromResult(new UploadIdempotencyReservation(UploadIdempotencyState.Acquired));
        public Task ReleaseAsync(TenantId tenant, Guid documentId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeIntents(List<string> order) : IUploadIntentLedger
    {
        public int Recorded { get; private set; }
        public int Forgotten { get; private set; }
        public bool FailRegistration { get; set; }
        public Task RecordAsync(TenantId tenant, Guid id, string key, CancellationToken ct = default)
        {
            order.Add("intent");
            if (FailRegistration) throw new IOException("Database unavailable");
            Recorded++;
            return Task.CompletedTask;
        }
        public Task<bool> RenewAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task ForgetAsync(TenantId tenant, Guid id, CancellationToken ct = default)
        {
            Forgotten++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStore(List<string> order) : IObjectStore
    {
        public int Deletes { get; private set; }
        public int Uploads { get; private set; }
        public Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
        {
            order.Add("put");
            Uploads++;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Deletes++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDocs(List<string> order) : IDocumentRepository
    {
        public bool FailMetadata { get; set; }
        public Task AddAsync(DocumentRecord item, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAuditedAsync(DocumentRecord item, SecurityAuditEvent audit, CancellationToken ct = default)
        {
            order.Add("commit");
            if (FailMetadata) throw new InvalidOperationException("Commit ACK unknown");
            return Task.CompletedTask;
        }
    }

    private sealed class Tenant : ITenantContext
    {
        public TenantId TenantId { get; } = TenantId.From(Guid.NewGuid());
        public string Subject => "subject";
        public bool HasScope(string scope) => scope == "peritial.write";
    }
    private sealed class Perizie : IPeriziaRepository
    {
        public Task<IReadOnlyList<Perizia>> ListAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Perizia>>([]);
        public Task<Perizia?> FindAsync(TenantId tenant, Guid id, CancellationToken ct = default) => Task.FromResult<Perizia?>(null);
        public Task AddAsync(Perizia x, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(Perizia x, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddAuditedAsync(Perizia x, SecurityAuditEvent a, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAuditedAsync(Perizia x, SecurityAuditEvent a, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Audit : ISecurityAuditTrail
    {
        public Task AppendAsync(SecurityAuditEvent entry, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class CleanScanner : IFileMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
            Task.FromResult(MalwareScanResult.Clean);
    }
}
