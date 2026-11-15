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
        await using (var cmd = scope.Command(
            "INSERT INTO perizie (tenant_id,id,reference,status,assigned_to,created_at_utc) VALUES (@tenant,@id,@reference,@status,@assignee,@created)"))
        {
            Fill(cmd, item);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await scope.CommitAsync(ct);
    }

    public async Task UpdateAsync(Perizia item, CancellationToken ct = default)
    {
        await using var scope = await TenantDbScope.OpenAsync(dataSource, item.TenantId, ct);
        await using (var cmd = scope.Command(
            "UPDATE perizie SET status=@status, assigned_to=@assignee WHERE tenant_id=@tenant AND id=@id"))
        {
            Fill(cmd, item);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1)
                throw new KeyNotFoundException("Perizia not found.");
        }
        await scope.CommitAsync(ct);
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
