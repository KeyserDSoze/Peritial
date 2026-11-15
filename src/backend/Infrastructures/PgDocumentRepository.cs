using Npgsql;
using Peritial.Business;

namespace Peritial.Infrastructures;

public sealed class PgDocumentRepository(NpgsqlDataSource dataSource) : IDocumentRepository
{
    public async Task AddAsync(DocumentRecord item, CancellationToken ct = default)
    {
        await using var scope = await TenantDbScope.OpenAsync(dataSource, item.TenantId, ct);
        await using (var cmd = scope.Command(
            "INSERT INTO documents (tenant_id,id,perizia_id,file_name,content_type,length_bytes,storage_key,created_at_utc) " +
            "VALUES (@tenant,@id,@perizia,@name,@type,@length,@key,@created)"))
        {
            cmd.Parameters.AddWithValue("tenant", item.TenantId.Value);
            cmd.Parameters.AddWithValue("id", item.Id);
            cmd.Parameters.AddWithValue("perizia", (object?)item.PeriziaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("name", item.FileName);
            cmd.Parameters.AddWithValue("type", item.ContentType);
            cmd.Parameters.AddWithValue("length", item.Length);
            cmd.Parameters.AddWithValue("key", item.StorageKey);
            cmd.Parameters.AddWithValue("created", item.CreatedAtUtc);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await scope.CommitAsync(ct);
    }
}
