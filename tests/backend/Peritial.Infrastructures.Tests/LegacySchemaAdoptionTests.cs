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

            Assert.Equal(1, await SchemaMigrator.AdoptLegacyAsync(db));
            Assert.Equal(0, await SchemaMigrator.ApplyAsync(db));
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrator.AdoptLegacyAsync(db));

            await using var verify = await db.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT (SELECT count(*) FROM schema_migrations)=2 " +
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
