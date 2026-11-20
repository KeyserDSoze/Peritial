using Npgsql;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class SchemaMigrationTests
{
    [Fact]
    public async Task Fresh_database_applies_versioned_migrations_only_once_and_rejects_checksum_drift()
    {
        await WithIsolatedSchemaAsync(async db =>
        {
            Assert.Equal(10, await SchemaMigrator.ApplyAsync(db));
            Assert.Equal(0, await SchemaMigrator.ApplyAsync(db));

            await using var conn = await db.OpenConnectionAsync();
            await using (var query = new NpgsqlCommand(
                "SELECT count(*) FROM schema_migrations WHERE version IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10)", conn))
                Assert.Equal(10L, (long)(await query.ExecuteScalarAsync())!);
            await using (var index = new NpgsqlCommand(
                "SELECT to_regclass('ix_security_audit_tenant_action_time') IS NOT NULL", conn))
                Assert.True((bool)(await index.ExecuteScalarAsync())!);

            await using (var tamper = new NpgsqlCommand(
                "UPDATE schema_migrations SET sha256 = repeat('0', 64) WHERE version = 1", conn))
                Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrator.ApplyAsync(db));
        });
    }

    [Fact]
    public async Task Upgrade_from_real_versioned_V005_keeps_existing_link_and_assigns_V006_baseline()
    {
        await WithIsolatedSchemaAsync(async db =>
        {
            await using var conn = await db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var journal = new NpgsqlCommand(
                "CREATE TABLE schema_migrations (" +
                "version integer PRIMARY KEY, filename text NOT NULL, " +
                "sha256 varchar(64) NOT NULL CHECK(length(sha256)=64), " +
                "applied_at_utc timestamptz NOT NULL DEFAULT now())",conn,tx))
                await journal.ExecuteNonQueryAsync();
            string[] scripts = [
                "001_init.sql", "V002__security_audit_action_index.sql",
                "V003__sinistri_rls.sql", "V004__perizie_sinistro_link.sql",
                "V005__sinistri_audited_delete.sql"
            ];
            var assembly = typeof(SchemaMigrator).Assembly;
            for(var i=0;i<scripts.Length;i++)
            {
                var name = scripts[i];
                var resource = assembly.GetManifestResourceNames().Single(n =>
                    n.EndsWith(name,StringComparison.Ordinal));
                await using var resourceStream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(resourceStream,System.Text.Encoding.UTF8);
                var sql = await reader.ReadToEndAsync();
                await using(var cmd = new NpgsqlCommand(sql,conn,tx))
                    await cmd.ExecuteNonQueryAsync();
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
                await using(var record = new NpgsqlCommand(
                    "INSERT INTO schema_migrations(version,filename,sha256) VALUES (@v,@name,@sha)",
                    conn,tx))
                {
                    record.Parameters.AddWithValue("v",i+1);
                    record.Parameters.AddWithValue("name",name);
                    record.Parameters.AddWithValue("sha",hash);
                    await record.ExecuteNonQueryAsync();
                }
            }
            var tenant = Guid.NewGuid();
            var sinistro = Guid.NewGuid();
            var perizia = Guid.NewGuid();
            await using(var existing = new NpgsqlCommand(
                "INSERT INTO sinistri(tenant_id,id,external_reference,occurred_on,created_at_utc) " +
                "VALUES (@tenant,@sinistro,'PRE-V006','2026-11-18',now()); " +
                "INSERT INTO perizie(tenant_id,id,reference,status,created_at_utc,sinistro_id) " +
                "VALUES (@tenant,@perizia,'PRE-V006-P',-1,now(),@sinistro)", conn,tx))
            {
                existing.Parameters.AddWithValue("tenant",tenant);
                existing.Parameters.AddWithValue("sinistro",sinistro);
                existing.Parameters.AddWithValue("perizia",perizia);
                await existing.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            await conn.CloseAsync();
            Assert.Equal(5,await SchemaMigrator.ApplyAsync(db));
            Assert.Equal(0,await SchemaMigrator.ApplyAsync(db));
            await using var verify = await db.OpenConnectionAsync();
            await using(var record = new NpgsqlCommand(
                "SELECT sinistro_id,sinistro_link_version FROM perizie " +
                "WHERE tenant_id=@tenant AND id=@perizia",verify))
            {
                record.Parameters.AddWithValue("tenant",tenant);
                record.Parameters.AddWithValue("perizia",perizia);
                await using var row = await record.ExecuteReaderAsync();
                Assert.True(await row.ReadAsync());
                Assert.Equal(sinistro,row.GetGuid(0));
                Assert.Equal(1L,row.GetInt64(1));
            }
            await using var history = new NpgsqlCommand(
                "SELECT count(*) FROM perizia_sinistro_history",verify);
            Assert.Equal(0L,(long)(await history.ExecuteScalarAsync())!);
            await using var journalCount = new NpgsqlCommand(
                "SELECT count(*) FROM schema_migrations WHERE version BETWEEN 1 AND 10",verify);
            Assert.Equal(10L,(long)(await journalCount.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task V010_document_keyset_indexes_support_global_and_perizia_scoped_seek()
    {
        await WithIsolatedSchemaAsync(async db =>
        {
            Assert.Equal(10, await SchemaMigrator.ApplyAsync(db));
            Assert.Equal(0, await SchemaMigrator.ApplyAsync(db));

            await using var conn = await db.OpenConnectionAsync();
            foreach (var (name, columns) in new[] {
                ("ix_documents_tenant_created_id",
                    "(tenant_id, created_at_utc DESC, id DESC)"),
                ("ix_documents_tenant_perizia_created_id",
                    "(tenant_id, perizia_id, created_at_utc DESC, id DESC)")
            })
            {
                await using var index = new NpgsqlCommand(
                    "SELECT pg_get_indexdef(i.indexrelid) FROM pg_index i " +
                    "WHERE i.indexrelid=to_regclass(@index) " +
                    "AND i.indisvalid AND i.indisready AND i.indisunique=false", conn);
                index.Parameters.AddWithValue("index", name);
                var definition = (string?)await index.ExecuteScalarAsync();
                Assert.NotNull(definition);
                Assert.Contains("USING btree " + columns, definition);
            }

            // A controlled EXPLAIN, not a claimed production benchmark:
            // disable sequential and bitmap scans so the index ordering
            // must be able to satisfy a genuine keyset seek without Sort.
            await using var tx = await conn.BeginTransactionAsync();
            await using (var setup = new NpgsqlCommand(
                "SET LOCAL enable_seqscan=off; " +
                "SET LOCAL enable_bitmapscan=off; " +
                "SET LOCAL enable_sort=off", conn, tx))
                await setup.ExecuteNonQueryAsync();

            foreach (var (filtered, expectedIndex) in new[] {
                (false, "ix_documents_tenant_created_id"),
                (true, "ix_documents_tenant_perizia_created_id")
            })
            {
                var sql = "EXPLAIN (COSTS OFF) SELECT id, perizia_id, file_name, " +
                    "content_type, length_bytes, created_at_utc FROM documents " +
                    "WHERE tenant_id=@tenant " +
                    (filtered ? "AND perizia_id=@perizia " : "") +
                    "AND (created_at_utc,id)<(@time,@id) " +
                    "ORDER BY created_at_utc DESC,id DESC LIMIT 50";
                await using var explain = new NpgsqlCommand(sql, conn, tx);
                explain.Parameters.AddWithValue("tenant", Guid.NewGuid());
                if (filtered) explain.Parameters.AddWithValue("perizia", Guid.NewGuid());
                explain.Parameters.AddWithValue("time",
                    new DateTime(2026, 11, 19, 20, 0, 0, DateTimeKind.Utc));
                explain.Parameters.AddWithValue("id", Guid.NewGuid());
                var plan = new List<string>();
                await using (var reader = await explain.ExecuteReaderAsync())
                    while (await reader.ReadAsync()) plan.Add(reader.GetString(0));
                var text = string.Join("\n", plan);
                Assert.Contains(expectedIndex, text);
                Assert.DoesNotContain("Sort", text);
            }
            await tx.RollbackAsync();
        });
    }

    [Fact]
    public async Task V011_legacy_import_key_ledger_is_append_only_and_tenant_rls_forced()
    {
        await WithIsolatedSchemaAsync(async db =>
        {
            Assert.Equal(11, await SchemaMigrator.ApplyAsync(db));
            Assert.Equal(0, await SchemaMigrator.ApplyAsync(db));
            await using var conn = await db.OpenConnectionAsync();
            await using var check = new NpgsqlCommand(
                "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class " +
                "WHERE oid=to_regclass('legacy_xml_import_keys')", conn);
            Assert.True((bool)(await check.ExecuteScalarAsync())!);
            await using var privilege = new NpgsqlCommand(
                "SELECT has_table_privilege('peritial_app','legacy_xml_import_keys','SELECT') " +
                "AND has_table_privilege('peritial_app','legacy_xml_import_keys','INSERT') " +
                "AND NOT has_table_privilege('peritial_app','legacy_xml_import_keys','UPDATE') " +
                "AND NOT has_table_privilege('peritial_app','legacy_xml_import_keys','DELETE') " +
                "AND NOT has_table_privilege('peritial_recovery','legacy_xml_import_keys','SELECT')", conn);
            Assert.True((bool)(await privilege.ExecuteScalarAsync())!);
            await using var journal = new NpgsqlCommand(
                "SELECT count(*) FROM schema_migrations", conn);
            Assert.Equal(11L, (long)(await journal.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task Untracked_legacy_schema_is_rejected_without_creating_a_journal()
    {
        await WithIsolatedSchemaAsync(async db =>
        {
            await using (var conn = await db.OpenConnectionAsync())
            await using (var create = new NpgsqlCommand("CREATE TABLE perizie (id integer)", conn))
                await create.ExecuteNonQueryAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrator.ApplyAsync(db));
            await using var verify = await db.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT to_regclass('schema_migrations') IS NULL", verify);
            Assert.True((bool)(await cmd.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task Concurrent_migrators_are_serialized_and_do_not_duplicate_rows()
    {
        await WithIsolatedSchemaAsync(async db =>
        {
            var results = await Task.WhenAll(
                SchemaMigrator.ApplyAsync(db), SchemaMigrator.ApplyAsync(db));
            Assert.Equal(10, results.Sum());
            await using var conn = await db.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM schema_migrations", conn);
            Assert.Equal(10L, (long)(await cmd.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task Failed_later_migration_rolls_back_initial_schema_and_journal()
    {
        await WithIsolatedSchemaAsync(async db =>
        {
            // Deliberate name collision: V001 executes, then V002 must fail.
            await using (var conn = await db.OpenConnectionAsync())
            await using (var conflict = new NpgsqlCommand(
                "CREATE TABLE ix_security_audit_tenant_action_time (id integer)", conn))
                await conflict.ExecuteNonQueryAsync();

            await Assert.ThrowsAsync<PostgresException>(() => SchemaMigrator.ApplyAsync(db));

            await using var verify = await db.OpenConnectionAsync();
            await using var check = new NpgsqlCommand(
                "SELECT to_regclass('perizie') IS NULL AND " +
                "to_regclass('schema_migrations') IS NULL AND " +
                "to_regclass('ix_security_audit_tenant_action_time') IS NOT NULL", verify);
            Assert.True((bool)(await check.ExecuteScalarAsync())!);
        });
    }

    private static async Task WithIsolatedSchemaAsync(Func<NpgsqlDataSource, Task> test)
    {
        var cs = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
            ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required.");
        var schema = "migration_test_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(cs);
        await using (var conn = await admin.OpenConnectionAsync())
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", conn))
            await create.ExecuteNonQueryAsync();
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(cs) { SearchPath = schema };
            await using var scoped = NpgsqlDataSource.Create(builder.ConnectionString);
            await test(scoped);
        }
        finally
        {
            await using var conn = await admin.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
