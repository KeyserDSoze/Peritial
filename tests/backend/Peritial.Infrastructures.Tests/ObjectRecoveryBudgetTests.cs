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
    public void Too_many_small_documents_are_rejected_even_well_below_byte_cap()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ObjectRecoveryBudget.RequireWithin(new long[] { 1, 1, 1 }, 500,
                maxObjects: 2));
        Assert.Contains("count", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exactly_at_document_count_limit_is_allowed()
    {
        Assert.Equal(3L, ObjectRecoveryBudget.RequireWithin(
            new long[] { 1, 1, 1 }, 500, maxObjects: 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(100001)]
    public void Invalid_operator_document_count_limit_is_denied(int maxObjects)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ObjectRecoveryBudget.RequireWithin(new long[] { 1 }, 500,
                maxObjects: maxObjects));
    }

    [Fact]
    public void Empty_archive_still_rejects_invalid_count_limit()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ObjectRecoveryBudget.RequireWithin(Array.Empty<long>(), 100,
                maxObjects: 0));
    }

    [Fact]
    public void Empty_archives_have_zero_admitted_bytes()
    {
        Assert.Equal(0L, ObjectRecoveryBudget.RequireWithin(Array.Empty<long>(), 1));
    }
}
