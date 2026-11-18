using Npgsql;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// The CLI separately validates operator consent and a verified PostgreSQL
/// backup; these tests exercise only the catalog-equivalence/atomic DDL gate.
/// </summary>
public sealed class LegacySchemaAdoptionTests
{
    [Fact]
    public async Task Exact_untracked_bootstrap_is_adopted_without_touching_existing_tenant_data()
    {
        await InSchemaAsync(async db =>
        {
            await SchemaBootstrap.ApplyDevelopmentSchemaAsync(db);
            var tenant = Guid.NewGuid();
            var id = Guid.NewGuid();
            await using (var conn = await db.OpenConnectionAsync())
            await using (var insert = new NpgsqlCommand(
                "INSERT INTO perizie(tenant_id,id,reference,status,created_at_utc) " +
                "VALUES (@tenant,@id,'LEGACY-TEST',-1,now())", conn))
            {
                insert.Parameters.AddWithValue("tenant", tenant);
                insert.Parameters.AddWithValue("id", id);
                await insert.ExecuteNonQueryAsync();
            }

            // Ordinary 'apply' must continue rejecting unjournaled schemas.
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrator.ApplyAsync(db));

            Assert.Equal(5, await SchemaMigrator.AdoptLegacyAsync(db));
            Assert.Equal(0, await SchemaMigrator.ApplyAsync(db));
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrator.AdoptLegacyAsync(db));

            await using var verify = await db.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT (SELECT count(*) FROM schema_migrations)=6 " +
                "AND (SELECT count(*) FROM perizie WHERE tenant_id=@tenant AND id=@id AND reference='LEGACY-TEST')=1 " +
                "AND to_regclass('ix_security_audit_tenant_action_time') IS NOT NULL " +
                "AND (SELECT count(*) FROM pg_class WHERE relname IN ('perizie','documents','security_audit'," +
                "'upload_intents','upload_request_keys') AND relrowsecurity AND relforcerowsecurity AND relnamespace=current_schema()::regnamespace)=5",
                verify);
            cmd.Parameters.AddWithValue("tenant", tenant);
            cmd.Parameters.AddWithValue("id", id);
            Assert.True((bool)(await cmd.ExecuteScalarAsync())!);
        });
    }

    [Theory]
    [InlineData("ALTER TABLE documents NO FORCE ROW LEVEL SECURITY")]
    [InlineData("ALTER TABLE perizie DISABLE ROW LEVEL SECURITY")]
    [InlineData("ALTER TABLE documents DROP COLUMN content_type")]
    [InlineData("GRANT SELECT ON security_audit TO peritial_app")]
    [InlineData("CREATE TABLE unexpected_legacy_table (id integer)")]
    [InlineData("DROP POLICY tenant_upload_requests ON upload_request_keys")]
    [InlineData("DROP INDEX ix_upload_intents_lease")]
    public async Task Altered_security_or_structure_is_refused_without_any_journal(
        string alteration)
    {
        await InSchemaAsync(async db =>
        {
            await SchemaBootstrap.ApplyDevelopmentSchemaAsync(db);
            await using (var conn = await db.OpenConnectionAsync())
            await using (var edit = new NpgsqlCommand(alteration, conn))
                await edit.ExecuteNonQueryAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SchemaMigrator.AdoptLegacyAsync(db));
            await using var verify = await db.OpenConnectionAsync();
            await using var check = new NpgsqlCommand(
                "SELECT to_regclass('schema_migrations') IS NULL AND " +
                "to_regclass('ix_security_audit_tenant_action_time') IS NULL", verify);
            Assert.True((bool)(await check.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task Missing_database_is_not_considered_legacy_and_cannot_be_adopted()
    {
        await InSchemaAsync(async db =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SchemaMigrator.AdoptLegacyAsync(db));
            await using var conn = await db.OpenConnectionAsync();
            await using var check = new NpgsqlCommand(
                "SELECT to_regclass('schema_migrations') IS NULL", conn);
            Assert.True((bool)(await check.ExecuteScalarAsync())!);
        });
    }


    [Fact]
    public async Task Prelease_legacy_schema_upgrades_missing_lease_atomically_without_rewriting_rows()
    {
        await InSchemaAsync(async db =>
        {
            await SchemaBootstrap.ApplyDevelopmentSchemaAsync(db);
            var tenant = Guid.NewGuid();
            var documentId = Guid.NewGuid();
            await using (var conn = await db.OpenConnectionAsync())
            {
                await using var legacy = new NpgsqlCommand(
                    "ALTER TABLE upload_intents DROP COLUMN lease_expires_at_utc; " +
                    "INSERT INTO upload_intents(tenant_id,document_id,storage_key) " +
                    "VALUES (@tenant,@doc,'legacy/key.pdf')", conn);
                legacy.Parameters.AddWithValue("tenant", tenant);
                legacy.Parameters.AddWithValue("doc", documentId);
                await legacy.ExecuteNonQueryAsync();
            }

            // A distinct, explicit operator action is required for a pre-V001 variant.
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrator.AdoptLegacyAsync(db));
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrator.ApplyAsync(db));

            Assert.Equal(5, await SchemaMigrator.AdoptPreLeaseLegacyAsync(db));
            Assert.Equal(0, await SchemaMigrator.ApplyAsync(db));
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrator.AdoptPreLeaseLegacyAsync(db));

            await using var conn2 = await db.OpenConnectionAsync();
            await using var check = new NpgsqlCommand(
                "SELECT (SELECT count(*) FROM schema_migrations)=6 " +
                "AND to_regclass('ix_upload_intents_lease') IS NOT NULL " +
                "AND (SELECT count(*) FROM upload_intents WHERE tenant_id=@tenant " +
                "  AND document_id=@doc AND storage_key='legacy/key.pdf' " +
                "  AND lease_expires_at_utc > created_at_utc)=1 " +
                "AND (SELECT relforcerowsecurity FROM pg_class " +
                "  WHERE oid='upload_intents'::regclass)", conn2);
            check.Parameters.AddWithValue("tenant", tenant);
            check.Parameters.AddWithValue("doc", documentId);
            Assert.True((bool)(await check.ExecuteScalarAsync())!);
        });
    }

    [Theory]
    [InlineData("ALTER TABLE documents NO FORCE ROW LEVEL SECURITY")]
    [InlineData("GRANT SELECT ON security_audit TO peritial_app")]
    [InlineData("DROP POLICY tenant_upload_requests ON upload_request_keys")]
    [InlineData("CREATE TABLE unexpected_prelease_table (id int)")]
    public async Task Prelease_adoption_rejects_other_drift_and_rolls_back_all_ddl(
        string additionalDrift)
    {
        await InSchemaAsync(async db =>
        {
            await SchemaBootstrap.ApplyDevelopmentSchemaAsync(db);
            await using (var conn = await db.OpenConnectionAsync())
            await using (var alter = new NpgsqlCommand(
                "ALTER TABLE upload_intents DROP COLUMN lease_expires_at_utc; " + additionalDrift, conn))
                await alter.ExecuteNonQueryAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SchemaMigrator.AdoptPreLeaseLegacyAsync(db));
            await using var verify = await db.OpenConnectionAsync();
            await using var check = new NpgsqlCommand(
                "SELECT to_regclass('schema_migrations') IS NULL " +
                "AND to_regclass('ix_upload_intents_lease') IS NULL " +
                "AND NOT EXISTS (SELECT 1 FROM information_schema.columns " +
                " WHERE table_schema=current_schema() AND table_name='upload_intents' " +
                " AND column_name='lease_expires_at_utc')", verify);
            Assert.True((bool)(await check.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task Prelease_adoption_does_not_silently_accept_exact_V001_or_index_only_drift()
    {
        await InSchemaAsync(async db =>
        {
            await SchemaBootstrap.ApplyDevelopmentSchemaAsync(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SchemaMigrator.AdoptPreLeaseLegacyAsync(db));
            await using (var conn = await db.OpenConnectionAsync())
            await using (var drop = new NpgsqlCommand("DROP INDEX ix_upload_intents_lease", conn))
                await drop.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SchemaMigrator.AdoptPreLeaseLegacyAsync(db));
            await using var verify = await db.OpenConnectionAsync();
            await using var check = new NpgsqlCommand(
                "SELECT to_regclass('schema_migrations') IS NULL", verify);
            Assert.True((bool)(await check.ExecuteScalarAsync())!);
        });
    }


    [Fact]
    public async Task Genuine_prelease_table_created_without_any_dropped_column_is_adoptable()
    {
        // Reconstruct the older initial CREATE TABLE definition: unlike a
        // V001 table with DROP COLUMN, PostgreSQL has no tombstone attnum slot.
        await InSchemaAsync(async db =>
        {
            var assembly = typeof(SchemaBootstrap).Assembly;
            var resource = assembly.GetManifestResourceNames().Single(x =>
                x.EndsWith("001_init.sql", StringComparison.Ordinal));
            await using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync();
            var absentAtCreation =
                "    lease_expires_at_utc timestamptz NOT NULL DEFAULT (now() + interval '2 minutes'),\n";
            var postCreationLease =
                "-- Re-runnable bootstrap for an existing local database with older intents.\n" +
                "ALTER TABLE upload_intents ADD COLUMN IF NOT EXISTS lease_expires_at_utc\n" +
                "    timestamptz NOT NULL DEFAULT (now() + interval '2 minutes');\n" +
                "CREATE INDEX IF NOT EXISTS ix_upload_intents_lease\n" +
                "    ON upload_intents (created_at_utc, lease_expires_at_utc);\n";
            var columnGrant =
                "GRANT UPDATE (lease_expires_at_utc) ON upload_intents TO peritial_app;\n";
            Assert.Contains(absentAtCreation, sql);
            Assert.Contains(postCreationLease, sql);
            Assert.Contains(columnGrant, sql);
            sql = sql.Replace(absentAtCreation, "", StringComparison.Ordinal)
                .Replace(postCreationLease, "", StringComparison.Ordinal)
                .Replace(columnGrant, "", StringComparison.Ordinal);

            await using (var conn = await db.OpenConnectionAsync())
            await using (var build = new NpgsqlCommand(sql, conn))
                await build.ExecuteNonQueryAsync();

            var tenant = Guid.NewGuid();
            var doc = Guid.NewGuid();
            await using (var conn = await db.OpenConnectionAsync())
            await using (var insert = new NpgsqlCommand(
                "INSERT INTO upload_intents (tenant_id, document_id, storage_key) " +
                "VALUES (@tenant, @id, 'prelease/original.pdf')", conn))
            {
                insert.Parameters.AddWithValue("tenant", tenant);
                insert.Parameters.AddWithValue("id", doc);
                await insert.ExecuteNonQueryAsync();
            }
            Assert.Equal(5, await SchemaMigrator.AdoptPreLeaseLegacyAsync(db));
            await using var verify = await db.OpenConnectionAsync();
            await using var check = new NpgsqlCommand(
                "SELECT (SELECT count(*) FROM schema_migrations)=6 " +
                "AND (SELECT count(*) FROM upload_intents WHERE tenant_id=@tenant " +
                " AND document_id=@id AND lease_expires_at_utc > created_at_utc)=1 " +
                "AND (SELECT attnum FROM pg_attribute " +
                " WHERE attrelid='upload_intents'::regclass AND attname='lease_expires_at_utc')=5", verify);
            check.Parameters.AddWithValue("tenant", tenant);
            check.Parameters.AddWithValue("id", doc);
            Assert.True((bool)(await check.ExecuteScalarAsync())!);
        });
    }

    private static async Task InSchemaAsync(Func<NpgsqlDataSource, Task> test)
    {
        var cs = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
            ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required.");
        var schema = "legacy_adopt_" + Guid.NewGuid().ToString("N");
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
