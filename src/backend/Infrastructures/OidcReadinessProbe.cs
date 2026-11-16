using System.Net;
using System.Text.Json;

namespace Peritial.Infrastructures;

/// <summary>
/// Read-only OIDC reachability and structural signing-key readiness.
/// This is not JWT signature verification or signing-key rotation monitoring.
/// The configured discovery endpoint may be an internal development address,
/// but any advertised JWKS URI must share the public issuer's origin.
/// </summary>
public interface IOidcReadinessProbe
{
    Task<bool> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed class OidcReadinessProbe : IOidcReadinessProbe, IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _issuer;
    private readonly Uri _metadataAddress;
    private readonly string _issuerName;

    public OidcReadinessProbe(HttpClient client, string issuer, string metadataAddress,
        bool allowHttpForDevelopment = false)
    {
        _client = client;
        _issuer = RequireEndpoint(issuer, allowHttpForDevelopment);
        _metadataAddress = RequireEndpoint(metadataAddress, allowHttpForDevelopment);
        _issuerName = issuer;
    }

    public async Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var metadata = await FetchJsonAsync(_metadataAddress, 64 * 1024, cancellationToken);
            if (metadata.RootElement.ValueKind != JsonValueKind.Object ||
                ReadString(metadata.RootElement, "issuer") != _issuerName)
                return false;

            var jwksAddress = ReadString(metadata.RootElement, "jwks_uri");
            if (!Uri.TryCreate(jwksAddress, UriKind.Absolute, out var jwks) ||
                jwks.Scheme != _issuer.Scheme ||
                !jwks.Host.Equals(_issuer.Host, StringComparison.OrdinalIgnoreCase) ||
                jwks.Port != _issuer.Port ||
                jwks.UserInfo.Length > 0 || jwks.Fragment.Length > 0)
                return false;

            using var document = await FetchJsonAsync(jwks, 128 * 1024, cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("keys", out var keys) ||
                keys.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var key in keys.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.Object ||
                    string.IsNullOrWhiteSpace(ReadString(key, "kid")) ||
                    (ReadString(key, "use") is { } usage && usage != "sig"))
                    continue;

                var type = ReadString(key, "kty");
                if (type == "RSA" &&
                    !string.IsNullOrWhiteSpace(ReadString(key, "n")) &&
                    !string.IsNullOrWhiteSpace(ReadString(key, "e")))
                    return true;
                if (type == "EC" &&
                    !string.IsNullOrWhiteSpace(ReadString(key, "crv")) &&
                    !string.IsNullOrWhiteSpace(ReadString(key, "x")) &&
                    !string.IsNullOrWhiteSpace(ReadString(key, "y")))
                    return true;
                if (type == "OKP" &&
                    !string.IsNullOrWhiteSpace(ReadString(key, "crv")) &&
                    !string.IsNullOrWhiteSpace(ReadString(key, "x")))
                    return true;
            }
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Never leak internal hosts, raw metadata or provider exceptions.
            return false;
        }
    }

    private async Task<JsonDocument> FetchJsonAsync(Uri uri, long limit, CancellationToken ct)
    {
        // ResponseHeadersRead avoids buffering an attacker-controlled body
        // before the explicit maximum length below.
        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException("OIDC dependency not ready.");
        await response.Content.LoadIntoBufferAsync(limit, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 16 }, ct);
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;

    private static Uri RequireEndpoint(string value, bool allowHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps || allowHttp && uri.Scheme == Uri.UriSchemeHttp) ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Invalid OIDC readiness URL.");
        return uri;
    }

    public void Dispose() => _client.Dispose();
}
