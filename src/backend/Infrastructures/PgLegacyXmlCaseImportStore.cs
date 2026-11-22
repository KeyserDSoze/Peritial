using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Peritial.Business;
using Peritial.Domains;

namespace Peritial.Infrastructures;

/// <summary>
/// INTERNAL ONLY: one PG transaction for idempotency ledger, case insert and
/// successful audit. V012 shares the SQL reference mutex with *all* Sinistro
/// INSERT/UPDATE triggers and prevents collisions on imported references.
/// The import API remains disabled until broader XML data is characterized.
/// </summary>
public sealed class PgLegacyXmlCaseImportStore(NpgsqlDataSource dataSource)
    : ILegacyXmlCaseImportStore
{
    public async Task<LegacyXmlCaseImportResult> CommitAsync(
        TenantId tenant, string subject, string key,
        byte[] requestDigest, LegacyXmlPreviewDto preview,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256 ||
            requestDigest is null || requestDigest.Length != 32)
            throw new ArgumentException("Invalid XML import identity or request digest.");
        if (key is null || key.Length is < 16 or > 128 ||
            key.Any(c => !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c is '-' or '_' or ':' or '.')))
            throw new ArgumentException("Invalid XML import idempotency key.");
        if (preview is null || preview.AttachmentCount != 0 ||
            preview.Format is not ("classic" or "accertatore"))
            throw new ArgumentException("Only case-only XML facts can be imported.");
        // This validates the normalized reference and date in the domain layer.
        var sinistro = Sinistro.Create(tenant,
            preview.ExternalReference, preview.OccurredOn, preview.ReportedOn);
        if (!string.Equals(sinistro.ExternalReference, preview.ExternalReference,
            StringComparison.Ordinal))
            throw new ArgumentException("Noncanonical XML reference.");

        byte[] subjectDigest = SHA256.HashData(Encoding.UTF8.GetBytes(subject));
        byte[] keyDigest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        // Use the EXACT database-side lock expression from the V012 trigger.
        // Separate SHA and hashtextextended mutexes would not coordinate.
        var lockInput = tenant.Value.ToString("D") + "\n" + sinistro.ExternalReference;
        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);

        // V012 protects ALL writers with a database-side transaction mutex.
        // The INTERNAL XML importer must not wait on that shared mutex
        // indefinitely under bulk-load or stalled-transaction contention.
        // Preserve a stricter operator lock_timeout; otherwise cap only this
        // acquisition at 1500 ms, then restore the session's prior setting.
        // pg_settings.setting for lock_timeout is expressed in milliseconds.
        const int maxMutexWaitMs = 1500;
        int priorLockTimeoutMs;
        await using (var setting = scope.Command(
            "SELECT setting::integer FROM pg_settings WHERE name='lock_timeout'"))
            priorLockTimeoutMs = (int)(await setting.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("PostgreSQL lock_timeout unavailable."));
        bool narrowedLockTimeout = priorLockTimeoutMs == 0 ||
            priorLockTimeoutMs > maxMutexWaitMs;
        if (narrowedLockTimeout)
        {
            await using var set = scope.Command(
                "SELECT set_config('lock_timeout', @timeout, true)");
            set.Parameters.AddWithValue("timeout", maxMutexWaitMs + "ms");
            await set.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await using var guard = scope.Command(
                "SELECT pg_advisory_xact_lock(hashtextextended(@lock_input, 0))");
            guard.Parameters.AddWithValue("lock_input", lockInput);
            await guard.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "55P03")
        {
            // LockNotAvailable is temporary contention, NOT evidence of a
            // duplicate legacy identity. The aborted transaction rolls back
            // before any ledger, Sinistro or success audit can be committed.
            throw new LegacyXmlImportBusyException();
        }
        if (narrowedLockTimeout)
        {
            await using var restore = scope.Command(
                "SELECT set_config('lock_timeout', @timeout, true)");
            restore.Parameters.AddWithValue("timeout",
                priorLockTimeoutMs == 0 ? "0" : priorLockTimeoutMs + "ms");
            await restore.ExecuteNonQueryAsync(ct);
        }

        // Insert the ledger proof FIRST. PostgreSQL's UNIQUE arbitration
        // handles concurrent same-key attempts across different references,
        // while the deferred FK allows inserting the new case afterward.
        bool reserved;
        await using (var insert = scope.Command(
            "INSERT INTO legacy_xml_import_keys " +
            "(tenant_id,subject_digest,key_digest,request_digest,sinistro_id) " +
            "VALUES (@tenant,@subject,@key,@digest,@id) " +
            "ON CONFLICT (tenant_id,subject_digest,key_digest) " +
            "DO NOTHING RETURNING sinistro_id"))
        {
            insert.Parameters.AddWithValue("tenant", tenant.Value);
            insert.Parameters.AddWithValue("subject", subjectDigest);
            insert.Parameters.AddWithValue("key", keyDigest);
            insert.Parameters.AddWithValue("digest", requestDigest);
            insert.Parameters.AddWithValue("id", sinistro.Id);
            reserved = await insert.ExecuteScalarAsync(ct) is not null;
        }

        if (!reserved)
        {
            Guid existingId;
            byte[] previousDigest;
            await using (var find = scope.Command(
                "SELECT sinistro_id, request_digest FROM legacy_xml_import_keys " +
                "WHERE tenant_id=@tenant AND subject_digest=@subject AND key_digest=@key"))
            {
                find.Parameters.AddWithValue("tenant", tenant.Value);
                find.Parameters.AddWithValue("subject", subjectDigest);
                find.Parameters.AddWithValue("key", keyDigest);
                await using var row = await find.ExecuteReaderAsync(ct);
                if (!await row.ReadAsync(ct))
                    throw new LegacyXmlIdempotencyConflictException();
                existingId = row.GetGuid(0);
                previousDigest = row.GetFieldValue<byte[]>(1);
            }
            if (!CryptographicOperations.FixedTimeEquals(previousDigest, requestDigest))
                throw new LegacyXmlIdempotencyConflictException();

            // The deferred FK prevents deletion while a proof is recorded.
            // This SELECT also ensures RLS still resolves the original row.
            await using (var exists = scope.Command(
                "SELECT 1 FROM sinistri WHERE tenant_id=@tenant AND id=@id"))
            {
                exists.Parameters.AddWithValue("tenant", tenant.Value);
                exists.Parameters.AddWithValue("id", existingId);
                if (await exists.ExecuteScalarAsync(ct) is null)
                    throw new LegacyXmlIdempotencyConflictException();
            }
            await scope.CommitAsync(ct);
            return new LegacyXmlCaseImportResult(existingId, Replayed: true);
        }

        // The V012 trigger now serializes every Sinistro INSERT/UPDATE on
        // this exact reference and rejects any non-import collision.
        await using (var existing = scope.Command(
            "SELECT 1 FROM sinistri WHERE tenant_id=@tenant " +
            "AND external_reference=@reference LIMIT 1"))
        {
            existing.Parameters.AddWithValue("tenant", tenant.Value);
            existing.Parameters.AddWithValue("reference", sinistro.ExternalReference);
            if (await existing.ExecuteScalarAsync(ct) is not null)
                throw new LegacyXmlReferenceConflictException();
        }

        await using (var insert = scope.Command(
            "INSERT INTO sinistri (tenant_id,id,external_reference,occurred_on," +
            "reported_on,created_at_utc,version) " +
            "VALUES (@tenant,@id,@reference,@occurred,@reported,@created,1)"))
        {
            insert.Parameters.AddWithValue("tenant", tenant.Value);
            insert.Parameters.AddWithValue("id", sinistro.Id);
            insert.Parameters.AddWithValue("reference", sinistro.ExternalReference);
            insert.Parameters.Add("occurred", NpgsqlDbType.Date).Value = sinistro.OccurredOn;
            insert.Parameters.Add("reported", NpgsqlDbType.Date).Value =
                (object?)sinistro.ReportedOn ?? DBNull.Value;
            insert.Parameters.AddWithValue("created", sinistro.CreatedAtUtc);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await PgSecurityAuditTrail.AppendWithinScopeAsync(scope,
            new SecurityAuditEvent(Guid.NewGuid(), tenant, subject,
                "sinistro.import-xml", "succeeded", sinistro.Id, DateTime.UtcNow), ct);
        await scope.CommitAsync(ct);
        return new LegacyXmlCaseImportResult(sinistro.Id, Replayed: false);
    }
}
