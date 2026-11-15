using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime;

namespace Peritial.Infrastructures;

/// <summary>
/// S3 Object Lock COMPLIANCE preflight for durable encrypted-only backups.
/// Bucket configuration is necessary but not sufficient: after every PUT the
/// exact returned object VersionId and its retention are queried separately.
/// Does not prove availability or prevent delete markers from hiding versions.
/// </summary>
public static class OffsiteObjectLockPolicy
{
    public const int MinimumRetentionDays = 30;

    public static void RequireComplianceConfiguration(
        string? versioningStatus, string? objectLockEnabled, string? mode,
        int days, int years, int minimumDays = MinimumRetentionDays)
    {
        if (minimumDays < 1 || days < 0 || years < 0 ||
            !string.Equals(versioningStatus, "Enabled", StringComparison.Ordinal) ||
            !string.Equals(objectLockEnabled, "Enabled", StringComparison.Ordinal) ||
            !string.Equals(mode, "COMPLIANCE", StringComparison.Ordinal) ||
            (days == 0 && years == 0) ||
            (days > 0 && years > 0) ||
            (days == 0 ? checked((long)years * 365) : days) < minimumDays)
            throw new InvalidOperationException(
                "Remote S3 must provide enabled versioning and COMPLIANCE Object Lock retention.");
    }

    public static void RequireProtectedVersion(string? versionId, string? mode,
        DateTime? retainedUntil, DateTime observedUtc,
        int minimumDays = MinimumRetentionDays)
    {
        if (string.IsNullOrWhiteSpace(versionId) ||
            string.Equals(versionId, "null", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(mode, "COMPLIANCE", StringComparison.Ordinal) ||
            retainedUntil is null ||
            minimumDays < 0 ||
            retainedUntil.Value.ToUniversalTime() <
                observedUtc.ToUniversalTime().AddDays(minimumDays)
                    .AddMinutes(minimumDays == 0 ? 0 : -5))
            throw new InvalidOperationException(
                "A remote encrypted object version lacks verified COMPLIANCE retention.");
    }

    public static async Task RequireCompliantBucketAsync(
        IAmazonS3 s3, string bucket, CancellationToken ct = default)
    {
        try
        {
            var versioning = await s3.GetBucketVersioningAsync(
                new GetBucketVersioningRequest { BucketName = bucket }, ct);
            var locked = await s3.GetObjectLockConfigurationAsync(
                new GetObjectLockConfigurationRequest { BucketName = bucket }, ct);
            var config = locked.ObjectLockConfiguration;
            var retention = config?.Rule?.DefaultRetention;
            RequireComplianceConfiguration(
                versioning.VersioningConfig?.Status?.ToString(),
                config?.ObjectLockEnabled?.ToString(),
                retention?.Mode?.ToString(),
                Convert.ToInt32(retention?.Days),
                Convert.ToInt32(retention?.Years));
        }
        catch (AmazonServiceException)
        {
            // Access denied, unsupported S3 API, missing policy or server error:
            // never assume a valid lock from the absence of evidence.
            throw new InvalidOperationException(
                "Cannot verify remote S3 COMPLIANCE Object Lock configuration.");
        }
    }

    /// <summary>
    /// Audit the CURRENT object version, not merely a bucket's default rule.
    /// A delete marker, overwritten object, missing permission or missing
    /// version identifier must never be interpreted as immutable storage.
    /// </summary>
    public static async Task RequireCurrentProtectedVersionAsync(
        IAmazonS3 s3, string bucket, string key, CancellationToken ct = default)
    {
        string? versionId;
        try
        {
            var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket, Key = key
            }, ct);
            versionId = head.VersionId;
        }
        catch (AmazonServiceException)
        {
            throw new InvalidOperationException(
                "Cannot read the current locked object version.");
        }
        await RequireProtectedObjectVersionAsync(
            s3, bucket, key, versionId, DateTime.UtcNow, ct, 0);
    }

    public static async Task RequireProtectedObjectVersionAsync(
        IAmazonS3 s3, string bucket, string key, string? versionId,
        DateTime observedUtc, CancellationToken ct = default,
        int minimumRemainingDays = MinimumRetentionDays)
    {
        // If S3 lacks versioning it is unsafe even to request a retention
        // check without explicit VersionId.
        if (string.IsNullOrWhiteSpace(versionId) || versionId == "null")
            throw new InvalidOperationException(
                "S3 did not return the immutable object version identifier.");
        try
        {
            var result = await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest
            {
                BucketName = bucket, Key = key, VersionId = versionId
            }, ct);
            RequireProtectedVersion(versionId,
                result.Retention?.Mode?.ToString(),
                result.Retention?.RetainUntilDate, observedUtc, minimumRemainingDays);
        }
        catch (AmazonServiceException)
        {
            throw new InvalidOperationException(
                "Cannot verify the newly uploaded S3 version's COMPLIANCE retention.");
        }
    }
}
