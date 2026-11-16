using System.Net;
using System.Net.Sockets;
using Npgsql;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class ServiceReadinessProbeTests
{
    [Fact]
    public async Task Healthy_Postgres_and_Clamd_ping_are_ready()
    {
        await using var database = TestDatabase();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var scanner = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = client.GetStream();
            var ping = new byte[6];
            await stream.ReadExactlyAsync(ping, deadline.Token);
            Assert.Equal("zPING\0"u8.ToArray(), ping);
            await stream.WriteAsync("PONG\0"u8.ToArray(), deadline.Token);
        }, deadline.Token);

        var probe = new ServiceReadinessProbe(database, "127.0.0.1", port);
        Assert.True(await probe.CheckAsync(deadline.Token));
        await scanner;
    }

    [Fact]
    public async Task Wrong_Clamd_response_fails_closed()
    {
        await using var database = TestDatabase();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var scanner = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = client.GetStream();
            var ping = new byte[6];
            await stream.ReadExactlyAsync(ping, deadline.Token);
            await stream.WriteAsync("NOPE\0"u8.ToArray(), deadline.Token);
        }, deadline.Token);

        var probe = new ServiceReadinessProbe(database, "127.0.0.1", port);
        Assert.False(await probe.CheckAsync(deadline.Token));
        await scanner;
    }

    [Fact]
    public async Task Unreachable_scanner_fails_closed()
    {
        await using var database = TestDatabase();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var unusedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var probe = new ServiceReadinessProbe(database, "127.0.0.1", unusedPort);
        Assert.False(await probe.CheckAsync());
    }

    [Fact]
    public async Task Unreachable_database_fails_closed()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var unusedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var database = NpgsqlDataSource.Create(
            $"Host=127.0.0.1;Port={unusedPort};Database=peritial;Username=unavailable;Password=unavailable;Timeout=1;Pooling=false");
        var probe = new ServiceReadinessProbe(database, "127.0.0.1", 3310);
        Assert.False(await probe.CheckAsync());
    }

    private static NpgsqlDataSource TestDatabase()
    {
        var connection = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
            ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required.");
        return NpgsqlDataSource.Create(connection);
    }
}
