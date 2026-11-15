using Npgsql;
using Peritial.Business;

namespace Peritial.Infrastructures;

/// <summary>Append-only RLS-backed audit writer; application role has INSERT only.</summary>
public sealed class PgSecurityAuditTrail(NpgsqlDataSource dataSource) : ISecurityAuditTrail
{
    public async Task AppendAsync(SecurityAuditEvent entry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.Subject) || entry.Subject.Length > 256)
            throw new ArgumentException("Invalid audit subject.", nameof(entry));
        if (entry.Action.Length is 0 or > 64 || entry.Outcome is not ("succeeded" or "denied"))
            throw new ArgumentException("Invalid audit action or outcome.", nameof(entry));

        await using var scope = await TenantDbScope.OpenAsync(dataSource, entry.TenantId, ct);
        await using (var command = scope.Command(
            "INSERT INTO security_audit (tenant_id,id,subject,action,outcome,resource_id,occurred_at_utc) " +
            "VALUES (@tenant,@id,@subject,@action,@outcome,@resource,@occurred)"))
        {
            command.Parameters.AddWithValue("tenant", entry.TenantId.Value);
            command.Parameters.AddWithValue("id", entry.Id);
            command.Parameters.AddWithValue("subject", entry.Subject);
            command.Parameters.AddWithValue("action", entry.Action);
            command.Parameters.AddWithValue("outcome", entry.Outcome);
            command.Parameters.AddWithValue("resource", (object?)entry.ResourceId ?? DBNull.Value);
            command.Parameters.AddWithValue("occurred", entry.OccurredAtUtc);
            await command.ExecuteNonQueryAsync(ct);
        }
        await scope.CommitAsync(ct);
    }
}
