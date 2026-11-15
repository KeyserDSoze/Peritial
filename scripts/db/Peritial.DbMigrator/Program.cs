using Npgsql;
using Peritial.Infrastructures;

// Standalone administration command. Never run automatically in the API process.
// The DDL owner connection string is supplied only through the environment.
if (args.Length != 1 || args[0] != "apply")
{
    Console.Error.WriteLine("Usage: PERITIAL_MIGRATOR_POSTGRES=<privileged connection> dotnet run --project scripts/db/Peritial.DbMigrator -- apply");
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
    await using var admin = NpgsqlDataSource.Create(options.ConnectionString);
    var applied = await SchemaMigrator.ApplyAsync(admin);
    Console.WriteLine($"Schema migrations committed: {applied}");
    return 0;
}
catch (Exception ex)
{
    // No connection strings, raw SQL, parameter values, or passwords in logs.
    Console.Error.WriteLine("Schema migration failed closed (" + ex.GetType().Name + ").");
    return 1;
}
