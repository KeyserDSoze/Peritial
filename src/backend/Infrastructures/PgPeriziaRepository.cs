using Npgsql;
using Peritial.Business;
using Peritial.Domains;

namespace Peritial.Infrastructures;

public sealed class PgPeriziaRepository(NpgsqlDataSource dataSource) : IPeriziaRepository
{
    public async Task<IReadOnlyList<Perizia>> ListAsync(TenantId tenant, CancellationToken ct = default)
    {
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        var result = new List<Perizia>();
        await using (var cmd = scope.Command(
            "SELECT id, tenant_id, reference, status, assigned_to, created_at_utc FROM perizie WHERE tenant_id = @tenant ORDER BY created_at_utc DESC"))
        {
            cmd.Parameters.AddWithValue("tenant", tenant.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        }
        await scope.CommitAsync(ct);
        return result;
    }

    public async Task<Perizia?> FindAsync(TenantId tenant, Guid id, CancellationToken ct = default)
    {
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        Perizia? result = null;
        await using (var cmd = scope.Command(
            "SELECT id, tenant_id, reference, status, assigned_to, created_at_utc FROM perizie WHERE tenant_id = @tenant AND id = @id"))
        {
            cmd.Parameters.AddWithValue("tenant", tenant.Value);
            cmd.Parameters.AddWithValue("id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) result = Read(reader);
        }
        await scope.CommitAsync(ct);
        return result;
    }

    public async Task AddAsync(Perizia item, CancellationToken ct = default)
    {
        await using var scope = await TenantDbScope.OpenAsync(dataSource, item.TenantId, ct);
        await InsertAsync(scope, item, ct);
        await scope.CommitAsync(ct);
        item.ConfirmPersisted();
    }

    public async Task UpdateAsync(Perizia item, CancellationToken ct = default)
    {
        await using var scope = await TenantDbScope.OpenAsync(dataSource, item.TenantId, ct);
        await UpdateRowAsync(scope, item, ct);
        await scope.CommitAsync(ct);
        item.ConfirmPersisted();
    }

    public async Task AddAuditedAsync(Perizia item, SecurityAuditEvent audit, CancellationToken ct = default)
    {
        CheckAudit(item, audit, "perizia.create");
        await using var scope = await TenantDbScope.OpenAsync(dataSource, item.TenantId, ct);
        await InsertAsync(scope, item, ct);
        await PgSecurityAuditTrail.AppendWithinScopeAsync(scope, audit, ct);
        await scope.CommitAsync(ct);
        item.ConfirmPersisted();
    }

    public async Task UpdateAuditedAsync(Perizia item, SecurityAuditEvent audit, CancellationToken ct = default)
    {
        if (audit.Action is not ("perizia.assign" or "perizia.advance" or "perizia.cancel"))
            throw new ArgumentException("Invalid update audit action.", nameof(audit));
        CheckAudit(item, audit, audit.Action);
        CheckTransition(item, audit.Action);
        await using var scope = await TenantDbScope.OpenAsync(dataSource, item.TenantId, ct);
        await UpdateRowAsync(scope, item, ct);
        await PgSecurityAuditTrail.AppendWithinScopeAsync(scope, audit, ct);
        await scope.CommitAsync(ct);
        item.ConfirmPersisted();
    }

    private static void CheckAudit(Perizia item, SecurityAuditEvent audit, string action)
    {
        if (audit.TenantId != item.TenantId)
            throw new UnauthorizedAccessException("Cross-tenant audit operation denied.");
        if (audit.ResourceId != item.Id || audit.Action != action || audit.Outcome != "succeeded")
            throw new ArgumentException("Audit must describe this successful perizia mutation.", nameof(audit));
    }

    private static void CheckTransition(Perizia item, string action)
    {
        var previous = item.PersistedStatus;
        var current = item.Status;
        var valid = action switch
        {
            "perizia.assign" => previous == PeriziaStatus.Registered &&
                               current == PeriziaStatus.Assigned &&
                               !string.IsNullOrWhiteSpace(item.AssignedTo),
            "perizia.advance" => (previous, current) switch
            {
                (PeriziaStatus.Assigned, PeriziaStatus.ToBeRevised) => true,
                (PeriziaStatus.ToBeRevised, PeriziaStatus.Revised) => true,
                (PeriziaStatus.Revised, PeriziaStatus.ToSend) => true,
                (PeriziaStatus.ToSend, PeriziaStatus.Sent) => true,
                (PeriziaStatus.Sent, PeriziaStatus.Billed) => true,
                _ => false
            },
            "perizia.cancel" => current == PeriziaStatus.Cancelled &&
                                previous is not (PeriziaStatus.Cancelled or
                                    PeriziaStatus.Billed or PeriziaStatus.Duplicated),
            _ => false
        };
        if (!valid)
            throw new InvalidOperationException("Invalid perizia state transition.");
    }

    private static async Task InsertAsync(TenantDbScope scope, Perizia item, CancellationToken ct)
    {
        await using var cmd = scope.Command(
            "INSERT INTO perizie (tenant_id,id,reference,status,assigned_to,created_at_utc) " +
            "VALUES (@tenant,@id,@reference,@status,@assignee,@created)");
        Fill(cmd, item);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateRowAsync(
        TenantDbScope scope, Perizia item, CancellationToken ct)
    {
        // Optimistic compare-and-swap under PostgreSQL's row lock. Both
        // status and assignee must still equal the originally read values;
        // PostgreSQL rechecks this predicate after concurrent transactions.
        // A zero-row update MUST NOT append a success audit.
        await using var cmd = scope.Command(
            "UPDATE perizie SET status=@status, assigned_to=@assignee " +
            "WHERE tenant_id=@tenant AND id=@id " +
            "AND status=@expected_status " +
            "AND assigned_to IS NOT DISTINCT FROM @expected_assignee");
        Fill(cmd, item);
        cmd.Parameters.AddWithValue("expected_status", (int)item.PersistedStatus);
        cmd.Parameters.AddWithValue("expected_assignee", (object?)item.PersistedAssignedTo ?? DBNull.Value);
        if (await cmd.ExecuteNonQueryAsync(ct) == 1) return;

        // RLS must still hide foreign tenant records as 404. Distinguish
        // missing/foreign IDs from a stale row within the authenticated tenant.
        await using var existing = scope.Command(
            "SELECT 1 FROM perizie WHERE tenant_id=@tenant AND id=@id");
        existing.Parameters.AddWithValue("tenant", item.TenantId.Value);
        existing.Parameters.AddWithValue("id", item.Id);
        if (await existing.ExecuteScalarAsync(ct) is null)
            throw new KeyNotFoundException("Perizia not found.");
        throw new PeriziaConcurrencyConflictException();
    }

    private static Perizia Read(NpgsqlDataReader reader) => Perizia.Restore(
        reader.GetGuid(0), TenantId.From(reader.GetGuid(1)), reader.GetString(2),
        (PeriziaStatus)reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetDateTime(5));

    private static void Fill(NpgsqlCommand cmd, Perizia item)
    {
        cmd.Parameters.AddWithValue("tenant", item.TenantId.Value);
        cmd.Parameters.AddWithValue("id", item.Id);
        cmd.Parameters.AddWithValue("reference", item.Reference);
        cmd.Parameters.AddWithValue("status", (int)item.Status);
        cmd.Parameters.AddWithValue("assignee", (object?)item.AssignedTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("created", item.CreatedAtUtc);
    }
}
