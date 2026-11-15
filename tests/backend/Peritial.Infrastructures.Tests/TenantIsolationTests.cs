using Npgsql;
using Peritial.Domains;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class TenantIsolationTests
{
    [Fact]
    public async Task AppRole_IsNonPrivileged_AndRlsIsForcedOnBothTables()
    {
        await using var admin = await PrepareAdminAsync();
        await using var conn = await admin.OpenConnectionAsync();
        await using (var role = new NpgsqlCommand(
            "SELECT NOT rolsuper AND NOT rolbypassrls FROM pg_roles WHERE rolname = 'peritial_app'", conn))
            Assert.True((bool)(await role.ExecuteScalarAsync())!);

        await using var tables = new NpgsqlCommand(
            "SELECT count(*) FROM pg_class WHERE relname IN ('perizie','documents') AND relrowsecurity AND relforcerowsecurity", conn);
        Assert.Equal(2L, (long)(await tables.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task NoTenantContext_FailsClosed_ForSelectAndInsert()
    {
        await using var admin = await PrepareAdminAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var entry = Perizia.Create(tenant, "RLS-NOCONTEXT-" + Guid.NewGuid().ToString("N"));
        await new PgPeriziaRepository(admin).AddAsync(entry);

        await using var app = CreateAppDataSource();
        await using var conn = await app.OpenConnectionAsync();
        await using var read = new NpgsqlCommand("SELECT count(*) FROM perizie WHERE id = @id", conn);
        read.Parameters.AddWithValue("id", entry.Id);
        Assert.Equal(0L, (long)(await read.ExecuteScalarAsync())!);

        await using var write = new NpgsqlCommand(
            "INSERT INTO perizie (tenant_id,id,reference,status,created_at_utc) VALUES (@tenant,@id,@ref,-1,now())", conn);
        write.Parameters.AddWithValue("tenant", tenant.Value);
        write.Parameters.AddWithValue("id", Guid.NewGuid());
        write.Parameters.AddWithValue("ref", "DENIED-" + Guid.NewGuid().ToString("N"));
        var ex = await Assert.ThrowsAsync<PostgresException>(async () => await write.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task DirectSqlCannotReadUpdateDeleteOrInsertOtherTenantPerizie()
    {
        await using var admin = await PrepareAdminAsync();
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var foreign = Perizia.Create(b, "RLS-FOREIGN-" + Guid.NewGuid().ToString("N"));
        await new PgPeriziaRepository(admin).AddAsync(foreign);

        await using var app = CreateAppDataSource();
        await using var conn = await app.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await SetTenantAsync(conn, tx, a);

        Assert.Equal(0L, await CountAsync(conn, tx, "perizie", foreign.Id));
        await using (var update = new NpgsqlCommand("UPDATE perizie SET reference = 'CORRUPTED' WHERE id = @id", conn, tx))
        {
            update.Parameters.AddWithValue("id", foreign.Id);
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
        }
        await using (var delete = new NpgsqlCommand("DELETE FROM perizie WHERE id = @id", conn, tx))
        {
            delete.Parameters.AddWithValue("id", foreign.Id);
            Assert.Equal(0, await delete.ExecuteNonQueryAsync());
        }
        await tx.CommitAsync();

        await using var tx2 = await conn.BeginTransactionAsync();
        await SetTenantAsync(conn, tx2, a);
        await using var wrongInsert = new NpgsqlCommand(
            "INSERT INTO perizie (tenant_id,id,reference,status,created_at_utc) VALUES (@tenant,@id,@ref,-1,now())", conn, tx2);
        wrongInsert.Parameters.AddWithValue("tenant", b.Value);
        wrongInsert.Parameters.AddWithValue("id", Guid.NewGuid());
        wrongInsert.Parameters.AddWithValue("ref", "RLS-DENIED-" + Guid.NewGuid().ToString("N"));
        var ex = await Assert.ThrowsAsync<PostgresException>(async () => await wrongInsert.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        await tx2.RollbackAsync();
    }

    [Fact]
    public async Task DocumentsAreAlsoTenantScoped_EvenWithDirectSql()
    {
        await using var admin = await PrepareAdminAsync();
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var perizia = Perizia.Create(b, "RLS-DOC-" + Guid.NewGuid().ToString("N"));
        await new PgPeriziaRepository(admin).AddAsync(perizia);
        var documentId = Guid.NewGuid();
        await new PgDocumentRepository(admin).AddAsync(new(
            documentId, b, perizia.Id, "doc.pdf", "application/pdf", 5, $"{b.Value:N}/{documentId:N}.pdf", DateTime.UtcNow));

        await using var app = CreateAppDataSource();
        await using var conn = await app.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await SetTenantAsync(conn, tx, a);
        Assert.Equal(0L, await CountAsync(conn, tx, "documents", documentId));
        await using (var update = new NpgsqlCommand("UPDATE documents SET file_name = 'stolen.pdf' WHERE id = @id", conn, tx))
        {
            update.Parameters.AddWithValue("id", documentId);
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
        }
        await using (var delete = new NpgsqlCommand("DELETE FROM documents WHERE id = @id", conn, tx))
        {
            delete.Parameters.AddWithValue("id", documentId);
            Assert.Equal(0, await delete.ExecuteNonQueryAsync());
        }
        await tx.CommitAsync();

        await using var tx2 = await conn.BeginTransactionAsync();
        await SetTenantAsync(conn, tx2, a);
        await using var wrongInsert = new NpgsqlCommand(
            "INSERT INTO documents (tenant_id,id,perizia_id,file_name,content_type,length_bytes,storage_key,created_at_utc) " +
            "VALUES (@tenant,@id,@perizia,'doc.pdf','application/pdf',5,@key,now())", conn, tx2);
        wrongInsert.Parameters.AddWithValue("tenant", b.Value);
        wrongInsert.Parameters.AddWithValue("id", Guid.NewGuid());
        wrongInsert.Parameters.AddWithValue("perizia", perizia.Id);
        wrongInsert.Parameters.AddWithValue("key", $"{b.Value:N}/{Guid.NewGuid():N}.pdf");
        var ex = await Assert.ThrowsAsync<PostgresException>(async () => await wrongInsert.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        await tx2.RollbackAsync();
    }

    [Fact]
    public async Task ScopedRepositoryCanAccessOnlyOwnTenant_AndSessionContextNeverLeaksFromPool()
    {
        await using var admin = await PrepareAdminAsync();
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var first = Perizia.Create(a, "RLS-A-" + Guid.NewGuid().ToString("N"));
        var second = Perizia.Create(b, "RLS-B-" + Guid.NewGuid().ToString("N"));
        var seed = new PgPeriziaRepository(admin);
        await seed.AddAsync(first);
        await seed.AddAsync(second);

        var builder = AppConnectionBuilder();
        builder.MaxPoolSize = 1;
        await using var app = NpgsqlDataSource.Create(builder.ConnectionString);
        var repo = new PgPeriziaRepository(app);
        Assert.Contains(await repo.ListAsync(a), x => x.Id == first.Id);
        Assert.DoesNotContain(await repo.ListAsync(a), x => x.Id == second.Id);
        Assert.Contains(await repo.ListAsync(b), x => x.Id == second.Id);
        Assert.Null(await repo.FindAsync(a, second.Id));
        Assert.Null(await repo.FindAsync(b, first.Id));

        await using var conn = await app.OpenConnectionAsync();
        await using var read = new NpgsqlCommand("SELECT count(*) FROM perizie WHERE id IN (@a, @b)", conn);
        read.Parameters.AddWithValue("a", first.Id);
        read.Parameters.AddWithValue("b", second.Id);
        Assert.Equal(0L, (long)(await read.ExecuteScalarAsync())!);
    }

    private static async Task<NpgsqlDataSource> PrepareAdminAsync()
    {
        var connection = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
            ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required.");
        var admin = NpgsqlDataSource.Create(connection);
        await SchemaBootstrap.ApplyDevelopmentSchemaAsync(admin);
        await TestDbRoleProvisioner.ConfigureAsync(admin);
        return admin;
    }

    private static NpgsqlConnectionStringBuilder AppConnectionBuilder()
    {
        var root = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
            ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required.");
        return new NpgsqlConnectionStringBuilder(root)
        {
            Username = "peritial_app",
            Password = "local-only-app",
            ApplicationName = "peritial-rls-tests"
        };
    }
    private static NpgsqlDataSource CreateAppDataSource() =>
        NpgsqlDataSource.Create(AppConnectionBuilder().ConnectionString);

    private static async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenant)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT set_config('peritial.tenant_id', @tenant, true)", connection, transaction);
        cmd.Parameters.AddWithValue("tenant", tenant.Value.ToString("D"));
        await cmd.ExecuteNonQueryAsync();
    }
    private static async Task<long> CountAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string table, Guid id)
    {
        // Table names are fixed test-only literals, never user input.
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE id = @id", connection, tx);
        cmd.Parameters.AddWithValue("id", id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
