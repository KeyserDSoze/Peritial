using System.Net;
using System.Net.Http;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class OidcReadinessProbeTests
{
    private const string Issuer = "http://issuer.test/realms/peritial";
    private const string Metadata = "http://metadata.test/realms/peritial/.well-known/openid-configuration";
    private const string Jwks = "http://issuer.test/realms/peritial/protocol/openid-connect/certs";
    private const string PublicKeys = """{"keys":[{"kty":"RSA","kid":"signer-1","use":"sig","n":"AQAB","e":"AQAB"}]}""";

    [Fact]
    public async Task Trusted_discovery_and_nonempty_signing_JWKS_are_ready()
    {
        using var client = Client(req => req.RequestUri!.AbsoluteUri == Metadata
            ? Json($$"""{"issuer":"{{Issuer}}","jwks_uri":"{{Jwks}}"}""")
            : req.RequestUri!.AbsoluteUri == Jwks
                ? Json(PublicKeys)
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        using var probe = new OidcReadinessProbe(client, Issuer, Metadata, allowHttpForDevelopment: true);
        Assert.True(await probe.CheckAsync());
    }

    [Theory]
    [InlineData("""{"issuer":"http://another.test/realms/peritial","jwks_uri":"http://issuer.test/jwks"}""")]
    [InlineData("""{"issuer":"http://issuer.test/realms/peritial"}""")]
    [InlineData("""{"issuer":"http://issuer.test/realms/peritial","jwks_uri":"http://evil.test/jwks"}""")]
    [InlineData("""{"issuer":"http://issuer.test/realms/peritial","jwks_uri":"file:///tmp/certs"}""")]
    [InlineData("""not-json""")]
    public async Task Invalid_or_untrusted_metadata_fails_closed(string body)
    {
        using var client = Client(_ => Json(body));
        using var probe = new OidcReadinessProbe(client, Issuer, Metadata, allowHttpForDevelopment: true);
        Assert.False(await probe.CheckAsync());
    }

    [Theory]
    [InlineData("""{"keys":[]}""")]
    [InlineData("""{"keys":[{"kty":"RSA","kid":"only-kid"}]}""")]
    [InlineData("""{"keys":[{"kty":"RSA","kid":"encryption","use":"enc","n":"n","e":"e"}]}""")]
    [InlineData("""not-json""")]
    public async Task Missing_or_unusable_signing_keys_fail_closed(string jwks)
    {
        using var client = Client(req => req.RequestUri!.AbsoluteUri == Metadata
            ? Json($$"""{"issuer":"{{Issuer}}","jwks_uri":"{{Jwks}}"}""")
            : Json(jwks));
        using var probe = new OidcReadinessProbe(client, Issuer, Metadata, allowHttpForDevelopment: true);
        Assert.False(await probe.CheckAsync());
    }

    [Fact]
    public async Task Failed_discovery_request_fails_closed()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var probe = new OidcReadinessProbe(client, Issuer, Metadata, allowHttpForDevelopment: true);
        Assert.False(await probe.CheckAsync());
    }

    [Fact]
    public async Task Https_is_required_outside_development()
    {
        using var client = Client(_ => Json("{}"));
        Assert.Throws<ArgumentException>(() =>
            new OidcReadinessProbe(client, Issuer, Metadata, allowHttpForDevelopment: false));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response)
        => new(new LocalHandler(response)) { Timeout = TimeSpan.FromSeconds(2) };

    private static HttpResponseMessage Json(string value)
        => new(HttpStatusCode.OK) { Content = new StringContent(value, System.Text.Encoding.UTF8, "application/json") };

    private sealed class LocalHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
