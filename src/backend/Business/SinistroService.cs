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

    /// <summary>Server-side, case-insensitive autocomplete prefix plus optional date bounds.</summary>
    public async Task<IReadOnlyList<SinistroDto>> SearchAsync(
        string prefix, DateOnly? occurredFrom = null, DateOnly? occurredTo = null,
        int limit = 10, CancellationToken ct = default)
    {
        RequireRead();
        var filter = SinistroSearchFilter.Create(prefix, occurredFrom, occurredTo, limit);
        var results = await repository.SearchAsync(context.TenantId, filter.Prefix,
            filter.OccurredFrom, filter.OccurredTo, filter.Limit, ct);
        return results.Select(SinistroDto.From).ToArray();
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

/// <summary>
/// One canonical filter contract used both by Business and the PG adapter.
/// The prefix is literal; SQL wildcards are not a supported search language.
/// </summary>
public sealed record SinistroSearchFilter(
    string Prefix, DateOnly? OccurredFrom, DateOnly? OccurredTo, int Limit)
{
    public static SinistroSearchFilter Create(
        string prefix, DateOnly? occurredFrom = null, DateOnly? occurredTo = null, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Search prefix required.", nameof(prefix));
        var normalized = prefix.Trim();
        if (normalized.Length is < 2 or > 100)
            throw new ArgumentException("Search prefix must be 2–100 characters.", nameof(prefix));
        if (limit is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(limit), "Search limit must be 1–50.");
        if (occurredFrom is { } from && from == default)
            throw new ArgumentException("Invalid lower occurrence date.", nameof(occurredFrom));
        if (occurredTo is { } to && to == default)
            throw new ArgumentException("Invalid upper occurrence date.", nameof(occurredTo));
        if (occurredFrom > occurredTo)
            throw new ArgumentException("Occurrence date range is reversed.", nameof(occurredTo));
        return new SinistroSearchFilter(normalized, occurredFrom, occurredTo, limit);
    }
}
