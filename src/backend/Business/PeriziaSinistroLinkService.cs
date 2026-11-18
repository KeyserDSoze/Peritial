using Peritial.Domains;

namespace Peritial.Business;

/// <summary>Allowlisted link response: never exposes tenant, subject or audit metadata.</summary>
public sealed record PeriziaSinistroLinkDto(Guid PeriziaId, Guid SinistroId);
/// <summary>Tenant-safe read projection; a visible unlinked perizia returns null.</summary>
public sealed record PeriziaSinistroAssociationDto(Guid PeriziaId, Guid? SinistroId);

/// <summary>
/// First conservative relation slice: a perizia can be linked exactly once,
/// with no implicit unlink or reparent operation. Both endpoints must belong
/// to the signed tenant. Only successful audited writes can reach persistence.
/// </summary>
public sealed class PeriziaSinistroLinkService(
    ITenantContext context, IPeriziaSinistroLinkRepository links, ISecurityAuditTrail audit)
{
    public async Task<PeriziaSinistroLinkDto> LinkAsync(
        Guid periziaId, Guid sinistroId, CancellationToken ct = default)
    {
        await RequireWriteAsync(ct);
        if (periziaId == Guid.Empty)
            throw new ArgumentException("Perizia id required.", nameof(periziaId));
        if (sinistroId == Guid.Empty)
            throw new ArgumentException("Sinistro id required.", nameof(sinistroId));

        var success = Event("perizia.link-sinistro", "succeeded", periziaId);
        await links.LinkAuditedAsync(context.TenantId, periziaId, sinistroId, success, ct);
        return new PeriziaSinistroLinkDto(periziaId, sinistroId);
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

    private async Task RequireWriteAsync(CancellationToken ct)
    {
        if (context.HasScope("peritial.write")) return;
        await audit.AppendAsync(Event("perizia.link-sinistro", "denied", null), ct);
        throw new UnauthorizedAccessException("Insufficient OAuth scope.");
    }

    private SecurityAuditEvent Event(string action, string outcome, Guid? resourceId) =>
        new(Guid.NewGuid(), context.TenantId, context.Subject, action,
            outcome, resourceId, DateTime.UtcNow);
}
