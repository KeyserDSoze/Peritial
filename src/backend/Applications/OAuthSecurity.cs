using System.Security.Claims;

namespace Peritial.Applications;

/// <summary>Shared OAuth claims validation for the REST and MCP transport boundaries.</summary>
public static class OAuthSecurity
{
    public static bool HasSignedTenantAndSubject(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirst("tenant_id")?.Value, out var tenant) &&
        tenant != Guid.Empty &&
        !string.IsNullOrWhiteSpace(principal?.FindFirst("sub")?.Value ??
                                   principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value);

    public static bool HasScope(ClaimsPrincipal principal, string expected) =>
        principal.FindAll("scope").Concat(principal.FindAll("scp"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(expected, StringComparer.Ordinal);

    public static bool HasAnyScope(ClaimsPrincipal principal, params string[] scopes) =>
        scopes.Any(scope => HasScope(principal, scope));
}

/// <summary>Canonical RFC 9728 metadata URIs for a deployed MCP resource.</summary>
public sealed class OAuthProtectedResource
{
    public string McpResource { get; }
    public string McpMetadataUrl { get; }
    public string ApiResource { get; }

    public OAuthProtectedResource(string resource, bool allowInsecureLocalDevelopment)
    {
        if (!Uri.TryCreate(resource, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             !(allowInsecureLocalDevelopment && uri.Scheme == Uri.UriSchemeHttp)) ||
            uri.AbsolutePath != "/mcp" || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            resource != uri.AbsoluteUri)
            throw new InvalidOperationException(
                "Auth:Resource must be the canonical absolute MCP /mcp URL (HTTPS required outside Development).");

        McpResource = uri.AbsoluteUri;
        ApiResource = uri.GetLeftPart(UriPartial.Authority);
        McpMetadataUrl = ApiResource + "/.well-known/oauth-protected-resource/mcp";
    }

    public object AsMcpMetadata(string issuer) => Build(McpResource, issuer);
    public object AsApiMetadata(string issuer) => Build(ApiResource, issuer);

    private static object Build(string resource, string issuer) => new
    {
        resource,
        authorization_servers = new[] { issuer },
        scopes_supported = new[] { "peritial.read", "peritial.write", "peritial.sinistri.delete" },
        bearer_methods_supported = new[] { "header" }
    };
}
