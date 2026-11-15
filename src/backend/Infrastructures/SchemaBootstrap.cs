using Npgsql;

namespace Peritial.Infrastructures;

/// <summary>Opt-in local bootstrap only; production requires an explicit migration step.</summary>
public static class SchemaBootstrap
{
    public static async Task ApplyDevelopmentSchemaAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        var assembly = typeof(SchemaBootstrap).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n =>
            n.EndsWith("001_init.sql", StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Embedded SQL migration missing.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(ct);

        // Multiple xUnit classes may bootstrap the same test database concurrently.
        // Serialize repeatable DDL in a single transaction, avoiding PostgreSQL
        // duplicate catalog entries on simultaneous CREATE TABLE IF NOT EXISTS.
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(4700039001)", connection, transaction))
            await acquire.ExecuteNonQueryAsync(ct);
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
            await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
