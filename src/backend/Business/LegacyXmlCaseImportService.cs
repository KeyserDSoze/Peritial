using System.Security.Cryptography;
using Peritial.Domains;

namespace Peritial.Business;

/// <summary>One committed Sinistro ID or the identical prior import.</summary>
public sealed record LegacyXmlCaseImportResult(Guid SinistroId, bool Replayed);

/// <summary>Safe public projection: never returns identities, raw XML or file bytes.</summary>
public sealed record LegacyXmlCaseImportDto(
    Guid SinistroId, string Status, LegacyXmlPreviewDto Preview);

/// <summary>
/// The implementation must atomically commit idempotency, Sinistro and success
/// audit within one tenant-RLS PostgreSQL transaction. Not an import API.
/// </summary>
public interface ILegacyXmlCaseImportStore
{
    Task<LegacyXmlCaseImportResult> CommitAsync(TenantId tenant, string subject,
        string key, byte[] requestDigest, LegacyXmlPreviewDto preview,
        CancellationToken ct = default);
}

public sealed class LegacyXmlIdempotencyConflictException()
    : Exception("XML import idempotency key is already bound to different content.")
{
}

public sealed class LegacyXmlReferenceConflictException()
    : Exception("XML import reference already exists and requires manual reconciliation.")
{
}

/// <summary>
/// Internal only. No REST/MCP route or DI registration until all case writer
/// paths share a uniqueness/concurrency gate, and richer import is reviewed.
/// </summary>
public sealed class LegacyXmlCaseImportService(
    ITenantContext context, LegacyXmlPreviewService preview,
    ILegacyXmlCaseImportStore store)
{
    public async Task<LegacyXmlCaseImportDto> CommitAsync(
        ReadOnlyMemory<byte> xml, string idempotencyKey, CancellationToken ct = default)
    {
        // Verify the signed actor before inspecting any untrusted XML.
        if (!context.HasScope("peritial.write"))
            throw new UnauthorizedAccessException("Legacy XML import requires peritial.write.");
        ValidateIdempotencyKey(idempotencyKey);

        // Only three scalar facts are allowed; don't silently ignore PII,
        // policies, attachments, unscanned binary payloads or source metadata.
        var facts = preview.PreviewCaseOnly(xml);
        var digest = SHA256.HashData(xml.Span);
        var committed = await store.CommitAsync(context.TenantId, context.Subject,
            idempotencyKey, digest, facts, ct);
        return new LegacyXmlCaseImportDto(committed.SinistroId,
            committed.Replayed ? "replayed" : "created", facts);
    }

    internal static void ValidateIdempotencyKey(string? key)
    {
        if (key is null || key.Length is < 16 or > 128 ||
            key.Any(c => !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c is '-' or '_' or ':' or '.')))
            throw new ArgumentException(
                "XML import idempotency key must be 16–128 safe ASCII characters.",
                nameof(key));
    }
}
