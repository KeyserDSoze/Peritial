using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class ReadinessOtlpExportContractTests
{
    [Fact]
    public void Disabled_export_does_not_register_an_OpenTelemetry_provider()
    {
        var settings = ReadinessOtlpSettings.FromConfiguration(
            new ConfigurationBuilder().Build(), isDevelopment: false);
        var services = new ServiceCollection();
        services.AddReadinessOtlpExporter(settings);
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<MeterProvider>());
        Assert.Null(provider.GetService<TracerProvider>());
    }

    [Fact]
    public async Task Opted_in_export_sends_metrics_and_traces_via_OTLP_HTTP_to_loopback_receiver()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();

        using var receiver = new HttpListener();
        receiver.Prefixes.Add($"http://127.0.0.1:{port}/");
        receiver.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var payloads = new ConcurrentDictionary<string, byte[]>();
        var receive = Task.Run(async () =>
        {
            while (payloads.Count < 2)
            {
                var context = await receiver.GetContextAsync().WaitAsync(timeout.Token);
                using var body = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(body, timeout.Token);
                Assert.Equal("application/x-protobuf", context.Request.ContentType);
                payloads[context.Request.Url!.AbsolutePath] = body.ToArray();
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        }, timeout.Token);

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Observability:Otlp:Enabled"] = "true",
                ["Observability:Otlp:Endpoint"] = $"http://127.0.0.1:{port}/"
            }).Build();
        var settings = ReadinessOtlpSettings.FromConfiguration(config, isDevelopment: true);
        var services = new ServiceCollection();
        services.AddReadinessOtlpExporter(settings);

        using var provider = services.BuildServiceProvider();
        var metrics = provider.GetRequiredService<MeterProvider>();
        var traces = provider.GetRequiredService<TracerProvider>();
        Assert.False(await ReadinessTelemetry.RunRequestAsync(
            ct => ReadinessTelemetry.RunDependencyAsync(ReadinessDependency.Storage,
                _ => Task.FromResult(false), ct), timeout.Token));

        Assert.True(traces.ForceFlush(5000));
        Assert.True(metrics.ForceFlush(5000));
        await receive.WaitAsync(timeout.Token);
        Assert.Contains("/v1/metrics", payloads.Keys);
        Assert.Contains("/v1/traces", payloads.Keys);

        var metricBytes = Encoding.UTF8.GetString(payloads["/v1/metrics"]);
        var traceBytes = Encoding.UTF8.GetString(payloads["/v1/traces"]);
        Assert.Contains("peritial.readiness.requests", metricBytes);
        Assert.Contains("peritial.readiness.dependency.checks", metricBytes);
        Assert.Contains("peritial.readiness", traceBytes);
        Assert.Contains("storage", traceBytes);
        Assert.DoesNotContain("Authorization", metricBytes);
        Assert.DoesNotContain("Authorization", traceBytes);
    }
}
