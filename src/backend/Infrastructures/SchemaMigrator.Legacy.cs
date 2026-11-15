using Npgsql;

namespace Peritial.Infrastructures;

/// <summary>
/// Manual-only baseline of the unversioned V001 development schema. The
/// operator-facing CLI separately requires a verified PostgreSQL archive and
/// explicit maintenance confirmation. This method holds ACCESS EXCLUSIVE locks,
/// compares full catalog definitions with a freshly generated V001 reference,
/// and commits the journal and subsequent migrations in one transaction.
/// </summary>
public static partial class SchemaMigrator
{
    private const string LegacyCatalogSignatureSql = """
        SELECT jsonb_build_object(
          'relations', COALESCE((
            SELECT jsonb_agg(jsonb_build_array(
              c.relname, c.relkind::text, c.relpersistence::text,
              c.relrowsecurity, c.relforcerowsecurity, c.relreplident::text,
              c.reloptions::text, pg_get_userbyid(c.relowner), c.relacl::text
            ) ORDER BY c.relname)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=@schema
              AND c.relkind IN ('r','p','v','m','f','S')
          ), '[]'::jsonb),
          'columns', COALESCE((
            SELECT jsonb_agg(jsonb_build_array(
              c.relname, a.attnum, a.attname, format_type(a.atttypid,a.atttypmod),
              a.attnotnull, a.attidentity::text, a.attgenerated::text,
              a.attstorage::text, a.attcollation::regcollation::text,
              pg_get_expr(d.adbin,d.adrelid), a.attacl::text
            ) ORDER BY c.relname, a.attnum)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_attribute a ON a.attrelid=c.oid
            LEFT JOIN pg_attrdef d ON d.adrelid=c.oid AND d.adnum=a.attnum
            WHERE n.nspname=@schema AND c.relkind IN ('r','p','f')
              AND a.attnum > 0 AND NOT a.attisdropped
          ), '[]'::jsonb),
          'constraints', COALESCE((
            SELECT jsonb_agg(jsonb_build_array(
              c.relname, k.conname, k.contype::text,
              k.condeferrable, k.condeferred, k.convalidated,
              pg_get_constraintdef(k.oid,true)
            ) ORDER BY c.relname, k.conname)
            FROM pg_constraint k
            JOIN pg_class c ON c.oid=k.conrelid
            JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=@schema
          ), '[]'::jsonb),
          'indexes', COALESCE((
            SELECT jsonb_agg(jsonb_build_array(
              t.relname, idx.relname, i.indisunique, i.indisprimary,
              i.indisvalid, i.indisready, pg_get_indexdef(i.indexrelid)
            ) ORDER BY t.relname, idx.relname)
            FROM pg_index i
            JOIN pg_class t ON t.oid=i.indrelid
            JOIN pg_class idx ON idx.oid=i.indexrelid
            JOIN pg_namespace n ON n.oid=t.relnamespace
            WHERE n.nspname=@schema
          ), '[]'::jsonb),
          'policies', COALESCE((
            SELECT jsonb_agg(jsonb_build_array(
              tablename, policyname, permissive, roles::text, cmd, qual, with_check
            ) ORDER BY tablename,policyname)
            FROM pg_policies WHERE schemaname=@schema
          ), '[]'::jsonb),
          'triggers', COALESCE((
            SELECT jsonb_agg(jsonb_build_array(
              c.relname, t.tgname, pg_get_triggerdef(t.oid)
            ) ORDER BY c.relname,t.tgname)
            FROM pg_trigger t
            JOIN pg_class c ON c.oid=t.tgrelid
            JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=@schema AND NOT t.tgisinternal
          ), '[]'::jsonb),
          'rules', COALESCE((
            SELECT jsonb_agg(jsonb_build_array(
              c.relname, r.rulename, pg_get_ruledef(r.oid)
            ) ORDER BY c.relname,r.rulename)
            FROM pg_rewrite r
            JOIN pg_class c ON c.oid=r.ev_class
            JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=@schema
          ), '[]'::jsonb),
          'functions', COALESCE((
            SELECT jsonb_agg(jsonb_build_array(
              p.proname, pg_get_function_identity_arguments(p.oid),
              pg_get_functiondef(p.oid)
            ) ORDER BY p.proname,pg_get_function_identity_arguments(p.oid))
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname=@schema
          ), '[]'::jsonb),
          'custom_types', COALESCE((
            SELECT jsonb_agg(jsonb_build_array(
              t.typname, t.typtype::text, format_type(t.typbasetype,t.typtypmod)
            ) ORDER BY t.typname)
            FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace
            WHERE n.nspname=@schema AND t.typtype IN ('e','d','r','m')
          ), '[]'::jsonb)
        )::text
        """;

    /// <summary>
    /// Performs the catalog verification and atomic adoption. Call only from
    /// an operator tool which has independently verified the backup archive,
    /// consent and maintenance window. Does not modify legacy tenant rows.
    /// </summary>
    public static async Task<int> AdoptLegacyAsync(
        NpgsqlDataSource admin, CancellationToken ct = default)
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
        string target;
        await using (var schema = new NpgsqlCommand("SELECT current_schema()", conn, tx))
            target = (string?)await schema.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("A dedicated PostgreSQL schema is required.");
        if (target is "pg_catalog" or "information_schema" || target.StartsWith("pg_toast", StringComparison.Ordinal))
            throw new InvalidOperationException("Cannot baseline a PostgreSQL system schema.");

        bool hasJournal;
        await using (var check = new NpgsqlCommand(
            "SELECT to_regclass('schema_migrations') IS NOT NULL", conn, tx))
            hasJournal = (bool)(await check.ExecuteScalarAsync(ct))!;
        if (hasJournal)
            throw new InvalidOperationException("Migration journal already exists: use 'apply' instead.");

        bool rolesSafe;
        await using (var check = new NpgsqlCommand(
            """
            SELECT count(*)=2 FROM pg_roles
            WHERE rolname IN ('peritial_app','peritial_recovery')
              AND NOT rolsuper AND NOT rolbypassrls
              AND NOT rolcreatedb AND NOT rolcreaterole
            """, conn, tx))
            rolesSafe = (bool)(await check.ExecuteScalarAsync(ct))!;
        if (!rolesSafe)
            throw new InvalidOperationException("Required independent nonprivileged database roles are missing or unsafe.");

        // NOWAIT: fail rather than racing the application or blocking an
        // incident response. The operator must have stopped all writers.
        await using (var freeze = new NpgsqlCommand(
            """
            LOCK TABLE perizie, documents, security_audit, upload_intents,
              upload_request_keys IN ACCESS EXCLUSIVE MODE NOWAIT
            """, conn, tx))
            await freeze.ExecuteNonQueryAsync(ct);

        var observed = await ReadCatalogSignatureAsync(conn, tx, target, ct);

        // PostgreSQL SAVEPOINT isolates the reference V001 DDL, including
        // any role/schema grants, so no changes made merely for comparison
        // survive. Its catalog definitions have no tenant rows.
        const string savepointName = "peritial_legacy_reference";
        await using (var savepoint = new NpgsqlCommand(
            "SAVEPOINT " + savepointName, conn, tx))
            await savepoint.ExecuteNonQueryAsync(ct);
        var scratch = "peritial_ref_" + Guid.NewGuid().ToString("N");
        string expected;
        try
        {
            // Generated identifier contains only [a-z0-9_]; no user input.
            await using (var create = new NpgsqlCommand(
                "CREATE SCHEMA " + scratch, conn, tx))
                await create.ExecuteNonQueryAsync(ct);
            await using (var changePath = new NpgsqlCommand(
                "SELECT set_config('search_path', @schema, true)", conn, tx))
            {
                changePath.Parameters.AddWithValue("schema", scratch);
                await changePath.ExecuteNonQueryAsync(ct);
            }
            await using (var createBaseline = new NpgsqlCommand(
                scripts[0].Sql, conn, tx))
                await createBaseline.ExecuteNonQueryAsync(ct);

            expected = await ReadCatalogSignatureAsync(conn, tx, scratch, ct);
        }
        finally
        {
            // Undo all reference DDL, even though the method has not yet
            // changed any tenant rows, policies or the migration journal.
            await using var rollback = new NpgsqlCommand(
                "ROLLBACK TO SAVEPOINT " + savepointName, conn, tx);
            await rollback.ExecuteNonQueryAsync(ct);
        }

        if (!string.Equals(observed, expected, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Legacy schema differs from V001 (tables, columns, indexes, RLS, ACL, " +
                "constraints, policies, triggers or functions). Manual review required.");

        await using (var journal = new NpgsqlCommand(
            """
            CREATE TABLE schema_migrations (
                version integer PRIMARY KEY,
                filename text NOT NULL,
                sha256 varchar(64) NOT NULL CHECK (length(sha256) = 64),
                applied_at_utc timestamptz NOT NULL DEFAULT now()
            )
            """, conn, tx))
            await journal.ExecuteNonQueryAsync(ct);

        foreach (var script in scripts)
        {
            // The schema has been proven identical to V001; never re-execute
            // V001 against live legacy data, as it contains DROP POLICY.
            if (script.Version != 1)
            {
                await using var ddl = new NpgsqlCommand(script.Sql, conn, tx);
                await ddl.ExecuteNonQueryAsync(ct);
            }
            await using var record = new NpgsqlCommand(
                """
                INSERT INTO schema_migrations(version, filename, sha256)
                  VALUES (@version, @filename, @digest)
                """, conn, tx);
            record.Parameters.AddWithValue("version", script.Version);
            record.Parameters.AddWithValue("filename", script.Name);
            record.Parameters.AddWithValue("digest", script.Sha256);
            await record.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return scripts.Length - 1;
    }

    private static async Task<string> ReadCatalogSignatureAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string schema, CancellationToken ct)
    {
        // Deparsing pg_get_indexdef and pg_get_constraintdef must use that
        // schema's search_path so references are comparable across schemas.
        await using (var path = new NpgsqlCommand(
            "SELECT set_config('search_path', @schema, true)", conn, tx))
        {
            path.Parameters.AddWithValue("schema", schema);
            await path.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = new NpgsqlCommand(LegacyCatalogSignatureSql, conn, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        return (string?)await cmd.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Could not inspect PostgreSQL catalog.");
    }
}
