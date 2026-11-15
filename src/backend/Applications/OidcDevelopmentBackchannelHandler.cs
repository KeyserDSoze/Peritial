namespace Peritial.Applications;

/// <summary>
/// Dev-only: OIDC issuer remains the external browser URL, while JWKS retrieval
/// uses the Docker service's internal URL. Never enable in production.
/// </summary>
public sealed class OidcDevelopmentBackchannelHandler : DelegatingHandler
{
    private readonly Uri _publicOrigin;
    private readonly Uri _internalOrigin;

    public OidcDevelopmentBackchannelHandler(string publicIssuer, string internalOrigin)
        : base(new HttpClientHandler())
    {
        var issuer = new Uri(publicIssuer, UriKind.Absolute);
        var network = new Uri(internalOrigin, UriKind.Absolute);
        if (network.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("Local backchannel must be plain HTTP on Docker network.", nameof(internalOrigin));
        _publicOrigin = new Uri(issuer.GetLeftPart(UriPartial.Authority));
        _internalOrigin = new Uri(network.GetLeftPart(UriPartial.Authority));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("OIDC request URI missing.");
        if (uri.Scheme == _publicOrigin.Scheme &&
            uri.Host.Equals(_publicOrigin.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == _publicOrigin.Port)
        {
            var builder = new UriBuilder(uri)
            {
                Scheme = _internalOrigin.Scheme,
                Host = _internalOrigin.Host,
                Port = _internalOrigin.Port
            };
            request.RequestUri = builder.Uri;
        }
        return base.SendAsync(request, cancellationToken);
    }
}
