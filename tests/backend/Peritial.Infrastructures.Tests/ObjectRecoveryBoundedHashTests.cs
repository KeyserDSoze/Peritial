using System.Security.Cryptography;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// Deterministic stream-level proof of a hard S3 read budget. Merely checking
/// that an oversized body is eventually rejected would not prove bounded I/O.
/// </summary>
public sealed class ObjectRecoveryBoundedHashTests
{
    private sealed class CountedStream(byte[] content) : MemoryStream(content)
    {
        public long BytesDelivered { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            var read = Read(destination.Span);
            BytesDelivered += read;
            return ValueTask.FromResult(read);
        }
    }

    [Fact]
    public async Task Oversized_stream_consumes_only_committed_length_plus_one_byte()
    {
        var content = new byte[1_000_000];
        RandomNumberGenerator.Fill(content);
        const long expectedLength = 65536 + 17;
        using var input = new CountedStream(content);
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ObjectRecoveryBoundedHash.CopyHashAsync(input, output, expectedLength));

        // The first 64 KiB chunk was admitted and copied; the next 18
        // bytes detect the overrun before any excess data is written.
        Assert.Equal(expectedLength + 1, input.BytesDelivered);
        Assert.Equal(65536, output.Length);
        Assert.True(input.BytesDelivered < content.Length);
    }

    [Fact]
    public async Task Exactly_matching_input_is_copied_and_sha256_matches_the_bytes()
    {
        var content = new byte[131071];
        RandomNumberGenerator.Fill(content);
        using var input = new CountedStream(content);
        using var output = new MemoryStream();

        var (length, digest) = await ObjectRecoveryBoundedHash.CopyHashAsync(
            input, output, content.LongLength);

        Assert.Equal(content.LongLength, length);
        Assert.Equal(content.LongLength, input.BytesDelivered);
        Assert.Equal(content, output.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), digest);
    }

    [Fact]
    public async Task Truncated_input_fails_closed_without_fabricating_padding()
    {
        var content = "%PDF-1.7\nshort\n"u8.ToArray();
        using var input = new CountedStream(content);
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ObjectRecoveryBoundedHash.CopyHashAsync(input, output, content.LongLength + 7));

        Assert.Equal(content.LongLength, input.BytesDelivered);
        Assert.Equal(content, output.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Invalid_length_is_rejected_before_stream_access(long expectedLength)
    {
        using var input = new CountedStream(new byte[100]);
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ObjectRecoveryBoundedHash.CopyHashAsync(input, output, expectedLength));

        Assert.Equal(0, input.BytesDelivered);
        Assert.Equal(0, output.Length);
    }
}
