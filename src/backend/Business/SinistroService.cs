using Peritial.Domains;

namespace Peritial.Business;

/// <summary>
/// Safe tenant-owned projection for REST/MCP. Never exposes tenant ID, audit
/// identities, internal database information or legacy relationship keys.
/// </summary>
public sealed record SinistroDto(
    Guid Id, string ExternalReference, DateOnly OccurredOn,
    DateOnly? ReportedOn, DateTime CreatedAtUtc, long Version)
{
    public static SinistroDto From(Sinistro item) => new(
        item.Id, item.ExternalReference, item.OccurredOn, item.ReportedOn,
        item.CreatedAtUtc, item.PersistedVersion);
}

public sealed class SinistroService(
    ITenantContext context, ISinistroRepository repository, ISecurityAuditTrail audit)
{
    public async Task<IReadOnlyList<SinistroDto>> ListAsync(int limit = 50, CancellationToken ct = default)
    {
        RequireRead();
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var items = await repository.ListAsync(context.TenantId, limit, ct);
        return items.Select(SinistroDto.From).ToArray();
    }

    public async Task<SinistroDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        return SinistroDto.From(await FindRequiredAsync(id, ct));
    }

    public async Task<SinistroDto> CreateAsync(
        string externalReference, DateOnly occurredOn, DateOnly? reportedOn = null,
        CancellationToken ct = default)
    {
        await RequireWriteAsync("sinistro.create", ct);
        var item = Sinistro.Create(context.TenantId, externalReference, occurredOn, reportedOn);
        await repository.AddAuditedAsync(item, Event("sinistro.create", "succeeded", item.Id), ct);
        return SinistroDto.From(item);
    }

    /// <summary>
    /// Callers MUST supply the last observed version; never fetch and
    /// unconditionally replace a case under an outdated user form.
    /// Repository performs the final SQL CAS after this early check.
    /// </summary>
    public async Task<SinistroDto> UpdateAsync(
        Guid id, string externalReference, DateOnly occurredOn,
        DateOnly? reportedOn, long expectedVersion, CancellationToken ct = default)
    {
        await RequireWriteAsync("sinistro.update", ct);
        if (expectedVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        var item = await FindRequiredAsync(id, ct);
        if (item.PersistedVersion != expectedVersion)
            throw new SinistroConcurrencyConflictException();
        item.Update(context.TenantId, externalReference, occurredOn, reportedOn);
        await repository.UpdateAuditedAsync(item, Event("sinistro.update", "succeeded", id), ct);
        return SinistroDto.From(item);
    }

    private async Task<Sinistro> FindRequiredAsync(Guid id, CancellationToken ct) =>
        await repository.FindAsync(context.TenantId, id, ct)
        ?? throw new KeyNotFoundException("Sinistro not found.");

    private void RequireRead()
    {
        if (!context.HasScope("peritial.read") && !context.HasScope("peritial.write"))
            throw new UnauthorizedAccessException("Insufficient OAuth scope.");
    }

    private async Task RequireWriteAsync(string action, CancellationToken ct)
    {
        if (context.HasScope("peritial.write")) return;
        // Never audit a raw reference, request body or unvalidated actor payload.
        await audit.AppendAsync(Event(action, "denied", null), ct);
        throw new UnauthorizedAccessException("Insufficient OAuth scope.");
    }

    private SecurityAuditEvent Event(string action, string outcome, Guid? resource) =>
        new(Guid.NewGuid(), context.TenantId, context.Subject, action,
            outcome, resource, DateTime.UtcNow);
}

/// <summary>A stale optimistic version; map to HTTP 409, not a 404 or a 500.</summary>
public sealed class SinistroConcurrencyConflictException()
    : Exception("Sinistro has changed since it was read.")
{
}
