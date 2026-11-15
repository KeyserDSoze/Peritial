using Npgsql;
using Peritial.Infrastructures;

// Standalone administration command. Never run automatically in the API process.
// DDL-owner credentials, maintenance confirmation and backup proof are supplied
// through protected environment variables, not CLI arguments or source files.
if (args.Length != 1 || args[0] is not ("apply" or "adopt" or "adopt-prelease"))
{
    Console.Error.WriteLine("Usage: dotnet run --project scripts/db/Peritial.DbMigrator -- apply|adopt|adopt-prelease");
    return 2;
}
var connectionString = Environment.GetEnvironmentVariable("PERITIAL_MIGRATOR_POSTGRES");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("PERITIAL_MIGRATOR_POSTGRES is required; never use the app or recovery connection.");
    return 2;
}
try
{
    var options = new NpgsqlConnectionStringBuilder(connectionString);
    if (options.Username is "peritial_app" or "peritial_recovery")
        throw new InvalidOperationException("Migration requires a distinct DDL-owning role.");

    if (args[0] is "adopt" or "adopt-prelease")
    {
        if (Environment.GetEnvironmentVariable("PERITIAL_LEGACY_CONFIRM") != "OFFLINE_BACKUP_VERIFIED")
            throw new InvalidOperationException("Legacy adoption requires explicit offline maintenance confirmation.");
        var backup = Environment.GetEnvironmentVariable("PERITIAL_LEGACY_BACKUP_FILE");
        if (string.IsNullOrWhiteSpace(backup))
            throw new InvalidOperationException("Legacy adoption requires a verified backup archive.");
        await LegacyBackupProof.VerifyAsync(backup);
    }

    await using var admin = NpgsqlDataSource.Create(options.ConnectionString);
    if (args[0] == "apply")
    {
        var applied = await SchemaMigrator.ApplyAsync(admin);
        Console.WriteLine($"Schema migrations committed: {applied}");
    }
    else
    {
        var applied = args[0] == "adopt-prelease"
            ? await SchemaMigrator.AdoptPreLeaseLegacyAsync(admin)
            : await SchemaMigrator.AdoptLegacyAsync(admin);
        Console.WriteLine($"Legacy baseline verified/adopted ({args[0]}); pending migrations committed: {applied}");
    }
    return 0;
}
catch (Exception ex)
{
    // No connection strings, SQL, tenant rows, archive path or secrets in logs.
    Console.Error.WriteLine("Schema migration failed closed (" + ex.GetType().Name + ").");
    return 1;
}
