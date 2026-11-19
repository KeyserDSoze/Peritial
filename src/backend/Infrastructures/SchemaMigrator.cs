using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Peritial.Infrastructures;

/// <summary>
/// Explicit, forward-only PostgreSQL schema migrator. Requires a separate DDL-owning
/// connection, never the application or recovery roles. Never runs at API startup.
/// All scripts, checksums and journal entries commit atomically under an advisory lock.
/// </summary>
public static partial class SchemaMigrator
{
    private const long DdlLock = 4700039001;

    private sealed record Script(int Version, string Name, string Sql, string Sha256);

    private static async Task<Script[]> LoadScriptsAsync(CancellationToken ct)
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        var definitions = new (int Version, string File)[]
        {
            (1, "001_init.sql"),
            (2, "V002__security_audit_action_index.sql"),
            (3, "V003__sinistri_rls.sql"),
            (4, "V004__perizie_sinistro_link.sql"),
            (5, "V005__sinistri_audited_delete.sql"),
            (6, "V006__perizie_link_revision_history.sql"),
            (7, "V007__perizie_link_audit_guards.sql"),
            (8, "V008__perizie_revoke_app_delete.sql"),
            (9, "V009__documents_revoke_app_mutations.sql"),
            (10, "V010__documents_catalog_keyset_indexes.sql")
        };
        var scripts = new List<Script>();
        foreach (var (version, file) in definitions)
        {
            var resource = assembly.GetManifestResourceNames().Single(n =>
                n.EndsWith(file, StringComparison.Ordinal));
            await using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("Embedded schema migration missing: " + file);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = await reader.ReadToEndAsync(ct);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
            scripts.Add(new Script(version, file, sql, digest));
        }
        return scripts.ToArray();
    }

    /// <summary>
    /// Applies pending migrations. Refuses preexisting unjournaled schemas rather
    /// than incorrectly marking a legacy database as compatible.
    /// Returns the number of newly applied versions (zero for a safe replay).
    /// </summary>
    public static async Task<int> ApplyAsync(NpgsqlDataSource admin, CancellationToken ct = default)
    {
        var scripts = await LoadScriptsAsync(ct);
        await using var conn = await admin.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@key)", conn, tx))
        {
            acquire.Parameters.AddWithValue("key", DdlLock);
            await acquire.ExecuteNonQueryAsync(ct);
        }

        // A preexisting local/production installation must undergo a reviewed,
        // backed-up baseline, never an automatic 'looks similar' migration.
        bool hasJournal;
        await using (var journal = new NpgsqlCommand(
            "SELECT to_regclass('schema_migrations') IS NOT NULL", conn, tx))
            hasJournal = (bool)(await journal.ExecuteScalarAsync(ct))!;

        bool hasLegacySchema;
        await using (var legacy = new NpgsqlCommand(
            "SELECT to_regclass('perizie') IS NOT NULL OR " +
            "to_regclass('documents') IS NOT NULL OR " +
            "to_regclass('security_audit') IS NOT NULL OR " +
            "to_regclass('upload_intents') IS NOT NULL OR " +
            "to_regclass('upload_request_keys') IS NOT NULL", conn, tx))
            hasLegacySchema = (bool)(await legacy.ExecuteScalarAsync(ct))!;

        if (!hasJournal && hasLegacySchema)
            throw new InvalidOperationException(
                "Untracked existing Peritial schema: back up and review a manual baseline. " +
                "Automatic migration is disabled to protect existing data.");

        await using (var createJournal = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version integer PRIMARY KEY,
                filename text NOT NULL,
                sha256 varchar(64) NOT NULL CHECK (length(sha256) = 64),
                applied_at_utc timestamptz NOT NULL DEFAULT now()
            )
            """, conn, tx))
            await createJournal.ExecuteNonQueryAsync(ct);

        var applied = new List<(int Version, string Name, string Digest)>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT version, filename, sha256 FROM schema_migrations ORDER BY version", conn, tx))
        await using (var rows = await cmd.ExecuteReaderAsync(ct))
            while (await rows.ReadAsync(ct))
                applied.Add((rows.GetInt32(0), rows.GetString(1), rows.GetString(2)));

        if (applied.Count == 0 && hasLegacySchema)
            throw new InvalidOperationException("Legacy schema with an empty migration journal: manual baseline required.");

        for (var i = 0; i < applied.Count; i++)
        {
            if (i >= scripts.Length || applied[i].Version != scripts[i].Version ||
                applied[i].Name != scripts[i].Name ||
                !string.Equals(applied[i].Digest, scripts[i].Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Schema migration version/history/checksum drift at position " + (i + 1));
        }
        if (applied.Count > 0 && !hasLegacySchema)
            throw new InvalidOperationException("Migration journal exists but core Peritial tables are absent.");

        var count = 0;
        foreach (var script in scripts.Skip(applied.Count))
        {
            // PostgreSQL supports transactional DDL; journal + entire migration
            // are committed together, and failure rolls them back together.
            await using (var ddl = new NpgsqlCommand(script.Sql, conn, tx))
                await ddl.ExecuteNonQueryAsync(ct);
            await using (var record = new NpgsqlCommand(
                "INSERT INTO schema_migrations(version, filename, sha256) VALUES (@version, @name, @digest)",
                conn, tx))
            {
                record.Parameters.AddWithValue("version", script.Version);
                record.Parameters.AddWithValue("name", script.Name);
                record.Parameters.AddWithValue("digest", script.Sha256);
                await record.ExecuteNonQueryAsync(ct);
            }
            count++;
        }
        await tx.CommitAsync(ct);
        return count;
    }
}
