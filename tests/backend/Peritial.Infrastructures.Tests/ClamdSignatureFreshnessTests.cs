using Microsoft.Extensions.Configuration;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class ClamdSignatureFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Development_demo_can_opt_out_but_production_must_explicitly_set_age()
    {
        var empty = Config(null);
        Assert.False(ClamdSignaturePolicy.FromConfiguration(empty, isDevelopment: true).Enabled);
        Assert.Throws<InvalidOperationException>(() =>
            ClamdSignaturePolicy.FromConfiguration(empty, isDevelopment: false));
        Assert.True(ClamdSignaturePolicy.FromConfiguration(Config("72"), isDevelopment: false).Enabled);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("169")]
    [InlineData("NaN")]
    [InlineData("24.5")]
    [InlineData(" 48 ")]
    public void Invalid_signature_age_never_disables_the_guard(string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ClamdSignaturePolicy.FromConfiguration(Config(value), isDevelopment: true));
    }

    [Theory]
    [InlineData("ClamAV 1.5.0/28100/Mon Nov 16 10:00:00 2026", true)]
    [InlineData("ClamAV 1.5.0/28100/Sun Nov 15 00:00:00 2026", false)]
    [InlineData("ClamAV 1.5.0/28100/Tue Nov 17 10:00:00 2026", false)]
    [InlineData("COMMAND UNAVAILABLE", false)]
    [InlineData("ClamAV 1.5.0/0/Mon Nov 16 10:00:00 2026", false)]
    [InlineData("ClamAV 1.5.0/28100/unknown", false)]
    [InlineData("ClamAV 1.5.0/28100/Mon Nov 16 10:00:00 2026\nsecret", false)]
    public void Version_reply_must_show_fresh_database_date_or_fail_closed(string response, bool allowed)
    {
        var policy = ClamdSignaturePolicy.FromConfiguration(Config("8"), isDevelopment: false);
        Assert.Equal(allowed, policy.IsFreshReply(response, Now));
    }

    [Fact]
    public void Freshness_uses_utc_and_has_small_future_clock_skew_tolerance()
    {
        var policy = ClamdSignaturePolicy.FromConfiguration(Config("8"), isDevelopment: false);
        Assert.True(policy.IsFreshReply(
            "ClamAV 1.5.0/28100/Mon Nov 16 12:04:00 2026", Now));
        Assert.False(policy.IsFreshReply(
            "ClamAV 1.5.0/28100/Mon Nov 16 12:06:00 2026", Now));
    }

    private static IConfiguration Config(string? hours) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            hours is null ? new Dictionary<string, string?>() :
                new Dictionary<string, string?> { ["Malware:Clamd:MaxSignatureAgeHours"] = hours })
            .Build();
}
