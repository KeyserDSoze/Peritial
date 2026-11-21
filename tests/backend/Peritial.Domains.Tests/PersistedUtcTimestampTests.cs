using Peritial.Domains;
using Xunit;

namespace Peritial.Domains.Tests;

public sealed class PersistedUtcTimestampTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(5L)]
    [InlineData(9L)]
    [InlineData(10L)]
    [InlineData(11L)]
    [InlineData(19L)]
    public void Creation_timestamp_truncates_to_a_microsecond_without_rounding(long extraTicks)
    {
        var baseTime = new DateTime(2026, 11, 21, 12, 10, 30, DateTimeKind.Utc);
        var input = baseTime.AddTicks(extraTicks);
        var result = PersistedUtcTimestamp.ForNewRecord(input);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(baseTime.AddTicks(extraTicks - extraTicks % 10), result);
        Assert.Equal(0, result.Ticks % 10);
        Assert.True(result <= input);
        Assert.True(input.Ticks - result.Ticks < 10);
        Assert.Equal(result, PersistedUtcTimestamp.ForNewRecord(result));
    }

    [Fact]
    public void Truncation_does_not_roll_over_to_the_next_second_or_day()
    {
        var lastTick = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc)
            .AddTicks(9_999_999);
        var result = PersistedUtcTimestamp.ForNewRecord(lastTick);
        Assert.Equal(new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc)
            .AddTicks(9_999_990), result);
        Assert.Equal(2026, result.Year);
        Assert.Equal(31, result.Day);
        Assert.Equal(59, result.Second);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void Non_Utc_timestamps_are_rejected_rather_than_silently_converted()
    {
        var utc = new DateTime(2026, 11, 21, 12, 10, 30, DateTimeKind.Utc);
        Assert.Throws<ArgumentException>(() =>
            PersistedUtcTimestamp.ForNewRecord(DateTime.SpecifyKind(utc, DateTimeKind.Local)));
        Assert.Throws<ArgumentException>(() =>
            PersistedUtcTimestamp.ForNewRecord(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified)));
    }
}
