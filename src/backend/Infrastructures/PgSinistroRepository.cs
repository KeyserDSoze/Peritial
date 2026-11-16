using Npgsql;
using NpgsqlTypes;
using Peritial.Business;
using Peritial.Domains;

namespace Peritial.Infrastructures;

/// <summary>
/// Tenant-filtered sinistro catalog and audited writes. The tenant is always taken
/// from a validated domain identity, and every operation uses transaction-local
/// RLS context. Called only through the scope-aware Business service (#7).
/// </summary>
public sealed class PgSinistroRepository(NpgsqlDataSource dataSource) : ISinistroRepository
{
    public async Task<IReadOnlyList<Sinistro>> ListAsync(
        TenantId tenant, int limit = 50, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        var items = new List<Sinistro>();
        await using (var command = scope.Command(
            "SELECT id, tenant_id, external_reference, occurred_on, reported_on, " +
            "created_at_utc, version FROM sinistri WHERE tenant_id=@tenant " +
            "ORDER BY occurred_on DESC, id LIMIT @limit"))
        {
            command.Parameters.AddWithValue("tenant", tenant.Value);
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add(Read(reader));
        }
        await scope.CommitAsync(ct);
        return items;
    }

    /// <summary>
    /// Prefix matching happens in PostgreSQL before ORDER/LIMIT; the pattern
    /// treats user-supplied percent, underscore and ! as literal characters.
    /// A separate explicit tenant predicate complements FORCE RLS.
    /// </summary>
    public async Task<IReadOnlyList<Sinistro>> SearchAsync(
        TenantId tenant, string prefix, DateOnly? occurredFrom = null,
        DateOnly? occurredTo = null, int limit = 10, CancellationToken ct = default)
    {
        var filter = SinistroSearchFilter.Create(prefix, occurredFrom, occurredTo, limit);
        // '!' is the SQL LIKE escape to avoid backslash/standard_conforming_strings
        // assumptions. Escape the escape first, then the two pattern metacharacters.
        var literalPrefix = filter.Prefix.Replace("!", "!!")
            .Replace("%", "!%").Replace("_", "!_") + "%";
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        var items = new List<Sinistro>();
        await using (var command = scope.Command(
            "SELECT id, tenant_id, external_reference, occurred_on, reported_on, " +
            "created_at_utc, version FROM sinistri " +
            "WHERE tenant_id=@tenant AND external_reference ILIKE @pattern ESCAPE '!' " +
            "AND (@dateFrom IS NULL OR occurred_on >= @dateFrom) " +
            "AND (@dateTo IS NULL OR occurred_on <= @dateTo) " +
            "ORDER BY occurred_on DESC, id LIMIT @limit"))
        {
            command.Parameters.AddWithValue("tenant", tenant.Value);
            command.Parameters.AddWithValue("pattern", literalPrefix);
            command.Parameters.Add("dateFrom", NpgsqlDbType.Date).Value =
                (object?)filter.OccurredFrom ?? DBNull.Value;
            command.Parameters.Add("dateTo", NpgsqlDbType.Date).Value =
                (object?)filter.OccurredTo ?? DBNull.Value;
            command.Parameters.AddWithValue("limit", filter.Limit);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add(Read(reader));
        }
        await scope.CommitAsync(ct);
        return items;
    }

    public async Task<Sinistro?> FindAsync(TenantId tenant, Guid id, CancellationToken ct = default)
    {
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        Sinistro? item = null;
        await using (var command = scope.Command(
            "SELECT id, tenant_id, external_reference, occurred_on, reported_on, " +
            "created_at_utc, version FROM sinistri WHERE tenant_id=@tenant AND id=@id"))
        {
            command.Parameters.AddWithValue("tenant", tenant.Value);
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) item = Read(reader);
        }
        await scope.CommitAsync(ct);
        return item;
    }

    /// <summary>Insert both the case and its successful audit in one PG commit.</summary>
    public async Task AddAuditedAsync(
        Sinistro item, SecurityAuditEvent audit, CancellationToken ct = default)
    {
        ValidateAudit(item, audit, "sinistro.create");
        await using var scope = await TenantDbScope.OpenAsync(dataSource, item.TenantId, ct);
        await using (var command = scope.Command(
            "INSERT INTO sinistri (tenant_id,id,external_reference,occurred_on," +
            "reported_on,created_at_utc,version) " +
            "VALUES (@tenant,@id,@reference,@occurred,@reported,@created,@version)"))
        {
            Fill(command, item);
            await command.ExecuteNonQueryAsync(ct);
        }
        await PgSecurityAuditTrail.AppendWithinScopeAsync(scope, audit, ct);
        await scope.CommitAsync(ct);
    }

    /// <summary>
    /// A stale snapshot never overwrites a newer row or appends success audit.
    /// The expected version changes only after the audit and update both commit.
    /// </summary>
    public async Task UpdateAuditedAsync(
        Sinistro item, SecurityAuditEvent audit, CancellationToken ct = default)
    {
        ValidateAudit(item, audit, "sinistro.update");
        await using var scope = await TenantDbScope.OpenAsync(dataSource, item.TenantId, ct);
        await using (var command = scope.Command(
            "UPDATE sinistri SET external_reference=@reference, occurred_on=@occurred, " +
            "reported_on=@reported, version=version+1 " +
            "WHERE tenant_id=@tenant AND id=@id AND version=@version"))
        {
            Fill(command, item);
            if (await command.ExecuteNonQueryAsync(ct) != 1)
            {
                // A foreign ID is deliberately indistinguishable from missing.
                await using var existing = scope.Command(
                    "SELECT 1 FROM sinistri WHERE tenant_id=@tenant AND id=@id");
                existing.Parameters.AddWithValue("tenant", item.TenantId.Value);
                existing.Parameters.AddWithValue("id", item.Id);
                if (await existing.ExecuteScalarAsync(ct) is null)
                    throw new KeyNotFoundException("Sinistro not found.");
                throw new SinistroConcurrencyConflictException();
            }
        }
        await PgSecurityAuditTrail.AppendWithinScopeAsync(scope, audit, ct);
        await scope.CommitAsync(ct);
        item.ConfirmPersistedUpdate();
    }

    private static void ValidateAudit(Sinistro item, SecurityAuditEvent audit, string action)
    {
        if (item.TenantId != audit.TenantId)
            throw new UnauthorizedAccessException("Cross-tenant sinistro audit denied.");
        if (audit.ResourceId != item.Id || audit.Action != action || audit.Outcome != "succeeded")
            throw new ArgumentException("Audit must describe this successful sinistro mutation.", nameof(audit));
    }

    private static Sinistro Read(NpgsqlDataReader reader) => Sinistro.Restore(
        reader.GetGuid(0), TenantId.From(reader.GetGuid(1)),
        reader.GetString(2), reader.GetFieldValue<DateOnly>(3),
        reader.IsDBNull(4) ? null : reader.GetFieldValue<DateOnly>(4),
        reader.GetDateTime(5), reader.GetInt64(6));

    private static void Fill(NpgsqlCommand command, Sinistro item)
    {
        command.Parameters.AddWithValue("tenant", item.TenantId.Value);
        command.Parameters.AddWithValue("id", item.Id);
        command.Parameters.AddWithValue("reference", item.ExternalReference);
        command.Parameters.AddWithValue("occurred", NpgsqlDbType.Date, item.OccurredOn);
        command.Parameters.Add("reported", NpgsqlDbType.Date).Value =
            (object?)item.ReportedOn ?? DBNull.Value;
        command.Parameters.AddWithValue("created", item.CreatedAtUtc);
        command.Parameters.AddWithValue("version", item.PersistedVersion);
    }
}

