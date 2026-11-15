using Npgsql;
using Peritial.Business;

namespace Peritial.Infrastructures;

/// <summary>Append-only RLS-backed audit writer; application role has INSERT only.</summary>
public sealed class PgSecurityAuditTrail(NpgsqlDataSource dataSource) : ISecurityAuditTrail
{
    public async Task AppendAsync(SecurityAuditEvent entry, CancellationToken ct = default)
    {
        await using var scope = await TenantDbScope.OpenAsync(dataSource, entry.TenantId, ct);
        await AppendWithinScopeAsync(scope, entry, ct);
        await scope.CommitAsync(ct);
    }

    // Called by transactional repositories after business SQL but before COMMIT.
    // The audit failure aborts the entire transaction and cannot silently succeed.
    internal static async Task AppendWithinScopeAsync(
        TenantDbScope scope, SecurityAuditEvent entry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.Subject) || entry.Subject.Length > 256)
            throw new ArgumentException("Invalid audit subject.", nameof(entry));
        if (string.IsNullOrEmpty(entry.Action) || entry.Action.Length > 64 ||
            entry.Outcome is not ("succeeded" or "denied"))
            throw new ArgumentException("Invalid audit action or outcome.", nameof(entry));

        await using var cmd = scope.Command(
            "INSERT INTO security_audit (tenant_id,id,subject,action,outcome,resource_id,occurred_at_utc) " +
            "VALUES (@tenant,@id,@subject,@action,@outcome,@resource,@occurred)");
        cmd.Parameters.AddWithValue("tenant", entry.TenantId.Value);
        cmd.Parameters.AddWithValue("id", entry.Id);
        cmd.Parameters.AddWithValue("subject", entry.Subject);
        cmd.Parameters.AddWithValue("action", entry.Action);
        cmd.Parameters.AddWithValue("outcome", entry.Outcome);
        cmd.Parameters.AddWithValue("resource", (object?)entry.ResourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("occurred", entry.OccurredAtUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
