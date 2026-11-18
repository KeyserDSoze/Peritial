using Peritial.Domains;

namespace Peritial.Business;

/// <summary>Allowlisted link response: never exposes tenant, subject or audit metadata.</summary>
public sealed record PeriziaSinistroLinkDto(Guid PeriziaId, Guid SinistroId);
/// <summary>Tenant-safe read projection; a visible unlinked perizia returns null.</summary>
public sealed record PeriziaSinistroAssociationDto(Guid PeriziaId, Guid? SinistroId);

/// <summary>
/// Associations require an explicit signed write: link when null, unlink
/// with expected ID, or reparent in one atomic source-to-target CAS.
/// No operation silently overwrites a concurrently changed association.
/// </summary>
public sealed class PeriziaSinistroLinkService(
    ITenantContext context, IPeriziaSinistroLinkRepository links, ISecurityAuditTrail audit)
{
    public async Task<PeriziaSinistroLinkDto> LinkAsync(
        Guid periziaId, Guid sinistroId, CancellationToken ct = default)
    {
        await RequireWriteAsync("perizia.link-sinistro", ct);
        if (periziaId == Guid.Empty)
            throw new ArgumentException("Perizia id required.", nameof(periziaId));
        if (sinistroId == Guid.Empty)
            throw new ArgumentException("Sinistro id required.", nameof(sinistroId));

        var success = Event("perizia.link-sinistro", "succeeded", periziaId);
        await links.LinkAuditedAsync(context.TenantId, periziaId, sinistroId, success, ct);
        return new PeriziaSinistroLinkDto(periziaId, sinistroId);
    }

    /// <summary>
    /// A single source-to-target compare-and-swap: no intermediate unlink,
    /// no silent overwrite of another writer, no deletion of either record.
    /// </summary>
    public async Task<PeriziaSinistroLinkDto> ReparentAsync(
        Guid periziaId, Guid expectedSinistroId, Guid newSinistroId,
        CancellationToken ct = default)
    {
        await RequireWriteAsync("perizia.reparent-sinistro", ct);
        if (periziaId == Guid.Empty)
            throw new ArgumentException("Perizia id required.", nameof(periziaId));
        if (expectedSinistroId == Guid.Empty)
            throw new ArgumentException("Expected Sinistro id required.", nameof(expectedSinistroId));
        if (newSinistroId == Guid.Empty)
            throw new ArgumentException("New Sinistro id required.", nameof(newSinistroId));
        if (newSinistroId == expectedSinistroId)
            throw new ArgumentException("A different target Sinistro is required.", nameof(newSinistroId));

        await links.ReparentAuditedAsync(context.TenantId, periziaId,
            expectedSinistroId, newSinistroId,
            Event("perizia.reparent-sinistro", "succeeded", periziaId), ct);
        return new PeriziaSinistroLinkDto(periziaId, newSinistroId);
    }

    /// <summary>
    /// Detaches only the Sinistro link the client actually observed.
    /// No implicit reparent, cascade or modification of either entity.
    /// </summary>
    public async Task<PeriziaSinistroAssociationDto> UnlinkAsync(
        Guid periziaId, Guid expectedSinistroId, CancellationToken ct = default)
    {
        await RequireWriteAsync("perizia.unlink-sinistro", ct);
        if (periziaId == Guid.Empty)
            throw new ArgumentException("Perizia id required.", nameof(periziaId));
        if (expectedSinistroId == Guid.Empty)
            throw new ArgumentException("Expected Sinistro id required.", nameof(expectedSinistroId));

        var success = Event("perizia.unlink-sinistro", "succeeded", periziaId);
        await links.UnlinkAuditedAsync(context.TenantId, periziaId, expectedSinistroId, success, ct);
        return new PeriziaSinistroAssociationDto(periziaId, null);
    }

    public async Task<PeriziaSinistroAssociationDto> GetPeriziaAssociationAsync(
        Guid periziaId, CancellationToken ct = default)
    {
        RequireRead();
        if (periziaId == Guid.Empty)
            throw new ArgumentException("Perizia id required.", nameof(periziaId));
        var sinistroId = await links.FindSinistroIdForPeriziaAsync(
            context.TenantId, periziaId, ct);
        return new PeriziaSinistroAssociationDto(periziaId, sinistroId);
    }

    public Task<IReadOnlyList<Guid>> ListPeriziaIdsAsync(
        Guid sinistroId, CancellationToken ct = default)
    {
        RequireRead();
        if (sinistroId == Guid.Empty)
            throw new ArgumentException("Sinistro id required.", nameof(sinistroId));
        return links.ListPeriziaIdsAsync(context.TenantId, sinistroId, ct);
    }

    private void RequireRead()
    {
        if (!context.HasScope("peritial.read") && !context.HasScope("peritial.write"))
            throw new UnauthorizedAccessException("Insufficient OAuth scope.");
    }

    private async Task RequireWriteAsync(string action, CancellationToken ct)
    {
        if (context.HasScope("peritial.write")) return;
        await audit.AppendAsync(Event(action, "denied", null), ct);
        throw new UnauthorizedAccessException("Insufficient OAuth scope.");
    }

    private SecurityAuditEvent Event(string action, string outcome, Guid? resourceId) =>
        new(Guid.NewGuid(), context.TenantId, context.Subject, action,
            outcome, resourceId, DateTime.UtcNow);
}
