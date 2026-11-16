using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class ReadinessTelemetryTests
{
    [Fact]
    public async Task Unhealthy_dependency_emits_bounded_metric_labels_and_failure_trace()
    {
        var measurements = new ConcurrentBag<Measurement>();
        var spans = new ConcurrentBag<(string Name, string? Dependency, string? Outcome)>();
        using var listener = CaptureMetrics(measurements);
        using var traceListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Peritial.Readiness",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => spans.Add((activity.OperationName,
                activity.GetTagItem("dependency")?.ToString(),
                activity.GetTagItem("outcome")?.ToString()))
        };
        ActivitySource.AddActivityListener(traceListener);

        var ready = await ReadinessTelemetry.RunRequestAsync(
            ct => ReadinessTelemetry.RunDependencyAsync(
                ReadinessDependency.Storage, _ => throw new InvalidOperationException(
                    "private-key=secret;tenant_id=beef;bucket=documents"), ct),
            CancellationToken.None);

        Assert.False(ready);
        Assert.Contains(measurements, m =>
            m.Name == "peritial.readiness.dependency.checks" && m.Value == 1 &&
            m.Tags.SequenceEqual(new[] { ("dependency", "storage"), ("outcome", "failed") }));
        Assert.Contains(measurements, m =>
            m.Name == "peritial.readiness.requests" && m.Value == 1 &&
            m.Tags.SequenceEqual(new[] { ("outcome", "not_ready") }));
        Assert.Contains(measurements, m =>
            m.Name == "peritial.readiness.dependency.duration" && m.Value >= 0);
        Assert.Contains(measurements, m =>
            m.Name == "peritial.readiness.duration" && m.Value >= 0);
        Assert.Contains(spans, s => s.Name == "peritial.readiness.dependency" &&
            s.Dependency == "storage" && s.Outcome == "failed");
        Assert.Contains(spans, s => s.Name == "peritial.readiness" && s.Outcome == "not_ready");
        Assert.DoesNotContain(measurements.SelectMany(m => m.Tags).Select(t => t.Item2),
            value => value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("documents", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("beef", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Healthy_dependency_emits_ready_without_sensitive_dimensions()
    {
        var measurements = new ConcurrentBag<Measurement>();
        using var listener = CaptureMetrics(measurements);

        var ready = await ReadinessTelemetry.RunRequestAsync(
            ct => ReadinessTelemetry.RunDependencyAsync(
                ReadinessDependency.Oidc, _ => Task.FromResult(true), ct),
            CancellationToken.None);

        Assert.True(ready);
        Assert.Contains(measurements, m =>
            m.Name == "peritial.readiness.dependency.checks" &&
            m.Tags.SequenceEqual(new[] { ("dependency", "oidc"), ("outcome", "ok") }));
        Assert.Contains(measurements, m =>
            m.Name == "peritial.readiness.requests" &&
            m.Tags.SequenceEqual(new[] { ("outcome", "ready") }));
        Assert.All(measurements, m =>
            Assert.All(m.Tags, tag => Assert.Contains(tag.Item1, new[] { "dependency", "outcome" })));
    }

    [Fact]
    public async Task Aborted_request_propagates_cancellation_and_has_a_bounded_outcome()
    {
        var measurements = new ConcurrentBag<Measurement>();
        using var listener = CaptureMetrics(measurements);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReadinessTelemetry.RunRequestAsync(
                ct => ReadinessTelemetry.RunDependencyAsync(
                    ReadinessDependency.Postgres, _ => Task.FromCanceled<bool>(ct), ct),
                cancel.Token));

        Assert.Contains(measurements, m =>
            m.Name == "peritial.readiness.requests" &&
            m.Tags.SequenceEqual(new[] { ("outcome", "cancelled") }));
    }

    private static MeterListener CaptureMetrics(ConcurrentBag<Measurement> output)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Peritial.Readiness")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            output.Add(new Measurement(instrument.Name, value, Tags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            output.Add(new Measurement(instrument.Name, value, Tags(tags))));
        listener.Start();
        return listener;
    }

    private static (string, string)[] Tags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        => tags.ToArray().Select(tag => (tag.Key, tag.Value?.ToString() ?? "")).ToArray();

    private sealed record Measurement(string Name, double Value, (string, string)[] Tags);
}
