using Peritial.Business;
using Peritial.Domains;

namespace Peritial.Infrastructures;

/// <summary>
/// Explicit projection: never SELECT * or retrieve storage_key. The signed
/// tenant is established per transaction and FORCE RLS enforces it again.
/// </summary>
public sealed class PgDocumentCatalogRepository(Npgsql.NpgsqlDataSource dataSource)
    : IDocumentCatalogRepository
{
    public async Task<IReadOnlyList<DocumentSummaryDto>> ListAsync(
        TenantId tenant, Guid? periziaId, int limit, CancellationToken ct = default)
    {
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        const string columns = "id, perizia_id, file_name, content_type, length_bytes, created_at_utc";
        var sql = periziaId.HasValue
            ? $"SELECT {columns} FROM documents WHERE tenant_id=@tenant AND perizia_id=@perizia " +
              "ORDER BY created_at_utc DESC, id DESC LIMIT @limit"
            : $"SELECT {columns} FROM documents WHERE tenant_id=@tenant " +
              "ORDER BY created_at_utc DESC, id DESC LIMIT @limit";
        var result = new List<DocumentSummaryDto>();
        await using (var cmd = scope.Command(sql))
        {
            cmd.Parameters.AddWithValue("tenant", tenant.Value);
            if (periziaId.HasValue) cmd.Parameters.AddWithValue("perizia", periziaId.Value);
            cmd.Parameters.AddWithValue("limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        }
        await scope.CommitAsync(ct);
        return result;
    }

    public async Task<DocumentSummaryDto?> FindAsync(
        TenantId tenant, Guid id, CancellationToken ct = default)
    {
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        DocumentSummaryDto? result = null;
        await using (var cmd = scope.Command(
            "SELECT id, perizia_id, file_name, content_type, length_bytes, created_at_utc " +
            "FROM documents WHERE tenant_id=@tenant AND id=@id"))
        {
            cmd.Parameters.AddWithValue("tenant", tenant.Value);
            cmd.Parameters.AddWithValue("id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) result = Read(reader);
        }
        await scope.CommitAsync(ct);
        return result;
    }

    private static DocumentSummaryDto Read(Npgsql.NpgsqlDataReader row) => new(
        row.GetGuid(0),
        row.IsDBNull(1) ? null : row.GetGuid(1),
        row.GetString(2),
        row.GetString(3),
        row.GetInt64(4),
        row.GetDateTime(5));
}
