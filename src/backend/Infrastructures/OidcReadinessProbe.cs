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
                if (HasUsableSigningMaterial(key))
                    return true;
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

    // A JWKS with a "kid" and nonempty strings is not evidence of an
    // operational signing key. Check canonical JWK material, supported
    // signature algorithms and expected public-key sizes before reporting ready.
    // This is structural validation, not an IdP trust or JWT signature check.
    private static bool HasUsableSigningMaterial(JsonElement key)
    {
        if (key.ValueKind != JsonValueKind.Object ||
            string.IsNullOrWhiteSpace(ReadString(key, "kid")) ||
            (ReadString(key, "use") is { } use && use != "sig"))
            return false;

        if (key.TryGetProperty("key_ops", out var operations) &&
            (operations.ValueKind != JsonValueKind.Array ||
             !operations.EnumerateArray().Any(op =>
                 op.ValueKind == JsonValueKind.String && op.GetString() == "verify")))
            return false;

        var algorithm = ReadString(key, "alg");
        var type = ReadString(key, "kty");
        if (type == "RSA")
        {
            if (algorithm is not null && algorithm is not
                ("RS256" or "RS384" or "RS512" or "PS256" or "PS384" or "PS512"))
                return false;
            if (!TryMaterial(key, "n", out var modulus) ||
                !TryMaterial(key, "e", out var exponent) ||
                modulus.Length is < 256 or > 1024 ||
                (modulus[0] & 0x80) == 0 || (modulus[^1] & 1) == 0 ||
                exponent.Length is < 1 or > 4 || exponent[0] == 0)
                return false;
            long e = 0;
            foreach (var part in exponent)
                e = (e << 8) | part;
            return e >= 3 && (e & 1) != 0;
        }
        if (type == "EC")
        {
            var coordinateLength = ReadString(key, "crv") switch
            {
                "P-256" when algorithm is null or "ES256" => 32,
                "P-384" when algorithm is null or "ES384" => 48,
                "P-521" when algorithm is null or "ES512" => 66,
                _ => 0
            };
            return coordinateLength > 0 &&
                TryMaterial(key, "x", out var x) && x.Length == coordinateLength &&
                TryMaterial(key, "y", out var y) && y.Length == coordinateLength;
        }
        if (type == "OKP")
        {
            if (algorithm is not null && algorithm != "EdDSA")
                return false;
            var length = ReadString(key, "crv") switch
            {
                "Ed25519" => 32,
                "Ed448" => 57,
                _ => 0
            };
            return length > 0 && TryMaterial(key, "x", out var x) && x.Length == length;
        }
        return false;
    }

    private static bool TryMaterial(JsonElement key, string property, out byte[] material)
    {
        material = [];
        var value = ReadString(key, property);
        if (string.IsNullOrEmpty(value) || value.Length > 2048 ||
            value.Any(c => !((c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                c is '-' or '_')))
            return false;

        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            material = Convert.FromBase64String(base64);
            var canonical = Convert.ToBase64String(material).TrimEnd('=')
                .Replace('+', '-').Replace('/', '_');
            return string.Equals(value, canonical, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
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
