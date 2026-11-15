using System.Runtime.ExceptionServices;
using Peritial.Domains;

namespace Peritial.Business;

/// <summary>
/// Refreshes a tenant-scoped PostgreSQL upload lease during a slow object-store
/// PUT. Loss of the lease cancels the upload and prevents metadata commit.
/// </summary>
public sealed class UploadLeaseHeartbeat(IUploadIntentLedger ledger, TimeSpan? interval = null)
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromSeconds(20);

    public async Task RunPutAsync(
        TenantId tenant, Guid documentId, Func<CancellationToken, Task> put,
        CancellationToken requestToken = default)
    {
        if (_interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        using var stopPulses = new CancellationTokenSource();
        using var putToken = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        Exception? renewalFailure = null;

        async Task PulseAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(_interval);
                while (await timer.WaitForNextTickAsync(stopPulses.Token))
                {
                    if (!await ledger.RenewAsync(tenant, documentId, stopPulses.Token))
                        throw new UploadLeaseExpiredException();
                }
            }
            catch (OperationCanceledException) when (stopPulses.IsCancellationRequested)
            {
                // Expected: successful PUT or an upload error stopped the timer.
            }
            catch (Exception ex)
            {
                renewalFailure = ex;
                putToken.Cancel();
            }
        }

        // The first timer wait yields immediately; the heartbeat runs while
        // PUT is pending, regardless of HTTP caller latency or MCP transport.
        var pulse = PulseAsync();
        Exception? uploadFailure = null;
        try
        {
            await put(putToken.Token);
        }
        catch (Exception ex) { uploadFailure = ex; }
        finally
        {
            stopPulses.Cancel();
            await pulse;
        }

        // Do not allow a store that ignored CancellationToken to commit success
        // after the heartbeat already observed a lost lease.
        if (renewalFailure is not null)
            throw new UploadLeaseExpiredException(renewalFailure);
        if (uploadFailure is not null)
            ExceptionDispatchInfo.Capture(uploadFailure).Throw();

        // Fence the gap between the final PUT chunk and the database write.
        // AddAuditedAsync independently verifies the lease under the same
        // transaction that consumes the intent and commits document+audit.
        if (!await ledger.RenewAsync(tenant, documentId, requestToken))
            throw new UploadLeaseExpiredException();
    }
}

public sealed class UploadLeaseExpiredException : Exception
{
    public UploadLeaseExpiredException() : base("Upload lease expired; retry with the same idempotency key.") { }
    public UploadLeaseExpiredException(Exception inner)
        : base("Upload lease unavailable; upload blocked.", inner) { }
}
