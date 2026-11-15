using System.Data;
using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;

namespace Peritial.Infrastructures;

/// <summary>
/// Offline integrity checkpoint for DB document references and S3 object bytes.
/// This is NOT an object backup, a cryptographic signature, or an atomic
/// DB/S3 snapshot. Never run against an active application/worker.
/// </summary>
public static class ObjectRecoveryCheckpoint
{
    public sealed record Document(
        Guid TenantId, Guid DocumentId, string StorageKey, long LengthBytes, string Sha256);

    public sealed record Manifest(int FormatVersion, IReadOnlyList<Document> Documents);

    /// <summary>
    /// Read all tenants with an audit/DDL role capable of bypassing FORCE RLS,
    /// reject pending uploads, stream object bytes and capture SHA-256 digests.
    /// </summary>
    public static async Task<Manifest> CaptureAsync(
        NpgsqlDataSource administrator, IAmazonS3 objects, string bucket,
        CancellationToken ct = default)
    {
        var documents = await ReadAllMetadataAsync(administrator, ct);
        var manifest = new List<Document>(documents.Count);
        foreach (var row in documents)
        {
            var (length, digest) = await HashObjectAsync(objects, bucket, row.StorageKey, ct);
            if (length != row.LengthBytes)
                throw new InvalidOperationException("Object checkpoint failed: document length differs from PostgreSQL.");
            manifest.Add(new Document(
                row.TenantId, row.DocumentId, row.StorageKey, row.LengthBytes, digest));
        }
        return new Manifest(1, manifest);
    }

    /// <summary>
    /// Verify that the restored DB contains exactly the captured tenant/id/key/
    /// length references, and that all associated S3 bytes match each digest.
    /// No writes to PostgreSQL or object storage are performed.
    /// </summary>
    public static async Task VerifyAsync(
        NpgsqlDataSource administrator, IAmazonS3 objects, string bucket,
        Manifest checkpoint, CancellationToken ct = default)
    {
        if (checkpoint is null || checkpoint.FormatVersion != 1 || checkpoint.Documents is null)
            throw new InvalidOperationException("Unsupported or malformed checkpoint.");

        var documents = await ReadAllMetadataAsync(administrator, ct);
        if (documents.Count != checkpoint.Documents.Count)
            throw new InvalidOperationException("Restored document metadata count differs from checkpoint.");

        for (var index = 0; index < documents.Count; index++)
        {
            var current = documents[index];
            var saved = checkpoint.Documents[index];
            if (saved is null || current.TenantId != saved.TenantId ||
                current.DocumentId != saved.DocumentId ||
                !string.Equals(current.StorageKey, saved.StorageKey, StringComparison.Ordinal) ||
                current.LengthBytes != saved.LengthBytes ||
                saved.Sha256?.Length != 64 ||
                !saved.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new InvalidOperationException(
                    "Restored document references or checkpoint manifest differ from PostgreSQL.");

            var (length, hash) = await HashObjectAsync(objects, bucket, current.StorageKey, ct);
            if (length != saved.LengthBytes || !string.Equals(hash, saved.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "S3 object integrity mismatch: restored bytes do not match checkpoint.");
        }
    }

    private sealed record Metadata(Guid TenantId, Guid DocumentId, string StorageKey, long LengthBytes);

    private static async Task<List<Metadata>> ReadAllMetadataAsync(
        NpgsqlDataSource administrator, CancellationToken ct)
    {
        await using var conn = await administrator.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        // Without a BYPASSRLS/superuser audit identity, FORCE RLS could make
        // 'zero documents' look like success. Refuse an incomplete inventory.
        await using (var checkRole = new NpgsqlCommand(
            """
            SELECT COALESCE((
                SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname=current_user
            ),false)
            """, conn, tx))
        {
            if ((bool)(await checkRole.ExecuteScalarAsync(ct))! != true)
                throw new InvalidOperationException(
                    "Checkpoint requires a dedicated privileged read-only audit identity.");
        }

        // Read lock helps prevent DB mutations while enumerating metadata.
        // No PostgreSQL lock can freeze S3: caller MUST quiesce all writers.
        await using (var lockCmd = new NpgsqlCommand(
            "LOCK TABLE documents, upload_intents, upload_request_keys IN SHARE MODE NOWAIT",
            conn, tx))
            await lockCmd.ExecuteNonQueryAsync(ct);

        await using (var pending = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM upload_intents) +
                   (SELECT count(*) FROM upload_request_keys WHERE status='pending')
            """, conn, tx))
        {
            if ((long)(await pending.ExecuteScalarAsync(ct))! != 0)
                throw new InvalidOperationException(
                    "Object checkpoint requires reconciliation of all pending uploads and idempotency requests.");
        }

        var list = new List<Metadata>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT tenant_id,id,storage_key,length_bytes
              FROM documents ORDER BY tenant_id,id
            """, conn, tx))
        await using (var rows = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rows.ReadAsync(ct))
            {
                var item = new Metadata(
                    rows.GetGuid(0), rows.GetGuid(1), rows.GetString(2), rows.GetInt64(3));
                if (string.IsNullOrWhiteSpace(item.StorageKey) || item.LengthBytes <= 0 ||
                    !keys.Add(item.StorageKey))
                    throw new InvalidOperationException(
                        "Object checkpoint detected an invalid or reused global S3 object key.");
                list.Add(item);
            }
        }
        await tx.CommitAsync(ct);
        return list;
    }

    private static async Task<(long Length, string Sha256)> HashObjectAsync(
        IAmazonS3 storage, string bucket, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            throw new InvalidOperationException("Checkpoint bucket is required.");
        try
        {
            using var response = await storage.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key
            }, ct);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long length = 0;
            int read;
            while ((read = await response.ResponseStream.ReadAsync(buffer, ct)) > 0)
            {
                length = checked(length + read);
                digest.AppendData(buffer, 0, read);
            }
            return (length, Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant());
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // No tenant IDs, object keys, tokens or object contents in logs.
            throw new InvalidOperationException(
                "Object checkpoint failed: referenced S3 object is missing.");
        }
    }
}
