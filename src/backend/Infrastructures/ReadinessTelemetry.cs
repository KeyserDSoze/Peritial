using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Peritial.Infrastructures;

/// <summary>Closed set of health dependency tags: no tenant, user, bucket or host dimensions.</summary>
public enum ReadinessDependency
{
    Postgres,
    Clamav,
    Oidc,
    Storage
}

/// <summary>
/// Zero-dependency .NET Meter/ActivitySource foundation for health observability.
/// No exporter or collector is silently enabled; production owns that wiring.
/// No exception text, URI, database or user identifiers are ever added to tags.
/// </summary>
public static class ReadinessTelemetry
{
    public const string SourceName = "Peritial.Readiness";

    private static readonly Meter Meter = new(SourceName, "1.0.0");
    private static readonly ActivitySource Activities = new(SourceName, "1.0.0");
    private static readonly Counter<long> DependencyChecks = Meter.CreateCounter<long>(
        "peritial.readiness.dependency.checks", "1",
        "Number of actual dependency probes by bounded dependency and outcome.");
    private static readonly Histogram<double> DependencyDurations = Meter.CreateHistogram<double>(
        "peritial.readiness.dependency.duration", "s",
        "Elapsed time for an actual dependency probe.");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "peritial.readiness.requests", "1", "Overall readiness requests by result.");
    private static readonly Histogram<double> RequestDurations = Meter.CreateHistogram<double>(
        "peritial.readiness.duration", "s", "Elapsed time for a full readiness request.");

    public static async Task<bool> RunDependencyAsync(
        ReadinessDependency dependency, Func<CancellationToken, Task<bool>> probe,
        CancellationToken cancellationToken)
    {
        var label = dependency switch
        {
            ReadinessDependency.Postgres => "postgres",
            ReadinessDependency.Clamav => "clamav",
            ReadinessDependency.Oidc => "oidc",
            ReadinessDependency.Storage => "storage",
            _ => throw new ArgumentOutOfRangeException(nameof(dependency))
        };

        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        using var activity = Activities.StartActivity("peritial.readiness.dependency");
        activity?.SetTag("dependency", label);
        try
        {
            var ready = await probe(cancellationToken);
            outcome = ready ? "ok" : "failed";
            return ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception)
        {
            // The underlying dependency exception must never reach HTTP or trace tags.
            return false;
        }
        finally
        {
            activity?.SetTag("outcome", outcome);
            TagList tags = new() { { "dependency", label }, { "outcome", outcome } };
            DependencyChecks.Add(1, tags);
            DependencyDurations.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    }

    public static async Task<bool> RunRequestAsync(
        Func<CancellationToken, Task<bool>> probe, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "not_ready";
        using var activity = Activities.StartActivity("peritial.readiness");
        try
        {
            var ready = await probe(cancellationToken);
            outcome = ready ? "ready" : "not_ready";
            return ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception)
        {
            // Includes an internally enforced timeout. Client cancellation is
            // distinguished by the original request token in the filter above.
            return false;
        }
        finally
        {
            activity?.SetTag("outcome", outcome);
            TagList tags = new() { { "outcome", outcome } };
            Requests.Add(1, tags);
            RequestDurations.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    }
}
