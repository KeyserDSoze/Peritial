using Npgsql;
using Peritial.Domains;

namespace Peritial.Infrastructures;

/// <summary>
/// Transaction-local tenant context. Commit and rollback clear the PostgreSQL
/// setting, so a pooled connection cannot leak tenant identity to the next request.
/// </summary>
internal sealed class TenantDbScope : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private TenantDbScope(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public static async Task<TenantDbScope> OpenAsync(
        NpgsqlDataSource source, TenantId tenant, CancellationToken ct = default)
    {
        if (tenant.Value == Guid.Empty)
            throw new ArgumentException("Tenant identity required.", nameof(tenant));

        var connection = await source.OpenConnectionAsync(ct);
        try
        {
            var tx = await connection.BeginTransactionAsync(ct);
            try
            {
                await using var command = new NpgsqlCommand(
                    "SELECT set_config('peritial.tenant_id', @tenant, true)", connection, tx);
                command.Parameters.AddWithValue("tenant", tenant.Value.ToString("D"));
                await command.ExecuteNonQueryAsync(ct);
                return new TenantDbScope(connection, tx);
            }
            catch
            {
                await tx.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public NpgsqlCommand Command(string sql) => new(sql, _connection, _transaction);

    public Task CommitAsync(CancellationToken ct = default) => _transaction.CommitAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
