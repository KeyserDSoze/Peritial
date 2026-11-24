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
    public const int DefaultMaxObjects = 10_000;
    // A hard upper bound prevents a mistaken operator override from
    // materializing millions of tiny rows in RAM or a huge JSON manifest.
    public const int MaxSupportedObjects = 100_000;

    internal static void RequireCountLimit(int maxObjects)
    {
        if (maxObjects is <= 0 or > MaxSupportedObjects)
            throw new InvalidOperationException("Offline recovery object count budget is invalid.");
    }

    internal static long RequireWithin(
        IEnumerable<long> lengths, long maxTotalBytes,
        int maxObjects = DefaultMaxObjects)
    {
        if (maxTotalBytes <= 0)
            throw new InvalidOperationException("Offline recovery byte budget must be positive.");
        RequireCountLimit(maxObjects);

        long total = 0;
        int count = 0;
        foreach (var length in lengths)
        {
            // A large population of tiny documents can exhaust memory and
            // manifest capacity despite a valid total-byte quota.
            if (count >= maxObjects)
                throw new InvalidOperationException(
                    "Offline recovery object count budget exceeded.");
            count++;
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
