using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Peritial.Business;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class ClamdScannerContractTests
{
    // EICAR is a safe test pattern, not executable malware.
    private const string Eicar =
        @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    [Fact]
    public async Task Real_clamd_detects_EICAR_and_accepts_clean_content()
    {
        var host = Environment.GetEnvironmentVariable("PERITIAL_TEST_CLAMD_HOST");
        if (string.IsNullOrEmpty(host))
            return; // Run with Compose for a genuine signatures-backed engine.

        var scanner = NewScanner(host, 3310);
        Assert.Equal(MalwareScanResult.Clean,
            await scanner.ScanAsync("%PDF-1.7\nsafe content\n%%EOF\n"u8.ToArray()));
        Assert.Equal(MalwareScanResult.Infected,
            await scanner.ScanAsync(Encoding.ASCII.GetBytes(Eicar)));
    }

    [Fact]
    public async Task Stale_database_prevents_transmitting_file_bytes_to_clamd()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var responder = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync(deadline.Token);
            await using var stream = client.GetStream();
            var command = new byte[9];
            await stream.ReadExactlyAsync(command, deadline.Token);
            Assert.Equal("zVERSION\0"u8.ToArray(), command);
            await stream.WriteAsync(
                "ClamAV 1.5.0/28000/Sun Nov 15 00:00:00 2026\0"u8.ToArray(),
                deadline.Token);
        }, deadline.Token);

        var scanner = NewScanner("127.0.0.1", port, maxSignatureAgeHours: "1");
        await Assert.ThrowsAsync<MalwareScannerUnavailableException>(
            () => scanner.ScanAsync("safe"u8.ToArray(), deadline.Token));
        await responder;
        // Only one TCP connection (zVERSION) was accepted; zINSTREAM is never
        // opened when the database has an obsolete timestamp.
        Assert.False(server.Pending());
    }

    [Fact]
    public async Task Fresh_database_allows_an_instream_scan_after_version()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var responder = Task.Run(async () =>
        {
            using (var versionPeer = await server.AcceptTcpClientAsync(deadline.Token))
            {
                await using var version = versionPeer.GetStream();
                var command = new byte[9];
                await version.ReadExactlyAsync(command, deadline.Token);
                Assert.Equal("zVERSION\0"u8.ToArray(), command);
                var timestamp = DateTime.UtcNow.ToString(
                    "ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture);
                await version.WriteAsync(
                    Encoding.ASCII.GetBytes($"ClamAV 1.5.0/28100/{timestamp}\0"),
                    deadline.Token);
            }
            using var scanPeer = await server.AcceptTcpClientAsync(deadline.Token);
            await using var scan = scanPeer.GetStream();
            var streamCommand = new byte[10];
            await scan.ReadExactlyAsync(streamCommand, deadline.Token);
            Assert.Equal("zINSTREAM\0"u8.ToArray(), streamCommand);
            var frame = new byte[4];
            await scan.ReadExactlyAsync(frame, deadline.Token);
            var length = BinaryPrimitives.ReadInt32BigEndian(frame);
            Assert.Equal(4, length);
            var bytes = new byte[length];
            await scan.ReadExactlyAsync(bytes, deadline.Token);
            Assert.Equal("safe"u8.ToArray(), bytes);
            await scan.ReadExactlyAsync(frame, deadline.Token);
            Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(frame));
            await scan.WriteAsync("stream: OK\0"u8.ToArray(), deadline.Token);
        }, deadline.Token);

        var scanner = NewScanner("127.0.0.1", port, maxSignatureAgeHours: "1");
        Assert.Equal(MalwareScanResult.Clean,
            await scanner.ScanAsync("safe"u8.ToArray(), deadline.Token));
        await responder;
    }

    [Fact]
    public async Task Missing_clamd_endpoint_fails_closed()
    {
        var scanner = NewScanner("127.0.0.1", 1, 2);
        await Assert.ThrowsAsync<MalwareScannerUnavailableException>(
            async () => await scanner.ScanAsync("safe"u8.ToArray()));
    }

    [Fact]
    public async Task Error_response_from_a_scanner_is_not_a_clean_verdict()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        byte[] sent = "%PDF-1.7\nsafe\n%%EOF\n"u8.ToArray();

        var responder = Task.Run(async () =>
        {
            using var peer = await server.AcceptTcpClientAsync();
            await using var stream = peer.GetStream();

            var command = new byte[10];
            await stream.ReadExactlyAsync(command);
            Assert.Equal("zINSTREAM\0", Encoding.ASCII.GetString(command));
            var length = new byte[4];
            await stream.ReadExactlyAsync(length);
            var size = BinaryPrimitives.ReadInt32BigEndian(length);
            Assert.Equal(sent.Length, size);
            var data = new byte[size];
            await stream.ReadExactlyAsync(data);
            Assert.Equal(sent, data);
            await stream.ReadExactlyAsync(length);
            Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(length));

            await stream.WriteAsync("stream: scanner error ERROR\0"u8.ToArray());
        });

        await Assert.ThrowsAsync<MalwareScannerUnavailableException>(
            async () => await NewScanner("127.0.0.1", port)
                .ScanAsync(sent));
        await responder;
    }

    private static ClamdFileMalwareScanner NewScanner(
        string host, int port, int timeoutSeconds = 15,
        string? maxSignatureAgeHours = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Malware:Clamd:Host"] = host,
                ["Malware:Clamd:Port"] = port.ToString(),
                ["Malware:Clamd:TimeoutSeconds"] = timeoutSeconds.ToString(),
                ["Malware:Clamd:MaxSignatureAgeHours"] = maxSignatureAgeHours
            }).Build();
        return new ClamdFileMalwareScanner(config);
    }
}
