using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Peritial.Business;
using Peritial.Domains;

namespace Peritial.Infrastructures;

/// <summary>
/// Uses PostgreSQL unique-index arbitration across independent app instances.
/// The caller's opaque key and JWT subject are hashed before any SQL operation.
/// </summary>
public sealed class PgUploadIdempotencyStore(NpgsqlDataSource dataSource) : IUploadIdempotencyStore
{
    public async Task<UploadIdempotencyReservation> ReserveAsync(
        TenantId tenant, string subject, string key, byte[] digest, Guid id, CancellationToken ct = default)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(subject) || subject.Length > 256 ||
            digest is null || digest.Length != 32)
            throw new ArgumentException("Invalid upload idempotency reservation.");
        if (key is null || key.Length is < 16 or > 128 ||
            key.Any(c => !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                           (c >= '0' && c <= '9') || c is '-' or '_' or ':' or '.')))
            throw new ArgumentException("Idempotency-Key must contain 16–128 ASCII letters, digits, hyphens, underscores, colons or dots.");

        byte[] actorHash = SHA256.HashData(Encoding.UTF8.GetBytes(subject));
        byte[] keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);

        bool inserted;
        await using (var insert = scope.Command(
            "INSERT INTO upload_request_keys " +
            "(tenant_id,subject_digest,key_digest,request_digest,document_id,status) " +
            "VALUES (@tenant,@actor,@key,@digest,@id,'pending') " +
            "ON CONFLICT (tenant_id,subject_digest,key_digest) DO NOTHING RETURNING document_id"))
        {
            insert.Parameters.AddWithValue("tenant", tenant.Value);
            insert.Parameters.AddWithValue("actor", actorHash);
            insert.Parameters.AddWithValue("key", keyHash);
            insert.Parameters.AddWithValue("digest", digest);
            insert.Parameters.AddWithValue("id", id);
            inserted = await insert.ExecuteScalarAsync(ct) is not null;
        }
        if (inserted)
        {
            await scope.CommitAsync(ct);
            return new UploadIdempotencyReservation(UploadIdempotencyState.Acquired);
        }

        Guid existingId;
        byte[] existingDigest;
        string status;
        await using (var find = scope.Command(
            "SELECT document_id,request_digest,status FROM upload_request_keys " +
            "WHERE tenant_id=@tenant AND subject_digest=@actor AND key_digest=@key"))
        {
            find.Parameters.AddWithValue("tenant", tenant.Value);
            find.Parameters.AddWithValue("actor", actorHash);
            find.Parameters.AddWithValue("key", keyHash);
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new UploadIdempotencyConflictException();
            existingId = reader.GetGuid(0);
            existingDigest = reader.GetFieldValue<byte[]>(1);
            status = reader.GetString(2);
        }
        if (!CryptographicOperations.FixedTimeEquals(existingDigest, digest))
            throw new UploadIdempotencyConflictException();

        if (status == "pending")
        {
            await scope.CommitAsync(ct);
            return new UploadIdempotencyReservation(UploadIdempotencyState.InProgress);
        }
        if (status != "completed")
            throw new UploadIdempotencyConflictException();

        DocumentDto result;
        await using (var doc = scope.Command(
            "SELECT id,perizia_id,file_name,content_type,length_bytes " +
            "FROM documents WHERE tenant_id=@tenant AND id=@id"))
        {
            doc.Parameters.AddWithValue("tenant", tenant.Value);
            doc.Parameters.AddWithValue("id", existingId);
            await using var reader = await doc.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new UploadIdempotencyConflictException();
            result = new DocumentDto(reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2), reader.GetString(3), reader.GetInt64(4));
        }
        await scope.CommitAsync(ct);
        return new UploadIdempotencyReservation(UploadIdempotencyState.Completed, result);
    }

    public async Task ReleaseAsync(TenantId tenant, Guid id, CancellationToken ct = default)
    {
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        await using (var cmd = scope.Command(
            "DELETE FROM upload_request_keys " +
            "WHERE tenant_id=@tenant AND document_id=@id AND status='pending'"))
        {
            cmd.Parameters.AddWithValue("tenant", tenant.Value);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await scope.CommitAsync(ct);
    }
}
