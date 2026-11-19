using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Peritial.Business;

/// <summary>
/// Positional keyset boundary; NOT an authorization token or a multi-request
/// snapshot. Tenant and scopes are always checked independently on every read.
/// </summary>
public sealed record DocumentPageBoundary(DateTime CreatedAtUtc, Guid Id);
public sealed record DocumentPageDto(IReadOnlyList<DocumentSummaryDto> Items, string? NextCursor);

public static class DocumentPageCursor
{
    private const string Version = "d1";
    private const int MaxLength = 128;

    public static string Encode(DocumentPageBoundary boundary, Guid? periziaId)
    {
        if (boundary.CreatedAtUtc.Kind != DateTimeKind.Utc ||
            boundary.CreatedAtUtc == default || boundary.Id == Guid.Empty)
            throw new ArgumentException("Invalid document page boundary.", nameof(boundary));
        var plain = boundary.CreatedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture) +
            "|" + boundary.Id.ToString("D");
        var positional = Convert.ToBase64String(Encoding.UTF8.GetBytes(plain))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Version + "." + positional + "." + Fingerprint(periziaId);
    }

    public static DocumentPageBoundary Decode(string cursor, Guid? periziaId)
    {
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxLength)
            throw new ArgumentException("Invalid document page cursor.", nameof(cursor));
        var parts = cursor.Split('.');
        if (parts.Length != 3 || parts[0] != Version ||
            parts[2].Length != 16 || parts[1].Length > 96 ||
            parts[1].Any(c => !((c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                c is '-' or '_')))
            throw new ArgumentException("Invalid document page cursor.", nameof(cursor));
        string decoded;
        try
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            throw new ArgumentException("Invalid document page cursor.", nameof(cursor));
        }
        var fields = decoded.Split('|');
        if (fields.Length != 2 ||
            !long.TryParse(fields[0], NumberStyles.None,
                CultureInfo.InvariantCulture, out var ticks) ||
            ticks < 1 || ticks > DateTime.MaxValue.Ticks ||
            !Guid.TryParseExact(fields[1], "D", out var id) || id == Guid.Empty)
            throw new ArgumentException("Invalid document page cursor.", nameof(cursor));
        var boundary = new DocumentPageBoundary(new DateTime(ticks, DateTimeKind.Utc), id);
        if (!string.Equals(cursor, Encode(boundary, periziaId), StringComparison.Ordinal))
            throw new ArgumentException("Document page cursor does not match filter.", nameof(cursor));
        return boundary;
    }

    private static string Fingerprint(Guid? periziaId)
    {
        var normalized = periziaId?.ToString("N") ?? "-";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(digest)[..16].ToLowerInvariant();
    }
}
