using Npgsql;

namespace Peritial.Infrastructures;

/// <summary>Opt-in local bootstrap only; production requires an explicit migration step.</summary>
public static class SchemaBootstrap
{
    public static async Task ApplyDevelopmentSchemaAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        var assembly = typeof(SchemaBootstrap).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("001_init.sql", StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Embedded SQL migration missing.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(ct);
        await using var cmd = dataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
