using Npgsql;
using Peritial.Business;
using Peritial.Domains;

namespace Peritial.Infrastructures;

/// <summary>
/// V004 link adapter. A composite FK and FORCE RLS enforce the same-tenant
/// relationship even if a future caller makes an authorization mistake.
/// Successful links and audit rows commit in one PG transaction.
/// </summary>
public sealed class PgPeriziaSinistroLinkRepository(NpgsqlDataSource source)
    : IPeriziaSinistroLinkRepository
{
    public async Task LinkAuditedAsync(TenantId tenant, Guid periziaId, Guid sinistroId,
        SecurityAuditEvent audit, CancellationToken ct = default)
    {
        if (periziaId == Guid.Empty || sinistroId == Guid.Empty)
            throw new ArgumentException("Both relation identifiers are required.");
        if (audit.TenantId != tenant)
            throw new UnauthorizedAccessException("Cross-tenant link audit denied.");
        if (audit.ResourceId != periziaId ||
            audit.Action != "perizia.link-sinistro" || audit.Outcome != "succeeded")
            throw new ArgumentException("Audit does not describe the successful link.", nameof(audit));

        await using var scope = await TenantDbScope.OpenAsync(source, tenant, ct);

        // Foreign and missing Sinistro IDs are indistinguishable. The
        // database FK remains the final guard against cross-tenant links.
        await using (var exists = scope.Command(
            "SELECT 1 FROM sinistri WHERE tenant_id=@tenant AND id=@sinistro"))
        {
            exists.Parameters.AddWithValue("tenant", tenant.Value);
            exists.Parameters.AddWithValue("sinistro", sinistroId);
            if (await exists.ExecuteScalarAsync(ct) is null)
                throw new KeyNotFoundException("Resource not found.");
        }

        // Single-shot compare-and-swap. Competing links serialize on the
        // perizia row lock: at most one can write and append success audit.
        await using (var update = scope.Command(
            "UPDATE perizie SET sinistro_id=@sinistro " +
            "WHERE tenant_id=@tenant AND id=@perizia AND sinistro_id IS NULL"))
        {
            update.Parameters.AddWithValue("tenant", tenant.Value);
            update.Parameters.AddWithValue("perizia", periziaId);
            update.Parameters.AddWithValue("sinistro", sinistroId);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
            {
                await using var exists = scope.Command(
                    "SELECT 1 FROM perizie WHERE tenant_id=@tenant AND id=@perizia");
                exists.Parameters.AddWithValue("tenant", tenant.Value);
                exists.Parameters.AddWithValue("perizia", periziaId);
                if (await exists.ExecuteScalarAsync(ct) is null)
                    throw new KeyNotFoundException("Resource not found.");
                throw new PeriziaConcurrencyConflictException();
            }
        }

        await PgSecurityAuditTrail.AppendWithinScopeAsync(scope, audit, ct);
        await scope.CommitAsync(ct);
    }

    public async Task<Guid?> FindSinistroIdForPeriziaAsync(
        TenantId tenant, Guid periziaId, CancellationToken ct = default)
    {
        if (periziaId == Guid.Empty)
            throw new ArgumentException("Perizia id required.", nameof(periziaId));
        await using var scope = await TenantDbScope.OpenAsync(source, tenant, ct);
        Guid? sinistroId = null;
        await using (var command = scope.Command(
            "SELECT sinistro_id FROM perizie WHERE tenant_id=@tenant AND id=@perizia"))
        {
            command.Parameters.AddWithValue("tenant", tenant.Value);
            command.Parameters.AddWithValue("perizia", periziaId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new KeyNotFoundException("Perizia not found.");
            if (!reader.IsDBNull(0)) sinistroId = reader.GetGuid(0);
        }
        await scope.CommitAsync(ct);
        return sinistroId;
    }

    public async Task<IReadOnlyList<Guid>> ListPeriziaIdsAsync(
        TenantId tenant, Guid sinistroId, CancellationToken ct = default)
    {
        if (sinistroId == Guid.Empty)
            throw new ArgumentException("Sinistro id required.", nameof(sinistroId));

        await using var scope = await TenantDbScope.OpenAsync(source, tenant, ct);
        await using (var exists = scope.Command(
            "SELECT 1 FROM sinistri WHERE tenant_id=@tenant AND id=@sinistro"))
        {
            exists.Parameters.AddWithValue("tenant", tenant.Value);
            exists.Parameters.AddWithValue("sinistro", sinistroId);
            if (await exists.ExecuteScalarAsync(ct) is null)
                throw new KeyNotFoundException("Resource not found.");
        }

        var ids = new List<Guid>();
        await using (var command = scope.Command(
            "SELECT id FROM perizie WHERE tenant_id=@tenant AND sinistro_id=@sinistro ORDER BY id"))
        {
            command.Parameters.AddWithValue("tenant", tenant.Value);
            command.Parameters.AddWithValue("sinistro", sinistroId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                ids.Add(reader.GetGuid(0));
        }
        await scope.CommitAsync(ct);
        return ids;
    }
}
