using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class SealedRecoveryBudgetTests
{
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
