using Npgsql;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// Local CI fixture only: serializes test-role changes alongside schema DDL.
/// The production setup never provisions a demo password.
/// </summary>
internal static class TestDbRoleProvisioner
{
    public static async Task ConfigureAsync(NpgsqlDataSource admin)
    {
        await using var conn = await admin.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(4700039001)", conn, tx))
            await lockCmd.ExecuteNonQueryAsync();
        await using (var cmd = new NpgsqlCommand(
            "ALTER ROLE peritial_app WITH LOGIN PASSWORD 'local-only-app' NOSUPERUSER NOBYPASSRLS",
            conn, tx))
            await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }
}
