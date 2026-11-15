using Peritial.Infrastructures;

namespace Peritial.Applications;

/// <summary>Polls a bounded batch of stale intents; no public recovery endpoint.</summary>
public sealed class UploadRecoveryWorker(
    PgUploadReconciler reconciler, ILogger<UploadRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // PUT + commit should complete far sooner than the 60 minute
                // quarantine period. Recover a max of 20 items per cycle.
                for (var i = 0; i < 20; i++)
                    if (!await reconciler.ReconcileOneAsync(DateTime.UtcNow.AddHours(-1), stoppingToken))
                        break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Do not log raw storage keys, filename or access token.
                logger.LogError("Upload recovery failed; pending intent retained for retry.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
