using Npgsql;
using Peritial.Business;

namespace Peritial.Infrastructures;

/// <summary>
/// Reconcile one stale upload under a database row lock. Metadata writers must
/// consume the SAME intent row during their commit, so a worker cannot delete
/// a blob and then allow a new committed document to reference that blob.
/// </summary>
public sealed class PgUploadReconciler(NpgsqlDataSource recoverySource, IObjectStore objects)
{
    public async Task<bool> ReconcileOneAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        if (olderThanUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Recovery cutoff must be UTC.", nameof(olderThanUtc));

        await using var conn = await recoverySource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid tenant;
        Guid id;
        string key;
        await using (var pick = new NpgsqlCommand(
            "SELECT tenant_id, document_id, storage_key FROM upload_intents " +
            "WHERE created_at_utc < @cutoff ORDER BY created_at_utc LIMIT 1 FOR UPDATE SKIP LOCKED",
            conn, tx))
        {
            pick.Parameters.AddWithValue("cutoff", olderThanUtc);
            await using var reader = await pick.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return false;
            tenant = reader.GetGuid(0);
            id = reader.GetGuid(1);
            key = reader.GetString(2);
        }

        bool referenced;
        await using (var check = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM documents WHERE tenant_id=@tenant AND storage_key=@key)",
            conn, tx))
        {
            check.Parameters.AddWithValue("tenant", tenant);
            check.Parameters.AddWithValue("key", key);
            referenced = (bool)(await check.ExecuteScalarAsync(ct))!;
        }

        if (!referenced)
        {
            // Object-store delete is idempotent. If the call fails or the worker
            // is killed, the PostgreSQL transaction rolls back for safe retry.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await objects.DeleteAsync(key, timeout.Token);
        }

        // If the upload was not committed, release its idempotency key so a
        // later retry can start afresh. If metadata exists (legacy/lost ACK),
        // repair the completed claim while preserving the original blob.
        var requestSql = referenced
            ? "UPDATE upload_request_keys SET status='completed',completed_at_utc=now() " +
              "WHERE tenant_id=@tenant AND document_id=@id AND status='pending'"
            : "DELETE FROM upload_request_keys " +
              "WHERE tenant_id=@tenant AND document_id=@id AND status='pending'";
        await using (var request = new NpgsqlCommand(requestSql, conn, tx))
        {
            request.Parameters.AddWithValue("tenant", tenant);
            request.Parameters.AddWithValue("id", id);
            await request.ExecuteNonQueryAsync(ct);
        }

        await using (var done = new NpgsqlCommand(
            "DELETE FROM upload_intents WHERE tenant_id=@tenant AND document_id=@id",
            conn, tx))
        {
            done.Parameters.AddWithValue("tenant", tenant);
            done.Parameters.AddWithValue("id", id);
            if (await done.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Upload recovery intent changed unexpectedly.");
        }
        await tx.CommitAsync(ct);
        return true;
    }
}
