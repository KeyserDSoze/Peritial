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
    /// Seek after (date DESC, UUID ASC), with an extra row for has-more.
    /// Explicit tenant and transaction-local FORCE RLS apply to every page.
    /// </summary>
    public async Task<IReadOnlyList<Sinistro>> PageAsync(
        TenantId tenant, SinistroPageBoundary? after, int fetchLimit,
        CancellationToken ct = default)
    {
        if (fetchLimit is < 1 or > 51)
            throw new ArgumentOutOfRangeException(nameof(fetchLimit));
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        var items = new List<Sinistro>();
        await using (var command = scope.Command(
            "SELECT id, tenant_id, external_reference, occurred_on, reported_on, " +
            "created_at_utc, version FROM sinistri WHERE tenant_id=@tenant " +
            "AND (@afterDate IS NULL OR occurred_on < @afterDate " +
            "OR (occurred_on = @afterDate AND id > @afterId)) " +
            "ORDER BY occurred_on DESC, id ASC LIMIT @limit"))
        {
            command.Parameters.AddWithValue("tenant", tenant.Value);
            command.Parameters.Add("afterDate", NpgsqlDbType.Date).Value =
                (object?)after?.OccurredOn ?? DBNull.Value;
            command.Parameters.Add("afterId", NpgsqlDbType.Uuid).Value =
                (object?)after?.Id ?? DBNull.Value;
            command.Parameters.AddWithValue("limit", fetchLimit);
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

    /// <summary>
    /// Filtered keyset seek; literal ILIKE, inclusive dates and the cursor
    /// predicate are all evaluated server-side before ORDER/LIMIT.
    /// </summary>
    public async Task<IReadOnlyList<Sinistro>> SearchPageAsync(
        TenantId tenant, SinistroSearchFilter filter, SinistroPageBoundary? after,
        int fetchLimit, CancellationToken ct = default)
    {
        if (fetchLimit is < 1 or > 51)
            throw new ArgumentOutOfRangeException(nameof(fetchLimit));
        var validated = SinistroSearchFilter.Create(
            filter.Prefix, filter.OccurredFrom, filter.OccurredTo, filter.Limit);
        var literalPrefix = validated.Prefix.Replace("!", "!!")
            .Replace("%", "!%").Replace("_", "!_") + "%";
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        var items = new List<Sinistro>();
        await using (var command = scope.Command(
            "SELECT id, tenant_id, external_reference, occurred_on, reported_on, " +
            "created_at_utc, version FROM sinistri " +
            "WHERE tenant_id=@tenant AND external_reference ILIKE @pattern ESCAPE '!' " +
            "AND (@dateFrom IS NULL OR occurred_on >= @dateFrom) " +
            "AND (@dateTo IS NULL OR occurred_on <= @dateTo) " +
            "AND (@afterDate IS NULL OR occurred_on < @afterDate " +
            "OR (occurred_on = @afterDate AND id > @afterId)) " +
            "ORDER BY occurred_on DESC, id ASC LIMIT @limit"))
        {
            command.Parameters.AddWithValue("tenant", tenant.Value);
            command.Parameters.AddWithValue("pattern", literalPrefix);
            command.Parameters.Add("dateFrom", NpgsqlDbType.Date).Value =
                (object?)validated.OccurredFrom ?? DBNull.Value;
            command.Parameters.Add("dateTo", NpgsqlDbType.Date).Value =
                (object?)validated.OccurredTo ?? DBNull.Value;
            command.Parameters.Add("afterDate", NpgsqlDbType.Date).Value =
                (object?)after?.OccurredOn ?? DBNull.Value;
            command.Parameters.Add("afterId", NpgsqlDbType.Uuid).Value =
                (object?)after?.Id ?? DBNull.Value;
            command.Parameters.AddWithValue("limit", fetchLimit);
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
            try { await command.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (IsImportedReferenceGuard(ex))
            {
                throw new LegacyXmlReferenceConflictException();
            }
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
            int affected;
            try { affected = await command.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (IsImportedReferenceGuard(ex))
            {
                throw new LegacyXmlReferenceConflictException();
            }
            if (affected != 1)
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

    /// <summary>
    /// SQL CAS delete + successful audit in a single PG transaction. V004
    /// RESTRICT FK prevents concurrent Perizia links from being orphaned.
    /// </summary>
    public async Task DeleteAuditedAsync(
        TenantId tenant, Guid id, long expectedVersion, SecurityAuditEvent audit,
        CancellationToken ct = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Invalid sinistro ID.", nameof(id));
        if (expectedVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        if (audit.TenantId != tenant)
            throw new UnauthorizedAccessException("Cross-tenant sinistro audit denied.");
        if (audit.ResourceId != id || audit.Action != "sinistro.delete" ||
            audit.Outcome != "succeeded")
            throw new ArgumentException("Audit must describe this successful delete.", nameof(audit));

        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        await using (var command = scope.Command(
            "DELETE FROM sinistri WHERE tenant_id=@tenant AND id=@id AND version=@version"))
        {
            command.Parameters.AddWithValue("tenant", tenant.Value);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("version", expectedVersion);
            int affected;
            try
            {
                affected = await command.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (
                ex.SqlState == PostgresErrorCodes.ForeignKeyViolation &&
                ex.ConstraintName == "fk_perizie_sinistro_same_tenant")
            {
                // The database's foreign key, not a racy SELECT count,
                // is the authoritative protection against linked deletion.
                throw new SinistroLinkedPerizieException();
            }
            if (affected != 1)
            {
                await using var check = scope.Command(
                    "SELECT 1 FROM sinistri WHERE tenant_id=@tenant AND id=@id");
                check.Parameters.AddWithValue("tenant", tenant.Value);
                check.Parameters.AddWithValue("id", id);
                if (await check.ExecuteScalarAsync(ct) is null)
                    throw new KeyNotFoundException("Sinistro not found.");
                throw new SinistroConcurrencyConflictException();
            }
        }
        await PgSecurityAuditTrail.AppendWithinScopeAsync(scope, audit, ct);
        await scope.CommitAsync(ct);
    }

    // A V012 SQL trigger protects imported refs even against app-role raw SQL.
    // Keep API conflict semantics stable without leaking an existing case ID.
    private static bool IsImportedReferenceGuard(PostgresException ex) =>
        ex.SqlState == PostgresErrorCodes.UniqueViolation &&
        ex.ConstraintName == "ck_imported_sinistro_reference_guard";

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

