using System.Net.Sockets;
using Npgsql;

namespace Peritial.Infrastructures;

/// <summary>
/// Read-only dependency admission probe. Four fixed low-cardinality checks
/// share a 3-second deadline; data/credentials/exception details stay private.
/// Dependency and overall outcomes are instrumented by ReadinessTelemetry.
/// </summary>
public sealed class ServiceReadinessProbe(
    NpgsqlDataSource database, string scannerHost, int scannerPort,
    IOidcReadinessProbe identity, IStorageReadinessProbe storage)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public Task<bool> CheckAsync(CancellationToken cancellationToken = default) =>
        ReadinessTelemetry.RunRequestAsync(CheckDependenciesAsync, cancellationToken);

    private async Task<bool> CheckDependenciesAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        if (!await ReadinessTelemetry.RunDependencyAsync(
            ReadinessDependency.Postgres, CheckDatabaseAsync, timeout.Token))
            return false;
        if (!await ReadinessTelemetry.RunDependencyAsync(
            ReadinessDependency.Clamav, CheckScannerAsync, timeout.Token))
            return false;
        if (!await ReadinessTelemetry.RunDependencyAsync(
            ReadinessDependency.Oidc, identity.CheckAsync, timeout.Token))
            return false;
        return await ReadinessTelemetry.RunDependencyAsync(
            ReadinessDependency.Storage, storage.CheckAsync, timeout.Token);
    }

    private async Task<bool> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        // Same least-privilege app data source as REST/MCP.
        // SELECT 1 does not use tenant context or bypass FORCE RLS.
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1", connection)
        {
            CommandTimeout = 2
        };
        return await command.ExecuteScalarAsync(cancellationToken) is int value && value == 1;
    }

    private async Task<bool> CheckScannerAsync(CancellationToken cancellationToken)
    {
        // A TCP connect alone is not ClamAV readiness: demand exact zPING/PONG.
        using var socket = new TcpClient();
        await socket.ConnectAsync(scannerHost, scannerPort, cancellationToken);
        await using var stream = socket.GetStream();
        await stream.WriteAsync("zPING\0"u8.ToArray(), cancellationToken);
        var response = new byte[5];
        await stream.ReadExactlyAsync(response, cancellationToken);
        return response.AsSpan().SequenceEqual("PONG\0"u8);
    }
}
