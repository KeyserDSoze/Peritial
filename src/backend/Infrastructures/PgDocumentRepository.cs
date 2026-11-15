using Npgsql;
using Peritial.Business;

namespace Peritial.Infrastructures;

public sealed class PgDocumentRepository(NpgsqlDataSource dataSource) : IDocumentRepository
{
    // Legacy test-only insertion seam: production ingestion uses AddAuditedAsync.
    public async Task AddAsync(DocumentRecord item, CancellationToken ct = default)
    {
        await using var scope = await TenantDbScope.OpenAsync(dataSource, item.TenantId, ct);
        await InsertAsync(scope, item, ct);
        await scope.CommitAsync(ct);
    }

    public async Task AddAuditedAsync(DocumentRecord item, SecurityAuditEvent audit, CancellationToken ct = default)
    {
        if (audit.TenantId != item.TenantId)
            throw new UnauthorizedAccessException("Cross-tenant document audit denied.");
        if (audit.ResourceId != item.Id || audit.Action != "document.ingest" || audit.Outcome != "succeeded")
            throw new ArgumentException("Audit must describe this successful document ingestion.", nameof(audit));

        await using var scope = await TenantDbScope.OpenAsync(dataSource, item.TenantId, ct);
        // Arbitrates with SELECT ... FOR UPDATE by the reconciler. If cleanup
        // won, the intent is gone and no metadata/audit success may commit.
        await using (var take = scope.Command(
            "DELETE FROM upload_intents WHERE tenant_id=@tenant AND document_id=@id AND storage_key=@key"))
        {
            take.Parameters.AddWithValue("tenant", item.TenantId.Value);
            take.Parameters.AddWithValue("id", item.Id);
            take.Parameters.AddWithValue("key", item.StorageKey);
            if (await take.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Document upload intent missing or already recovered.");
        }
        await InsertAsync(scope, item, ct);
        await PgSecurityAuditTrail.AppendWithinScopeAsync(scope, audit, ct);
        await scope.CommitAsync(ct);
    }

    private static async Task InsertAsync(TenantDbScope scope, DocumentRecord item, CancellationToken ct)
    {
        await using var cmd = scope.Command(
            "INSERT INTO documents (tenant_id,id,perizia_id,file_name,content_type,length_bytes,storage_key,created_at_utc) " +
            "VALUES (@tenant,@id,@perizia,@name,@type,@length,@key,@created)");
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
}
