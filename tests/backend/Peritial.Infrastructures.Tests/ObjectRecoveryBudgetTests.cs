using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class ObjectRecoveryBudgetTests
{
    [Fact]
    public void Exact_cumulative_multi_tenant_limit_is_admitted()
    {
        Assert.Equal(500L, ObjectRecoveryBudget.RequireWithin(
            new long[] { 100, 180, 220 }, 500));
    }

    [Fact]
    public void Sum_exceeding_limit_is_rejected_even_if_every_file_is_individually_small()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ObjectRecoveryBudget.RequireWithin(new long[] { 100, 180, 220 }, 499));
        Assert.Contains("budget", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Integer_overflow_is_rejected_instead_of_wrapping_to_small_sum()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ObjectRecoveryBudget.RequireWithin(new long[] { long.MaxValue - 1, 2 },
                long.MaxValue));
        Assert.Contains("budget", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Invalid_operator_budget_is_rejected(long limit)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ObjectRecoveryBudget.RequireWithin(new long[] { 10, 12 }, limit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Malformed_persisted_length_is_rejected(long invalidLength)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ObjectRecoveryBudget.RequireWithin(new long[] { 10, invalidLength }, 500));
    }

    [Fact]
    public void Empty_archives_have_zero_admitted_bytes()
    {
        Assert.Equal(0L, ObjectRecoveryBudget.RequireWithin(Array.Empty<long>(), 1));
    }
}
