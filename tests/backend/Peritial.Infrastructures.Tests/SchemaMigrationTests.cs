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
            Assert.Equal(2, await SchemaMigrator.ApplyAsync(db));
            Assert.Equal(0, await SchemaMigrator.ApplyAsync(db));

            await using var conn = await db.OpenConnectionAsync();
            await using (var query = new NpgsqlCommand(
                "SELECT count(*) FROM schema_migrations WHERE version IN (1, 2)", conn))
                Assert.Equal(2L, (long)(await query.ExecuteScalarAsync())!);
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
            Assert.Equal(2, results.Sum());
            await using var conn = await db.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM schema_migrations", conn);
            Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync())!);
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
