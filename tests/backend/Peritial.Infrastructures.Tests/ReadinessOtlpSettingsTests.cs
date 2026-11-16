using Microsoft.Extensions.Configuration;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class ReadinessOtlpSettingsTests
{
    [Fact]
    public void No_opt_in_never_configures_an_export_endpoint()
    {
        var settings = ReadinessOtlpSettings.FromConfiguration(Config(
            ("Observability:Otlp:Endpoint", "https://metrics.example.test/")), isDevelopment: false);
        Assert.False(settings.Enabled);
        Assert.Null(settings.Endpoint);
    }

    [Fact]
    public void Enabled_production_accepts_only_https_base_endpoint_without_credentials()
    {
        var settings = ReadinessOtlpSettings.FromConfiguration(Config(
            ("Observability:Otlp:Enabled", "true"),
            ("Observability:Otlp:Endpoint", "https://collector.example.test:4318/")), isDevelopment: false);
        Assert.True(settings.Enabled);
        Assert.Equal("https://collector.example.test:4318/v1/metrics", settings.MetricsEndpoint!.AbsoluteUri);
        Assert.Equal("https://collector.example.test:4318/v1/traces", settings.TracesEndpoint!.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://collector.test:4318/")]
    [InlineData("https://user:password@collector.test/")]
    [InlineData("https://collector.test/v1/metrics")]
    [InlineData("https://collector.test/?key=secret")]
    [InlineData("https://collector.test/#token")]
    [InlineData("file:///tmp/collector")]
    [InlineData("https://collector.test/observability")]
    public void Unsafe_production_endpoint_rejects_startup(string endpoint) =>
        Assert.Throws<InvalidOperationException>(() => ReadinessOtlpSettings.FromConfiguration(Config(
            ("Observability:Otlp:Enabled", "true"),
            ["Observability:Otlp:Endpoint"] = endpoint), isDevelopment: false));

    [Fact]
    public void Development_allows_explicit_local_http_collector()
    {
        var settings = ReadinessOtlpSettings.FromConfiguration(Config(
            ("Observability:Otlp:Enabled", "true"),
            ("Observability:Otlp:Endpoint", "http://127.0.0.1:4318/")), isDevelopment: true);
        Assert.Equal("http://127.0.0.1:4318/v1/metrics", settings.MetricsEndpoint!.AbsoluteUri);
    }

    [Fact]
    public void Explicit_opt_in_without_endpoint_fails_closed() =>
        Assert.Throws<InvalidOperationException>(() => ReadinessOtlpSettings.FromConfiguration(Config(
            ("Observability:Otlp:Enabled", "true")), isDevelopment: true));

    [Theory]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("sometimes")]
    public void Invalid_switch_fails_closed(string value) =>
        Assert.Throws<InvalidOperationException>(() => ReadinessOtlpSettings.FromConfiguration(Config(
            ("Observability:Otlp:Enabled", value)), isDevelopment: true));

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(pair =>
            new KeyValuePair<string, string?>(pair.Key, pair.Value))).Build();
}
