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

    /// <summary>
    /// One SQL UPDATE changes source to target only if the old link is still
    /// exactly as observed. Both the target existence check and mutation
    /// run in the signed tenant's transaction, with append-only audit.
    /// </summary>
    public async Task ReparentAuditedAsync(
        TenantId tenant, Guid periziaId, Guid expectedSinistroId,
        Guid newSinistroId, SecurityAuditEvent audit, CancellationToken ct = default)
    {
        if (periziaId == Guid.Empty || expectedSinistroId == Guid.Empty ||
            newSinistroId == Guid.Empty || newSinistroId == expectedSinistroId)
            throw new ArgumentException("Distinct, nonempty perizia/source/target IDs required.");
        if (audit.TenantId != tenant)
            throw new UnauthorizedAccessException("Cross-tenant reparent audit denied.");
        if (audit.ResourceId != periziaId ||
            audit.Action != "perizia.reparent-sinistro" || audit.Outcome != "succeeded")
            throw new ArgumentException("Audit must describe successful reparent.", nameof(audit));

        await using var scope = await TenantDbScope.OpenAsync(source, tenant, ct);
        await using (var exists = scope.Command(
            "SELECT 1 FROM sinistri WHERE tenant_id=@tenant AND id=@target"))
        {
            exists.Parameters.AddWithValue("tenant", tenant.Value);
            exists.Parameters.AddWithValue("target", newSinistroId);
            if (await exists.ExecuteScalarAsync(ct) is null)
                throw new KeyNotFoundException("Resource not found.");
        }
        await using (var command = scope.Command(
            "UPDATE perizie SET sinistro_id=@target " +
            "WHERE tenant_id=@tenant AND id=@perizia AND sinistro_id=@expected " +
            "AND status NOT IN (@billed,@duplicated,@cancelled)"))
        {
            command.Parameters.AddWithValue("tenant", tenant.Value);
            command.Parameters.AddWithValue("perizia", periziaId);
            command.Parameters.AddWithValue("target", newSinistroId);
            command.Parameters.AddWithValue("expected", expectedSinistroId);
            // Terminal statuses are never silently corrected after billing,
            // duplication or cancellation. Row-locked status guard makes
            // concurrent workflow changes conflict rather than disappear.
            command.Parameters.AddWithValue("billed", (int)PeriziaStatus.Billed);
            command.Parameters.AddWithValue("duplicated", (int)PeriziaStatus.Duplicated);
            command.Parameters.AddWithValue("cancelled", (int)PeriziaStatus.Cancelled);
            int affected;
            try
            {
                affected = await command.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (
                ex.SqlState == PostgresErrorCodes.ForeignKeyViolation &&
                ex.ConstraintName == "fk_perizie_sinistro_same_tenant")
            {
                // Target vanished (or became inaccessible) between SELECT
                // and UPDATE. No success audit, no tenant identifier leaked.
                throw new KeyNotFoundException("Resource not found.", ex);
            }
            if (affected != 1)
            {
                await using var check = scope.Command(
                    "SELECT 1 FROM perizie WHERE tenant_id=@tenant AND id=@perizia");
                check.Parameters.AddWithValue("tenant", tenant.Value);
                check.Parameters.AddWithValue("perizia", periziaId);
                if (await check.ExecuteScalarAsync(ct) is null)
                    throw new KeyNotFoundException("Resource not found.");
                throw new PeriziaConcurrencyConflictException();
            }
        }
        await PgSecurityAuditTrail.AppendWithinScopeAsync(scope, audit, ct);
        await scope.CommitAsync(ct);
    }

    /// <summary>
    /// Single-shot CAS unlink scoped to signed tenant. A concurrent re-link
    /// or unlink returns conflict rather than silently deleting a new link.
    /// Persist the matching UPDATE and append-only audit in one transaction.
    /// </summary>
    public async Task UnlinkAuditedAsync(
        TenantId tenant, Guid periziaId, Guid expectedSinistroId,
        SecurityAuditEvent audit, CancellationToken ct = default)
    {
        if (periziaId == Guid.Empty || expectedSinistroId == Guid.Empty)
            throw new ArgumentException("Perizia and last-observed Sinistro IDs required.");
        if (audit.TenantId != tenant)
            throw new UnauthorizedAccessException("Cross-tenant unlink audit denied.");
        if (audit.ResourceId != periziaId ||
            audit.Action != "perizia.unlink-sinistro" || audit.Outcome != "succeeded")
            throw new ArgumentException("Audit must describe successful unlink.", nameof(audit));

        await using var scope = await TenantDbScope.OpenAsync(source, tenant, ct);
        await using (var update = scope.Command(
            "UPDATE perizie SET sinistro_id=NULL " +
            "WHERE tenant_id=@tenant AND id=@perizia AND sinistro_id=@expected"))
        {
            update.Parameters.AddWithValue("tenant", tenant.Value);
            update.Parameters.AddWithValue("perizia", periziaId);
            update.Parameters.AddWithValue("expected", expectedSinistroId);
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
