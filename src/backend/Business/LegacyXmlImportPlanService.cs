using System.Text;
using Peritial.Domains;

namespace Peritial.Business;

/// <summary>
/// An exact-reference reconciliation candidate from one signed tenant.
/// This is NOT evidence that a future import may safely create/update cases.
/// </summary>
public sealed record LegacySinistroMatch(
    Guid Id, DateOnly OccurredOn, DateOnly? ReportedOn);

public interface ILegacyImportReferenceLookup
{
    /// <summary>
    /// Fetch at most two candidates for an exact reference, to distinguish
    /// zero/one/multiple WITHOUT guessing which duplicate is authoritative.
    /// </summary>
    Task<IReadOnlyList<LegacySinistroMatch>> FindExactAsync(
        TenantId tenant, string reference, int limit,
        CancellationToken ct = default);
}

/// <summary>Preview and tenant-scoped reconciliation only. No writes, no import.</summary>
public sealed record LegacyXmlImportPlanDto(
    LegacyXmlPreviewDto Preview, string Status, Guid? ExistingSinistroId);

public sealed class LegacyXmlImportPlanService(
    ITenantContext context, LegacyXmlPreviewService preview,
    ILegacyImportReferenceLookup lookup)
{
    public async Task<LegacyXmlImportPlanDto> PlanUtf8Async(
        string xml, CancellationToken ct = default)
    {
        RequireWrite();
        var parsed = preview.PreviewUtf8(xml);
        return await PlanValidatedAsync(parsed, ct);
    }

    public async Task<LegacyXmlImportPlanDto> PlanAsync(
        ReadOnlyMemory<byte> xml, CancellationToken ct = default)
    {
        RequireWrite();
        var parsed = preview.Preview(xml);
        return await PlanValidatedAsync(parsed, ct);
    }

    private async Task<LegacyXmlImportPlanDto> PlanValidatedAsync(
        LegacyXmlPreviewDto parsed, CancellationToken ct)
    {
        // The unique-by-reference invariant does not exist in schema V003.
        // Never infer uniqueness from a truncated list/search or use ILIKE.
        var matches = await lookup.FindExactAsync(
            context.TenantId, parsed.ExternalReference, 2, ct);

        if (matches.Count == 0)
        {
            // Missing reference is not permission to discard data that the
            // current Sinistro aggregate cannot persist. Fail closed on
            // rich XML (including timestamp and lexical normalization) even
            // though no candidate was found in this tenant.
            return new LegacyXmlImportPlanDto(parsed,
                parsed.HasUnmappedBusinessData ? "review-required" : "new", null);
        }
        if (matches.Count > 1)
            return new LegacyXmlImportPlanDto(parsed, "ambiguous", null);

        var match = matches[0];
        if (match.OccurredOn != parsed.OccurredOn ||
            (parsed.ReportedOn.HasValue && parsed.ReportedOn != match.ReportedOn))
            return new LegacyXmlImportPlanDto(parsed, "conflict", null);

        // The legacy model links cases to a policy and yearly identity.
        // V003 has neither. Rich XML must never claim a verified match based
        // solely on numeroSinistro and date; do not disclose a candidate ID.
        if (parsed.HasUnmappedBusinessData)
            return new LegacyXmlImportPlanDto(parsed, "review-required", null);

        // Reference/date-only advisory result, NOT approval to import.
        return new LegacyXmlImportPlanDto(parsed, "existing", match.Id);
    }

    private void RequireWrite()
    {
        if (!context.HasScope("peritial.write"))
            throw new UnauthorizedAccessException("Legacy XML planning requires peritial.write.");
    }
}
