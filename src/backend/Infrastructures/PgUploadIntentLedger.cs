using Npgsql;
using Peritial.Business;
using Peritial.Domains;

namespace Peritial.Infrastructures;

/// <summary>Tenant-scoped durable intent, committed before object-store PUT.</summary>
public sealed class PgUploadIntentLedger(NpgsqlDataSource dataSource) : IUploadIntentLedger
{
    public async Task RecordAsync(TenantId tenant, Guid id, string key, CancellationToken ct = default)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(key) || key.Length > 512)
            throw new ArgumentException("Invalid upload intent.");
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        await using (var cmd = scope.Command(
            "INSERT INTO upload_intents (tenant_id,document_id,storage_key,created_at_utc) " +
            "VALUES (@tenant,@id,@key,now())"))
        {
            cmd.Parameters.AddWithValue("tenant", tenant.Value);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("key", key);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await scope.CommitAsync(ct);
    }

    public async Task ForgetAsync(TenantId tenant, Guid id, CancellationToken ct = default)
    {
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        await using (var cmd = scope.Command(
            "DELETE FROM upload_intents WHERE tenant_id=@tenant AND document_id=@id"))
        {
            cmd.Parameters.AddWithValue("tenant", tenant.Value);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await scope.CommitAsync(ct);
    }
}
