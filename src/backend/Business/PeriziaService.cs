using Peritial.Domains;

namespace Peritial.Business;

public sealed record PeriziaDto(Guid Id, string Reference, string Status, string? AssignedTo, DateTime CreatedAtUtc)
{
    public static PeriziaDto From(Perizia item) => new(item.Id, item.Reference, item.Status.ToString(), item.AssignedTo, item.CreatedAtUtc);
}

public sealed class PeriziaService(ITenantContext context, IPeriziaRepository repository, ISecurityAuditTrail audit)
{
    public async Task<IReadOnlyList<PeriziaDto>> ListAsync(CancellationToken ct = default)
    {
        RequireRead();
        var items = await repository.ListAsync(context.TenantId, ct);
        return items.Select(PeriziaDto.From).ToArray();
    }

    /// <summary>
    /// Read one visible perizia. Authorize before looking up its ID and let
    /// tenant-scoped RLS make missing and foreign IDs indistinguishable.
    /// Read-only operations never append mutation-success audit events.
    /// </summary>
    public async Task<PeriziaDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        var item = await GetRequiredAsync(id, ct);
        return PeriziaDto.From(item);
    }

    public async Task<PeriziaDto> CreateAsync(string reference, CancellationToken ct = default)
    {
        await RequireWriteAsync("perizia.create", ct);
        var perizia = Perizia.Create(context.TenantId, reference);
        await repository.AddAuditedAsync(perizia, Event("perizia.create", "succeeded", perizia.Id), ct);
        return PeriziaDto.From(perizia);
    }

    public async Task<PeriziaDto> AssignAsync(Guid id, string assignee, CancellationToken ct = default)
    {
        await RequireWriteAsync("perizia.assign", ct);
        var perizia = await GetRequiredAsync(id, ct);
        perizia.Assign(context.TenantId, assignee);
        await repository.UpdateAuditedAsync(perizia, Event("perizia.assign", "succeeded", perizia.Id), ct);
        return PeriziaDto.From(perizia);
    }

    public async Task<PeriziaDto> AdvanceAsync(Guid id, PeriziaStatus target, CancellationToken ct = default)
    {
        await RequireWriteAsync("perizia.advance", ct);
        var perizia = await GetRequiredAsync(id, ct);
        perizia.Advance(context.TenantId, target);
        await repository.UpdateAuditedAsync(perizia, Event("perizia.advance", "succeeded", perizia.Id), ct);
        return PeriziaDto.From(perizia);
    }

    /// <summary>Cancel a non-terminal perizia with tenant-scoped success audit.</summary>
    public async Task<PeriziaDto> CancelAsync(Guid id, CancellationToken ct = default)
    {
        await RequireWriteAsync("perizia.cancel", ct);
        var perizia = await GetRequiredAsync(id, ct);
        perizia.Cancel(context.TenantId);
        await repository.UpdateAuditedAsync(perizia, Event("perizia.cancel", "succeeded", perizia.Id), ct);
        return PeriziaDto.From(perizia);
    }

    private async Task<Perizia> GetRequiredAsync(Guid id, CancellationToken ct) =>
        await repository.FindAsync(context.TenantId, id, ct) ?? throw new KeyNotFoundException("Perizia not found.");

    private void RequireRead()
    {
        if (!context.HasScope("peritial.read") && !context.HasScope("peritial.write"))
            throw new UnauthorizedAccessException("Insufficient OAuth scope.");
    }

    private async Task RequireWriteAsync(string action, CancellationToken ct)
    {
        if (context.HasScope("peritial.write")) return;
        await RecordAsync(action, "denied", null, ct);
        throw new UnauthorizedAccessException("Insufficient OAuth scope.");
    }

    private Task RecordAsync(string action, string outcome, Guid? resource, CancellationToken ct) =>
        audit.AppendAsync(Event(action, outcome, resource), ct);

    private SecurityAuditEvent Event(string action, string outcome, Guid? resource) =>
        new(Guid.NewGuid(), context.TenantId, context.Subject, action, outcome, resource, DateTime.UtcNow);
}
