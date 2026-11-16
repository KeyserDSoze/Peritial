namespace Peritial.Domains;

/// <summary>
/// Tenant-owned accident identity and minimal facts. No legacy persistence or
/// authorization identity is inferred from an HTTP/MCP payload.
/// The remaining insurance, policy, person and appraisal relationships are
/// intentionally left for the dedicated #7/#8 persistence slices.
/// </summary>
public sealed class Sinistro
{
    public Guid Id { get; }
    public TenantId TenantId { get; }
    public string ExternalReference { get; private set; }
    public DateOnly OccurredOn { get; private set; }
    public DateOnly? ReportedOn { get; private set; }
    public DateTime CreatedAtUtc { get; }

    private Sinistro(Guid id, TenantId tenant, string externalReference,
        DateOnly occurredOn, DateOnly? reportedOn, DateTime createdAtUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Invalid accident id.", nameof(id));
        if (tenant.Value == Guid.Empty) throw new ArgumentException("Invalid tenant.", nameof(tenant));
        var normalizedReference = ValidateReference(externalReference);
        ValidateDates(occurredOn, reportedOn);

        Id = id;
        TenantId = tenant;
        ExternalReference = normalizedReference;
        OccurredOn = occurredOn;
        ReportedOn = reportedOn;
        CreatedAtUtc = createdAtUtc;
    }

    public static Sinistro Create(TenantId tenant, string externalReference,
        DateOnly occurredOn, DateOnly? reportedOn = null) =>
        new(Guid.NewGuid(), tenant, externalReference, occurredOn, reportedOn, DateTime.UtcNow);

    /// <summary>Rehydrate from a trusted, tenant-filtered persistence adapter only.</summary>
    public static Sinistro Restore(Guid id, TenantId tenant, string externalReference,
        DateOnly occurredOn, DateOnly? reportedOn, DateTime createdAtUtc) =>
        new(id, tenant, externalReference, occurredOn, reportedOn, createdAtUtc);

    /// <summary>
    /// Update simple identifying facts, atomically at aggregate level.
    /// Persistence adapters must additionally enforce RLS and optimistic concurrency.
    /// </summary>
    public void Update(TenantId actorTenant, string externalReference,
        DateOnly occurredOn, DateOnly? reportedOn = null)
    {
        if (actorTenant != TenantId)
            throw new UnauthorizedAccessException("Cross-tenant accident operation denied.");

        // Validate *everything* before altering any property.
        var normalizedReference = ValidateReference(externalReference);
        ValidateDates(occurredOn, reportedOn);
        ExternalReference = normalizedReference;
        OccurredOn = occurredOn;
        ReportedOn = reportedOn;
    }

    private static string ValidateReference(string externalReference)
    {
        if (string.IsNullOrWhiteSpace(externalReference))
            throw new ArgumentException("External reference required.", nameof(externalReference));

        var normalized = externalReference.Trim();
        if (normalized.Length > 100)
            throw new ArgumentException("External reference too long.", nameof(externalReference));
        return normalized;
    }

    private static void ValidateDates(DateOnly occurredOn, DateOnly? reportedOn)
    {
        if (occurredOn == default)
            throw new ArgumentException("Occurrence date required.", nameof(occurredOn));
        if (reportedOn == default(DateOnly))
            throw new ArgumentException("Invalid report date.", nameof(reportedOn));
        // No ordering rule until representative legacy records are characterized.
    }
}
