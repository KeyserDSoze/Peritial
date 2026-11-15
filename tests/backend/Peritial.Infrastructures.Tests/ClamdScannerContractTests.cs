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
        string host, int port, int timeoutSeconds = 15)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Malware:Clamd:Host"] = host,
                ["Malware:Clamd:Port"] = port.ToString(),
                ["Malware:Clamd:TimeoutSeconds"] = timeoutSeconds.ToString()
            }).Build();
        return new ClamdFileMalwareScanner(config);
    }
}
