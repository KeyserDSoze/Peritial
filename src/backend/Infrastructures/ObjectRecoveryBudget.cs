namespace Peritial.Infrastructures;

/// <summary>
/// Hard aggregate byte budget for offline checkpoint and S3 archive operations.
/// Each length is from the complete privileged PostgreSQL inventory or an
/// already authenticated, database-verified checkpoint. A low default avoids
/// exhausting the private tmpfs used to stage sensitive plaintext archives;
/// operators must explicitly provision and authorize larger export windows.
/// </summary>
public static class ObjectRecoveryBudget
{
    public const long DefaultMaxTotalBytes = 512L * 1024 * 1024;

    internal static long RequireWithin(IEnumerable<long> lengths, long maxTotalBytes)
    {
        if (maxTotalBytes <= 0)
            throw new InvalidOperationException("Offline recovery byte budget must be positive.");

        long total = 0;
        foreach (var length in lengths)
        {
            // Avoid overflow even for hostile manifests containing long.MaxValue
            // lengths. Never build a plaintext archive or start reading S3 when
            // the *sum across tenants* would exceed the configured capacity.
            if (length <= 0 || length > maxTotalBytes - total)
                throw new InvalidOperationException(
                    "Offline recovery total byte budget exceeded or metadata invalid.");
            total += length;
        }
        return total;
    }
}
