using System.Globalization;
using System.Text;

namespace Peritial.Business;

/// <summary>
/// Stable keyset boundary for (occurred_on DESC, id ASC). It is positional
/// pagination metadata, NOT an authorization token or a tenant identifier.
/// Every query still authenticates the caller and applies PostgreSQL FORCE RLS.
/// </summary>
public sealed record SinistroPageBoundary(DateOnly OccurredOn, Guid Id);

public sealed record SinistroPageDto(IReadOnlyList<SinistroDto> Items, string? NextCursor);

public static class SinistroPageCursor
{
    private const int MaxLength = 128;

    public static string Encode(SinistroPageBoundary value)
    {
        if (value.OccurredOn == default || value.Id == Guid.Empty)
            throw new ArgumentException("Invalid page boundary.", nameof(value));
        var plain = value.OccurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            + "|" + value.Id.ToString("D");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(plain))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static SinistroPageBoundary Decode(string encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxLength ||
            encoded.Any(c => !((c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                c is '-' or '_')))
            throw new ArgumentException("Invalid Sinistri cursor.", nameof(encoded));

        string plain;
        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            plain = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            throw new ArgumentException("Invalid Sinistri cursor.", nameof(encoded));
        }
        if (plain.Length != 47 || plain[10] != '|' ||
            !DateOnly.TryParseExact(plain[..10], "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            !Guid.TryParseExact(plain[11..], "D", out var id) || id == Guid.Empty)
            throw new ArgumentException("Invalid Sinistri cursor.", nameof(encoded));

        var boundary = new SinistroPageBoundary(date, id);
        if (!string.Equals(encoded, Encode(boundary), StringComparison.Ordinal))
            throw new ArgumentException("Noncanonical Sinistri cursor.", nameof(encoded));
        return boundary;
    }
}
