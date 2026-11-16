using Microsoft.Extensions.Configuration;

namespace Peritial.Infrastructures;

/// <summary>
/// Explicit opt-in for exporting *only* Peritial.Readiness metrics and traces.
/// Production enforces a clean HTTPS OTLP/HTTP base URL with no embedded secrets.
/// The /v1/metrics and /v1/traces paths are generated, never user supplied.
/// </summary>
public sealed record ReadinessOtlpSettings(bool Enabled, Uri? Endpoint)
{
    public Uri? MetricsEndpoint => Endpoint is null ? null : new Uri(Endpoint, "v1/metrics");
    public Uri? TracesEndpoint => Endpoint is null ? null : new Uri(Endpoint, "v1/traces");

    public static ReadinessOtlpSettings FromConfiguration(IConfiguration config, bool isDevelopment)
    {
        var raw = config["Observability:Otlp:Enabled"];
        if (string.IsNullOrWhiteSpace(raw))
            return new ReadinessOtlpSettings(false, null);
        if (!bool.TryParse(raw, out var enabled))
            throw new InvalidOperationException("Observability:Otlp:Enabled must be a boolean.");
        if (!enabled)
            return new ReadinessOtlpSettings(false, null);

        var endpoint = config["Observability:Otlp:Endpoint"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps ||
              isDevelopment && uri.Scheme == Uri.UriSchemeHttp) ||
            string.IsNullOrEmpty(uri.Host) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                "OTLP requires a clean HTTPS collector origin (HTTP permitted only in Development).");

        return new ReadinessOtlpSettings(true, uri);
    }
}
