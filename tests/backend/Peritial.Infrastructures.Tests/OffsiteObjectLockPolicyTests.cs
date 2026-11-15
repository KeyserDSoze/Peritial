using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class OffsiteObjectLockPolicyTests
{
    [Theory]
    [InlineData("Enabled", "Enabled", "COMPLIANCE", 30, 0, 30)]
    [InlineData("Enabled", "Enabled", "COMPLIANCE", 0, 1, 30)]
    [InlineData("Enabled", "Enabled", "COMPLIANCE", 90, 0, 30)]
    public void Valid_S3_compliance_bucket_retention_is_accepted(
        string versioning, string lockState, string mode,
        int days, int years, int minDays)
    {
        OffsiteObjectLockPolicy.RequireComplianceConfiguration(
            versioning, lockState, mode, days, years, minDays);
    }

    [Theory]
    [InlineData("Suspended", "Enabled", "COMPLIANCE", 90, 0, 30)]
    [InlineData("Off", "Enabled", "COMPLIANCE", 90, 0, 30)]
    [InlineData("Enabled", "Disabled", "COMPLIANCE", 90, 0, 30)]
    [InlineData("Enabled", "Enabled", "GOVERNANCE", 90, 0, 30)]
    [InlineData("Enabled", "Enabled", null, 90, 0, 30)]
    [InlineData("Enabled", "Enabled", "COMPLIANCE", 29, 0, 30)]
    [InlineData("Enabled", "Enabled", "COMPLIANCE", 0, 0, 30)]
    [InlineData("Enabled", "Enabled", "COMPLIANCE", 31, 1, 30)]
    public void Unlocked_or_weak_bucket_is_rejected_before_writes(
        string? versioning, string? lockState, string? mode,
        int days, int years, int minDays)
    {
        Assert.Throws<InvalidOperationException>(() =>
            OffsiteObjectLockPolicy.RequireComplianceConfiguration(
                versioning, lockState, mode, days, years, minDays));
    }

    [Fact]
    public void Every_object_version_must_have_compliance_retention_in_future()
    {
        var now = DateTime.UtcNow;
        OffsiteObjectLockPolicy.RequireProtectedVersion(
            "version-1", "COMPLIANCE", now.AddDays(31), now, 30);
        Assert.Throws<InvalidOperationException>(() =>
            OffsiteObjectLockPolicy.RequireProtectedVersion(
                null, "COMPLIANCE", now.AddDays(31), now, 30));
        Assert.Throws<InvalidOperationException>(() =>
            OffsiteObjectLockPolicy.RequireProtectedVersion(
                "null", "COMPLIANCE", now.AddDays(31), now, 30));
        Assert.Throws<InvalidOperationException>(() =>
            OffsiteObjectLockPolicy.RequireProtectedVersion(
                "version-1", "GOVERNANCE", now.AddDays(31), now, 30));
        Assert.Throws<InvalidOperationException>(() =>
            OffsiteObjectLockPolicy.RequireProtectedVersion(
                "version-1", "COMPLIANCE", null, now, 30));
        Assert.Throws<InvalidOperationException>(() =>
            OffsiteObjectLockPolicy.RequireProtectedVersion(
                "version-1", "COMPLIANCE", now.AddDays(20), now, 30));
    }
}
