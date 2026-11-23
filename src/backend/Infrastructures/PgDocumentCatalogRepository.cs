using Peritial.Business;
using Peritial.Domains;

namespace Peritial.Infrastructures;

/// <summary>
/// Public list/get/page projections never retrieve storage_key. A separate
/// download-only method returns the key to a private Business service after
/// tenant-scoped SQL under FORCE RLS; it is never serialized to a client.
/// </summary>
public sealed class PgDocumentCatalogRepository(Npgsql.NpgsqlDataSource dataSource)
    : IDocumentCatalogRepository, IDocumentDownloadRepository
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

    public async Task<IReadOnlyList<DocumentSummaryDto>> PageAsync(
        TenantId tenant, Guid? periziaId, DocumentPageBoundary? after,
        int fetchLimit, CancellationToken ct = default)
    {
        if (fetchLimit is < 1 or > 51)
            throw new ArgumentOutOfRangeException(nameof(fetchLimit));
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        const string columns =
            "id, perizia_id, file_name, content_type, length_bytes, created_at_utc";
        var filter = periziaId.HasValue ? " AND perizia_id=@perizia" : "";
        // Avoid nullable-parameter OR predicates: the tuple seek is directly
        // usable as an ordered B-tree range on (created_at_utc DESC, id DESC).
        var seek = after is null ? "" :
            " AND (created_at_utc, id) < (@afterTime, @afterId)";
        var query = $"SELECT {columns} FROM documents WHERE tenant_id=@tenant" +
            filter + seek + " ORDER BY created_at_utc DESC, id DESC LIMIT @limit";
        var items = new List<DocumentSummaryDto>();
        await using (var cmd = scope.Command(query))
        {
            cmd.Parameters.AddWithValue("tenant", tenant.Value);
            if (periziaId.HasValue) cmd.Parameters.AddWithValue("perizia", periziaId.Value);
            if (after is not null)
            {
                cmd.Parameters.AddWithValue("afterTime", after.CreatedAtUtc);
                cmd.Parameters.AddWithValue("afterId", after.Id);
            }
            cmd.Parameters.AddWithValue("limit", fetchLimit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add(Read(reader));
        }
        await scope.CommitAsync(ct);
        return items;
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

    /// <summary>
    /// The only read of storage_key for a download. All rows are resolved by
    /// tenant and document UUID inside a transaction using FORCE RLS.
    /// Do not add this field to the public metadata projection.
    /// </summary>
    public async Task<DocumentDownloadLocation?> FindForDownloadAsync(
        TenantId tenant, Guid documentId, CancellationToken ct = default)
    {
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        DocumentDownloadLocation? found = null;
        await using (var cmd = scope.Command(
            "SELECT id, perizia_id, file_name, content_type, length_bytes, created_at_utc, storage_key, content_sha256 " +
            "FROM documents WHERE tenant_id=@tenant AND id=@id"))
        {
            cmd.Parameters.AddWithValue("tenant", tenant.Value);
            cmd.Parameters.AddWithValue("id", documentId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                found = new DocumentDownloadLocation(Read(reader), reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetFieldValue<byte[]>(7));
        }
        await scope.CommitAsync(ct);
        return found;
    }

    private static DocumentSummaryDto Read(Npgsql.NpgsqlDataReader row) => new(
        row.GetGuid(0),
        row.IsDBNull(1) ? null : row.GetGuid(1),
        row.GetString(2),
        row.GetString(3),
        row.GetInt64(4),
        row.GetDateTime(5));
}
