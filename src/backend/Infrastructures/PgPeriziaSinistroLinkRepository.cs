using Npgsql;
using NpgsqlTypes;
using Peritial.Business;
using Peritial.Domains;

namespace Peritial.Infrastructures;

/// <summary>
/// V006 tenant-scoped, monotone link revision CAS. All successful changes,
/// audit and append-only source/target provenance share one PG transaction.
/// </summary>
public sealed class PgPeriziaSinistroLinkRepository(NpgsqlDataSource source)
    : IPeriziaSinistroLinkRepository
{
    public async Task<long> LinkAuditedAsync(
        TenantId tenant, Guid periziaId, Guid sinistroId, long expectedLinkVersion,
        SecurityAuditEvent audit, CancellationToken ct = default)
    {
        if (periziaId == Guid.Empty || sinistroId == Guid.Empty)
            throw new ArgumentException("Both relation identifiers are required.");
        ValidateVersion(expectedLinkVersion);
        ValidateAudit(tenant, periziaId, audit, "perizia.link-sinistro");
        await using var scope = await TenantDbScope.OpenAsync(source, tenant, ct);
        await RequireVisibleTargetAsync(scope, tenant, sinistroId, ct);
        await using var update = scope.Command(
            "UPDATE perizie SET sinistro_id=@sinistro, " +
            "sinistro_link_version=sinistro_link_version+1 " +
            "WHERE tenant_id=@tenant AND id=@perizia AND sinistro_id IS NULL " +
            "AND sinistro_link_version=@expectedVersion RETURNING sinistro_link_version");
        update.Parameters.AddWithValue("tenant", tenant.Value);
        update.Parameters.AddWithValue("perizia", periziaId);
        update.Parameters.AddWithValue("sinistro", sinistroId);
        update.Parameters.AddWithValue("expectedVersion", expectedLinkVersion);
        var version = await RequireUpdatedVersionAsync(scope, update, tenant, periziaId, ct);
        await AppendHistoryAsync(scope, tenant, periziaId, version, null, sinistroId, audit, ct);
        await scope.CommitAsync(ct);
        return version;
    }

    public async Task<long> ReparentAuditedAsync(
        TenantId tenant, Guid periziaId, Guid expectedSinistroId,
        Guid newSinistroId, long expectedLinkVersion,
        SecurityAuditEvent audit, CancellationToken ct = default)
    {
        if (periziaId == Guid.Empty || expectedSinistroId == Guid.Empty ||
            newSinistroId == Guid.Empty || newSinistroId == expectedSinistroId)
            throw new ArgumentException("Distinct, nonempty perizia/source/target IDs required.");
        ValidateVersion(expectedLinkVersion);
        ValidateAudit(tenant, periziaId, audit, "perizia.reparent-sinistro");
        await using var scope = await TenantDbScope.OpenAsync(source, tenant, ct);
        await RequireVisibleTargetAsync(scope, tenant, newSinistroId, ct);
        await using var update = scope.Command(
            "UPDATE perizie SET sinistro_id=@target, " +
            "sinistro_link_version=sinistro_link_version+1 " +
            "WHERE tenant_id=@tenant AND id=@perizia AND sinistro_id=@expected " +
            "AND sinistro_link_version=@expectedVersion " +
            "AND status NOT IN (@billed,@duplicated,@cancelled) " +
            "RETURNING sinistro_link_version");
        update.Parameters.AddWithValue("tenant", tenant.Value);
        update.Parameters.AddWithValue("perizia", periziaId);
        update.Parameters.AddWithValue("target", newSinistroId);
        update.Parameters.AddWithValue("expected", expectedSinistroId);
        update.Parameters.AddWithValue("expectedVersion", expectedLinkVersion);
        // Workflow terminal states cannot be reparented, even under races.
        update.Parameters.AddWithValue("billed", (int)PeriziaStatus.Billed);
        update.Parameters.AddWithValue("duplicated", (int)PeriziaStatus.Duplicated);
        update.Parameters.AddWithValue("cancelled", (int)PeriziaStatus.Cancelled);
        var version = await RequireUpdatedVersionAsync(scope, update, tenant, periziaId, ct);
        await AppendHistoryAsync(scope, tenant, periziaId, version,
            expectedSinistroId, newSinistroId, audit, ct);
        await scope.CommitAsync(ct);
        return version;
    }

    public async Task<long> UnlinkAuditedAsync(
        TenantId tenant, Guid periziaId, Guid expectedSinistroId,
        long expectedLinkVersion, SecurityAuditEvent audit, CancellationToken ct = default)
    {
        if (periziaId == Guid.Empty || expectedSinistroId == Guid.Empty)
            throw new ArgumentException("Perizia and last-observed Sinistro IDs required.");
        ValidateVersion(expectedLinkVersion);
        ValidateAudit(tenant, periziaId, audit, "perizia.unlink-sinistro");
        await using var scope = await TenantDbScope.OpenAsync(source, tenant, ct);
        await using var update = scope.Command(
            "UPDATE perizie SET sinistro_id=NULL, " +
            "sinistro_link_version=sinistro_link_version+1 " +
            "WHERE tenant_id=@tenant AND id=@perizia AND sinistro_id=@expected " +
            "AND sinistro_link_version=@expectedVersion RETURNING sinistro_link_version");
        update.Parameters.AddWithValue("tenant", tenant.Value);
        update.Parameters.AddWithValue("perizia", periziaId);
        update.Parameters.AddWithValue("expected", expectedSinistroId);
        update.Parameters.AddWithValue("expectedVersion", expectedLinkVersion);
        var version = await RequireUpdatedVersionAsync(scope, update, tenant, periziaId, ct);
        await AppendHistoryAsync(scope, tenant, periziaId, version,
            expectedSinistroId, null, audit, ct);
        await scope.CommitAsync(ct);
        return version;
    }

    public async Task<PeriziaSinistroState> ReadAssociationAsync(
        TenantId tenant, Guid periziaId, CancellationToken ct = default)
    {
        if (periziaId == Guid.Empty)
            throw new ArgumentException("Perizia id required.", nameof(periziaId));
        await using var scope = await TenantDbScope.OpenAsync(source, tenant, ct);
        await using var command = scope.Command(
            "SELECT sinistro_id, sinistro_link_version " +
            "FROM perizie WHERE tenant_id=@tenant AND id=@perizia");
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("perizia", periziaId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new KeyNotFoundException("Perizia not found.");
        var value = new PeriziaSinistroState(
            reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetInt64(1));
        await reader.DisposeAsync();
        await scope.CommitAsync(ct);
        return value;
    }

    // Legacy/read-only seam for existing projections and tests. Mutations
    // intentionally do not have an unversioned equivalent.
    public async Task<Guid?> FindSinistroIdForPeriziaAsync(
        TenantId tenant, Guid periziaId, CancellationToken ct = default) =>
        (await ReadAssociationAsync(tenant, periziaId, ct)).SinistroId;

    private static void ValidateVersion(long version)
    {
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
    }

    private static void ValidateAudit(
        TenantId tenant, Guid periziaId, SecurityAuditEvent audit, string action)
    {
        if (audit.TenantId != tenant)
            throw new UnauthorizedAccessException("Cross-tenant relation audit denied.");
        if (audit.ResourceId != periziaId ||
            audit.Action != action || audit.Outcome != "succeeded")
            throw new ArgumentException("Audit must describe this successful change.", nameof(audit));
    }

    private static async Task RequireVisibleTargetAsync(
        TenantDbScope scope, TenantId tenant, Guid sinistroId, CancellationToken ct)
    {
        await using var exists = scope.Command(
            "SELECT 1 FROM sinistri WHERE tenant_id=@tenant AND id=@sinistro");
        exists.Parameters.AddWithValue("tenant", tenant.Value);
        exists.Parameters.AddWithValue("sinistro", sinistroId);
        if (await exists.ExecuteScalarAsync(ct) is null)
            throw new KeyNotFoundException("Resource not found.");
    }

    private static async Task<long> RequireUpdatedVersionAsync(
        TenantDbScope scope, NpgsqlCommand update, TenantId tenant, Guid periziaId,
        CancellationToken ct)
    {
        object? outcome;
        try
        {
            outcome = await update.ExecuteScalarAsync(ct);
        }
        catch (PostgresException ex) when (
            ex.SqlState == PostgresErrorCodes.ForeignKeyViolation &&
            ex.ConstraintName == "fk_perizie_sinistro_same_tenant")
        {
            throw new KeyNotFoundException("Resource not found.", ex);
        }
        if (outcome is long next) return next;
        // Do not report a foreign perizia's existence even if its ID is known.
        await using var check = scope.Command(
            "SELECT 1 FROM perizie WHERE tenant_id=@tenant AND id=@perizia");
        check.Parameters.AddWithValue("tenant", tenant.Value);
        check.Parameters.AddWithValue("perizia", periziaId);
        if (await check.ExecuteScalarAsync(ct) is null)
            throw new KeyNotFoundException("Resource not found.");
        throw new PeriziaConcurrencyConflictException();
    }

    private static async Task AppendHistoryAsync(
        TenantDbScope scope, TenantId tenant, Guid periziaId, long revision,
        Guid? previous, Guid? next, SecurityAuditEvent audit, CancellationToken ct)
    {
        // Foreign key to audit proves the provenance row is linked to the
        // exact successful append-only security event in this transaction.
        await PgSecurityAuditTrail.AppendWithinScopeAsync(scope, audit, ct);
        await using var cmd = scope.Command(
            "INSERT INTO perizia_sinistro_history " +
            "(tenant_id,perizia_id,revision,previous_sinistro_id,new_sinistro_id,audit_id) " +
            "VALUES (@tenant,@perizia,@revision,@previous,@next,@audit)");
        cmd.Parameters.AddWithValue("tenant", tenant.Value);
        cmd.Parameters.AddWithValue("perizia", periziaId);
        cmd.Parameters.AddWithValue("revision", revision);
        cmd.Parameters.Add(new NpgsqlParameter("previous", NpgsqlDbType.Uuid)
            { Value = (object?)previous ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("next", NpgsqlDbType.Uuid)
            { Value = (object?)next ?? DBNull.Value });
        cmd.Parameters.AddWithValue("audit", audit.Id);
        await cmd.ExecuteNonQueryAsync(ct);
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
