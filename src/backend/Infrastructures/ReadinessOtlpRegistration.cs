using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Peritial.Infrastructures;

/// <summary>
/// Registers exactly the Peritial.Readiness Meter and ActivitySource.
/// Opt-in only; intentionally does not export HTTP request bodies, logs,
/// user identities, routes, OIDC tokens or arbitrary framework telemetry.
/// </summary>
public static class ReadinessOtlpRegistration
{
    public static IServiceCollection AddReadinessOtlpExporter(
        this IServiceCollection services, ReadinessOtlpSettings settings)
    {
        if (!settings.Enabled)
            return services;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("peritial-api"))
            .WithMetrics(metrics => metrics
                .AddMeter(ReadinessTelemetry.SourceName)
                .AddOtlpExporter(options =>
                {
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.Endpoint = settings.MetricsEndpoint!;
                }))
            .WithTracing(traces => traces
                .AddSource(ReadinessTelemetry.SourceName)
                .AddOtlpExporter(options =>
                {
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.Endpoint = settings.TracesEndpoint!;
                }));

        return services;
    }
}
