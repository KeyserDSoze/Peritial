using Peritial.Domains;

namespace Peritial.Business;

public sealed record PeriziaDto(Guid Id, string Reference, string Status, string? AssignedTo, DateTime CreatedAtUtc)
{
    public static PeriziaDto From(Perizia item) => new(item.Id, item.Reference, item.Status.ToString(), item.AssignedTo, item.CreatedAtUtc);
}

public sealed class PeriziaService(ITenantContext context, IPeriziaRepository repository)
{
    public async Task<IReadOnlyList<PeriziaDto>> ListAsync(CancellationToken ct = default)
    {
        Require("peritial.read");
        var items = await repository.ListAsync(context.TenantId, ct);
        return items.Select(PeriziaDto.From).ToArray();
    }

    public async Task<PeriziaDto> CreateAsync(string reference, CancellationToken ct = default)
    {
        Require("peritial.write");
        var perizia = Perizia.Create(context.TenantId, reference);
        await repository.AddAsync(perizia, ct);
        return PeriziaDto.From(perizia);
    }

    public async Task<PeriziaDto> AssignAsync(Guid id, string assignee, CancellationToken ct = default)
    {
        Require("peritial.write");
        var perizia = await GetRequiredAsync(id, ct);
        perizia.Assign(context.TenantId, assignee);
        await repository.UpdateAsync(perizia, ct);
        return PeriziaDto.From(perizia);
    }

    public async Task<PeriziaDto> AdvanceAsync(Guid id, PeriziaStatus target, CancellationToken ct = default)
    {
        Require("peritial.write");
        var perizia = await GetRequiredAsync(id, ct);
        perizia.Advance(context.TenantId, target);
        await repository.UpdateAsync(perizia, ct);
        return PeriziaDto.From(perizia);
    }

    private async Task<Perizia> GetRequiredAsync(Guid id, CancellationToken ct) =>
        await repository.FindAsync(context.TenantId, id, ct) ?? throw new KeyNotFoundException("Perizia not found.");

    private void Require(string scope)
    {
        if (!context.HasScope(scope) && !(scope == "peritial.read" && context.HasScope("peritial.write")))
            throw new UnauthorizedAccessException("Insufficient OAuth scope.");
    }
}
