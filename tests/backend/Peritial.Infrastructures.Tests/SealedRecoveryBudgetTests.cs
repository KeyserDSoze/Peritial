using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class SealedRecoveryBudgetTests
{
    [Theory]
    [InlineData(null, 1073741824L)]
    [InlineData("1", 1L)]
    [InlineData("4294967296", 4294967296L)]
    public void Parse_explicit_capacity_with_default(string? input, long expected) =>
        Assert.Equal(expected, SealedRecoveryBudget.ParsePlaintextLimit(input));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1.5")]
    [InlineData("4294967297")]
    [InlineData("9223372036854775808")]
    public void Invalid_capacity_never_means_unlimited(string input) =>
        Assert.Throws<InvalidOperationException>(() =>
            SealedRecoveryBudget.ParsePlaintextLimit(input));

    [Fact]
    public void Explicit_larger_capacity_admits_old_sets_without_relaxing_hard_cap()
    {
        var large = SealedRecoveryBudget.MaxPlaintextBytes + 1;
        Assert.Throws<InvalidOperationException>(() =>
            SealedRecoveryBudget.RequirePlaintext(new[] { large }));
        Assert.Equal(large, SealedRecoveryBudget.RequirePlaintext(new[] { large }, large));
        Assert.Equal(large + 64L * 1024 * 1024,
            SealedRecoveryBudget.RequireTransfer(
                new[] { large + 64L * 1024 * 1024 }, large));
        Assert.Throws<InvalidOperationException>(() =>
            SealedRecoveryBudget.RequireTransfer(Array.Empty<long>(), long.MaxValue));
        Assert.Throws<InvalidOperationException>(() =>
            SealedRecoveryBudget.RequirePlaintext(Array.Empty<long>(), 0));
    }

    [Fact]
    public void Exact_plaintext_capacity_is_admitted_including_metadata()
    {
        var limit = SealedRecoveryBudget.MaxPlaintextBytes;
        Assert.Equal(limit, SealedRecoveryBudget.RequirePlaintext(
            new long[] { limit - 100, 64, 4, 32 }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(long.MaxValue)]
    public void Cumulative_plaintext_capacity_cannot_overflow(long extra)
    {
        Assert.Throws<InvalidOperationException>(() =>
            SealedRecoveryBudget.RequirePlaintext(new[] {
                SealedRecoveryBudget.MaxPlaintextBytes, extra }));
    }

    [Fact]
    public void Ciphertext_overhead_has_a_separate_bounded_allowance()
    {
        var max = SealedRecoveryBudget.MaxTransferBytes;
        Assert.Equal(max, SealedRecoveryBudget.RequireTransfer(new long[] { max - 32, 32 }));
        Assert.Throws<InvalidOperationException>(() =>
            SealedRecoveryBudget.RequireTransfer(new long[] { max, 1 }));
    }

    [Fact]
    public void Transfer_count_includes_index_and_four_technical_files()
    {
        var count = ObjectRecoveryBudget.DefaultMaxObjects + 6;
        Assert.Equal((long)count, SealedRecoveryBudget.RequireTransfer(
            Enumerable.Repeat(1L, count)));
        Assert.Throws<InvalidOperationException>(() =>
            SealedRecoveryBudget.RequireTransfer(Enumerable.Repeat(1L, count + 1)));
    }
}
