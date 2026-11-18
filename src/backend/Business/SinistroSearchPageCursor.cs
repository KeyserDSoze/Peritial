using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Peritial.Business;

/// <summary>
/// Versioned keyset cursor bound to a normalized search filter. This is
/// NOT signed or a bearer credential: scopes + PostgreSQL FORCE RLS are
/// independently enforced on every page. The fingerprint only detects
/// inconsistent filter changes between successive requests.
/// </summary>
public static class SinistroSearchPageCursor
{
    private const string Version = "s1";
    private const int MaxLength = 128;

    public static string Encode(SinistroSearchFilter filter, SinistroPageBoundary boundary) =>
        Version + "." + SinistroPageCursor.Encode(boundary) + "." + Fingerprint(filter);

    public static SinistroPageBoundary Decode(string cursor, SinistroSearchFilter filter)
    {
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxLength)
            throw new ArgumentException("Invalid filtered Sinistri cursor.", nameof(cursor));
        var parts = cursor.Split('.');
        if (parts.Length != 3 || parts[0] != Version || parts[2].Length != 16)
            throw new ArgumentException("Invalid filtered Sinistri cursor.", nameof(cursor));
        var boundary = SinistroPageCursor.Decode(parts[1]);
        if (!string.Equals(cursor, Encode(filter, boundary), StringComparison.Ordinal))
            throw new ArgumentException("Sinistri cursor does not match search filters.", nameof(cursor));
        return boundary;
    }

    private static string Fingerprint(SinistroSearchFilter filter)
    {
        // Prefix may itself contain separators; include its length to make the
        // digest input unambiguous without leaking the raw reference in cursors.
        var context = filter.Prefix.Length.ToString(CultureInfo.InvariantCulture) +
            ":" + filter.Prefix + "|" +
            (filter.OccurredFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-") +
            "|" + (filter.OccurredTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(context));
        return Convert.ToHexString(digest)[..16].ToLowerInvariant();
    }
}
