using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
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

        // Confirm that the actual four dependency checks (not only the
        // isolated helper) emit the bounded metric dimensions.
        var captured = new ConcurrentBag<(string Dependency, string Outcome)>();
        using var metrics = new MeterListener();
        metrics.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Peritial.Readiness")
                listener.EnableMeasurementEvents(instrument);
        };
        metrics.SetMeasurementEventCallback<long>((instrument, value, measurementTags, _) =>
        {
            if (instrument.Name != "peritial.readiness.dependency.checks" || value != 1)
                return;
            var tags = measurementTags.ToArray();
            var dependency = tags.FirstOrDefault(t => t.Key == "dependency").Value?.ToString();
            var outcome = tags.FirstOrDefault(t => t.Key == "outcome").Value?.ToString();
            if (dependency is not null && outcome is not null)
                captured.Add((dependency, outcome));
        });
        metrics.Start();
        var probe = new ServiceReadinessProbe(database, "127.0.0.1", port, new FixedIdentity(true), new FixedStorage(true));
        Assert.True(await probe.CheckAsync(deadline.Token));
        await scanner;
        foreach (var dependency in new[] { "postgres", "clamav", "oidc", "storage" })
            Assert.Contains(captured, item => item.Dependency == dependency && item.Outcome == "ok");
    }

    [Fact]
    public async Task Stale_Clamd_signature_database_makes_readiness_unavailable()
    {
        await using var database = TestDatabase();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var responder = Task.Run(async () =>
        {
            using (var pingPeer = await listener.AcceptTcpClientAsync(deadline.Token))
            {
                await using var pingStream = pingPeer.GetStream();
                var ping = new byte[6];
                await pingStream.ReadExactlyAsync(ping, deadline.Token);
                Assert.Equal("zPING\0"u8.ToArray(), ping);
                await pingStream.WriteAsync("PONG\0"u8.ToArray(), deadline.Token);
            }

            using var versionPeer = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var versionStream = versionPeer.GetStream();
            var version = new byte[9];
            await versionStream.ReadExactlyAsync(version, deadline.Token);
            Assert.Equal("zVERSION\0"u8.ToArray(), version);
            await versionStream.WriteAsync(
                "ClamAV 1.5.0/28000/Sun Nov 15 00:00:00 2026\0"u8.ToArray(),
                deadline.Token);
        }, deadline.Token);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Malware:Clamd:MaxSignatureAgeHours"] = "1"
            }).Build();
        var policy = ClamdSignaturePolicy.FromConfiguration(config, isDevelopment: false);

        var probe = new ServiceReadinessProbe(database, "127.0.0.1", port,
            new FixedIdentity(true), new FixedStorage(true), policy);
        Assert.False(await probe.CheckAsync(deadline.Token));
        await responder;
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

        var probe = new ServiceReadinessProbe(database, "127.0.0.1", port, new FixedIdentity(true), new FixedStorage(true));
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

        var probe = new ServiceReadinessProbe(database, "127.0.0.1", unusedPort, new FixedIdentity(true), new FixedStorage(true));
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
        var probe = new ServiceReadinessProbe(database, "127.0.0.1", 3310, new FixedIdentity(true), new FixedStorage(true));
        Assert.False(await probe.CheckAsync());
    }

    [Fact]
    public async Task Unavailable_identity_prevents_readiness_even_when_other_dependencies_respond()
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
            await stream.WriteAsync("PONG\0"u8.ToArray(), deadline.Token);
        }, deadline.Token);

        var probe = new ServiceReadinessProbe(database, "127.0.0.1", port, new FixedIdentity(false), new FixedStorage(true));
        Assert.False(await probe.CheckAsync(deadline.Token));
        await scanner;
    }

    [Fact]
    public async Task Unavailable_storage_prevents_readiness_even_when_others_respond()
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
            await stream.WriteAsync("PONG\0"u8.ToArray(), deadline.Token);
        }, deadline.Token);

        var probe = new ServiceReadinessProbe(database, "127.0.0.1", port,
            new FixedIdentity(true), new FixedStorage(false));
        Assert.False(await probe.CheckAsync(deadline.Token));
        await scanner;
    }

    private sealed class FixedStorage(bool ready) : IStorageReadinessProbe
    {
        public Task<bool> CheckAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ready);
    }

    private sealed class FixedIdentity(bool ready) : IOidcReadinessProbe
    {
        public Task<bool> CheckAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ready);
    }

    private static NpgsqlDataSource TestDatabase()
    {
        var connection = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
            ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required.");
        return NpgsqlDataSource.Create(connection);
    }
}
