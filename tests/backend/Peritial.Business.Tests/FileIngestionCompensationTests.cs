using System.Text;
using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class FileIngestionCompensationTests
{
    private static readonly byte[] Pdf =
        Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n");

    [Fact]
    public async Task Ambiguous_database_commit_never_deletes_possibly_referenced_object()
    {
        var (service, store, documents) = NewService();
        documents.FailCommit = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.IngestAsync("proof.pdf", "application/pdf", new MemoryStream(Pdf), Pdf.Length));

        Assert.Equal(1, store.Puts);
        Assert.Equal(0, store.Deletes);
        Assert.Single(store.BytesByKey); // Durable intent for recovery after commit-ACK uncertainty
        Assert.Empty(documents.Committed);
    }

    [Fact]
    public async Task Partial_object_upload_failure_also_attempts_cleanup()
    {
        var (service, store, documents) = NewService();
        store.FailAfterPartialPut = true;

        await Assert.ThrowsAsync<IOException>(
            () => service.IngestAsync("proof.pdf", "application/pdf", new MemoryStream(Pdf), Pdf.Length));

        Assert.Equal(1, store.Puts);
        Assert.Equal(1, store.Deletes);
        Assert.Empty(store.BytesByKey);
        Assert.Empty(documents.Committed);
    }

    [Fact]
    public async Task Retry_after_ambiguous_commit_preserves_previous_object_for_recovery()
    {
        var (service, store, documents) = NewService();
        documents.FailCommit = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.IngestAsync("proof.pdf", "application/pdf", new MemoryStream(Pdf), Pdf.Length));

        documents.FailCommit = false;
        var saved = await service.IngestAsync(
            "proof.pdf", "application/pdf", new MemoryStream(Pdf), Pdf.Length);
        Assert.Equal(2, store.Puts);
        Assert.Equal(0, store.Deletes);
        Assert.Equal(2, store.BytesByKey.Count); // stale intent cleaned by worker
        Assert.Equal(saved.Id, Assert.Single(documents.Committed).Id);
    }

    [Fact]
    public async Task Partial_put_and_cleanup_failure_keep_intent_for_retry()
    {
        var (service, store, documents) = NewService();
        store.FailAfterPartialPut = true;
        store.FailDelete = true;

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => service.IngestAsync("proof.pdf", "application/pdf", new MemoryStream(Pdf), Pdf.Length));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.All(error.InnerExceptions, ex => Assert.IsType<IOException>(ex));
        Assert.Equal(1, store.Deletes);
        Assert.Single(store.BytesByKey);
        Assert.Empty(documents.Committed);
    }

    [Fact]
    public async Task Cancelled_metadata_commit_leaves_object_for_safe_recovery()
    {
        using var cancellation = new CancellationTokenSource();
        var (service, store, documents) = NewService();
        documents.CancelThenFail = cancellation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.IngestAsync("proof.pdf", "application/pdf",
                new MemoryStream(Pdf), Pdf.Length, ct: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(0, store.Deletes);
        Assert.Single(store.BytesByKey);
    }

    [Fact]
    public async Task Successful_upload_never_deletes_its_object()
    {
        var (service, store, documents) = NewService();

        var saved = await service.IngestAsync(
            "proof.pdf", "application/pdf", new MemoryStream(Pdf), Pdf.Length);

        Assert.Equal(0, store.Deletes);
        Assert.Single(store.BytesByKey);
        Assert.Equal(saved.Id, Assert.Single(documents.Committed).Id);
    }

    private static (FileIngestionService Service, TrackingStore Store, Documents Docs) NewService()
    {
        var store = new TrackingStore();
        var documents = new Documents();
        var service = new FileIngestionService(
            new Tenant(), new Perizie(), documents, store, new Audit(), new CleanScanner(), new FakeIntents(), new FakeIdempotency());
        return (service, store, documents);
    }

    private sealed class FakeIdempotency : IUploadIdempotencyStore
    {
        public Task<UploadIdempotencyReservation> ReserveAsync(
            TenantId tenant, string subject, string key, byte[] digest, Guid id, CancellationToken ct = default) =>
            Task.FromResult(new UploadIdempotencyReservation(UploadIdempotencyState.Acquired));
        public Task ReleaseAsync(TenantId tenant, Guid documentId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeIntents : IUploadIntentLedger
    {
        public Task RecordAsync(TenantId tenant, Guid id, string key, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task ForgetAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class TrackingStore : IObjectStore
    {
        public Dictionary<string, byte[]> BytesByKey { get; } = new();
        public int Puts { get; private set; }
        public int Deletes { get; private set; }
        public bool FailAfterPartialPut { get; set; }
        public bool FailDelete { get; set; }
        public bool CleanupWasCanceled { get; private set; }

        public async Task PutAsync(string key, Stream content, string mime, CancellationToken ct = default)
        {
            Puts++;
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            BytesByKey[key] = buffer.ToArray();
            if (FailAfterPartialPut) throw new IOException("Partial object upload failure.");
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Deletes++;
            CleanupWasCanceled = ct.IsCancellationRequested;
            ct.ThrowIfCancellationRequested();
            if (FailDelete) throw new IOException("Cannot delete stored object.");
            BytesByKey.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class Documents : IDocumentRepository
    {
        public List<DocumentRecord> Committed { get; } = new();
        public bool FailCommit { get; set; }
        public CancellationTokenSource? CancelThenFail { get; set; }
        public Task AddAsync(DocumentRecord record, CancellationToken ct = default) =>
            throw new NotSupportedException("Only audited mutations are allowed.");

        public Task AddAuditedAsync(DocumentRecord record, SecurityAuditEvent audit, CancellationToken ct = default)
        {
            if (CancelThenFail is not null)
            {
                CancelThenFail.Cancel();
                throw new OperationCanceledException(ct);
            }
            if (FailCommit) throw new InvalidOperationException("PostgreSQL commit failed.");
            Committed.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class Tenant : ITenantContext
    {
        public TenantId TenantId { get; } = TenantId.From(Guid.NewGuid());
        public string Subject => "signed-subject";
        public bool HasScope(string scope) => scope == "peritial.write";
    }

    private sealed class Perizie : IPeriziaRepository
    {
        public Task<IReadOnlyList<Perizia>> ListAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Perizia>>([]);
        public Task<Perizia?> FindAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            Task.FromResult<Perizia?>(null);
        public Task AddAsync(Perizia perizia, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(Perizia perizia, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddAuditedAsync(Perizia perizia, SecurityAuditEvent audit, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAuditedAsync(Perizia perizia, SecurityAuditEvent audit, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CleanScanner : IFileMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
            Task.FromResult(MalwareScanResult.Clean);
    }

    private sealed class Audit : ISecurityAuditTrail
    {
        public Task AppendAsync(SecurityAuditEvent entry, CancellationToken ct = default) => Task.CompletedTask;
    }
}
