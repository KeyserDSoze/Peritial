using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class UploadLeaseHeartbeatTests
{
    [Fact]
    public async Task Blocked_put_keeps_renewing_until_request_is_cancelled()
    {
        var ledger = new FakeLedger();
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tenant = TenantId.From(Guid.NewGuid());
        var run = new UploadLeaseHeartbeat(ledger, TimeSpan.FromMilliseconds(10)).RunPutAsync(
            tenant, Guid.NewGuid(), async token =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            for (var i = 0; i < 100 && ledger.Calls < 3; i++)
                await Task.Delay(10);
            Assert.True(ledger.Calls >= 3, "Heartbeat must renew during the blocked PUT.");
        }
        finally { cancel.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Lease_lost_during_put_cancels_upload_and_never_reports_success()
    {
        var ledger = new FakeLedger { AllowRenew = false };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        bool sawCancellation = false;
        var error = await Assert.ThrowsAsync<UploadLeaseExpiredException>(() =>
            new UploadLeaseHeartbeat(ledger, TimeSpan.FromMilliseconds(10)).RunPutAsync(
                TenantId.From(Guid.NewGuid()), Guid.NewGuid(), async token =>
                {
                    started.SetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) { sawCancellation = true; throw; }
                }, cancel.Token));
        Assert.True(started.Task.IsCompleted);
        Assert.True(sawCancellation);
        Assert.Contains("lease", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Completed_put_requires_one_last_successful_renewal_before_metadata()
    {
        var ledger = new FakeLedger { AllowRenew = false };
        bool put = false;
        await Assert.ThrowsAsync<UploadLeaseExpiredException>(() =>
            new UploadLeaseHeartbeat(ledger).RunPutAsync(
                TenantId.From(Guid.NewGuid()), Guid.NewGuid(), token =>
                {
                    put = true;
                    return Task.CompletedTask;
                }));
        Assert.True(put);
        Assert.Equal(1, ledger.Calls);
    }

    private sealed class FakeLedger : IUploadIntentLedger
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool AllowRenew { get; set; } = true;
        public Task RecordAsync(TenantId tenant, Guid id, string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RenewAsync(TenantId tenant, Guid id, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(AllowRenew);
        }
        public Task ForgetAsync(TenantId tenant, Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
