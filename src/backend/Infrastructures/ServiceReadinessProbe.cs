using System.Net.Sockets;
using Npgsql;

namespace Peritial.Infrastructures;

/// <summary>
/// Fast, read-only dependency probe for the API admission path.
/// This is deliberately not an end-to-end test of OIDC, storage or malware signatures.
/// No database data, storage object, raw exception or secret is exposed.
/// </summary>
public sealed class ServiceReadinessProbe(
    NpgsqlDataSource database, string scannerHost, int scannerPort)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public async Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            // Use the same least-privilege application data source as REST/MCP;
            // SELECT 1 requires no tenant context and does not bypass FORCE RLS.
            await using (var connection = await database.OpenConnectionAsync(timeout.Token))
            {
                await using var command = new NpgsqlCommand("SELECT 1", connection)
                {
                    CommandTimeout = 2
                };
                if (await command.ExecuteScalarAsync(timeout.Token) is not int value || value != 1)
                    return false;
            }

            // Clamd's zPING command uses a NUL delimiter. Require exact PONG,
            // never treat a TCP connect or arbitrary reply as scan readiness.
            using var socket = new TcpClient();
            await socket.ConnectAsync(scannerHost, scannerPort, timeout.Token);
            await using var stream = socket.GetStream();
            await stream.WriteAsync("zPING\0"u8.ToArray(), timeout.Token);
            var response = new byte[5];
            await stream.ReadExactlyAsync(response, timeout.Token);
            return response.AsSpan().SequenceEqual("PONG\0"u8);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // An aborted HTTP request must not keep probing dependencies.
            throw;
        }
        catch (Exception)
        {
            // Failure is always not-ready; never echo exception details.
            return false;
        }
    }
}
