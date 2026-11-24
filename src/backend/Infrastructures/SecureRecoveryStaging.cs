namespace Peritial.Infrastructures;

/// <summary>
/// Fail-closed gate for intermediate PLAINTEXT recovery data on Linux.
/// Require a private (0700 or tighter) tmpfs mount and NO active swap before
/// accepting source PGDMP, local S3 blob archive or unseal destination.
/// Plain tmpfs can be swapped to disk: disabling swap is mandatory.
/// This is not a KMS, memory-dump or hostile-root defense.
/// </summary>
public static class SecureRecoveryStaging
{
    private const UnixFileMode SharedAccess =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public static void RequirePrivateTmpfsFile(string file)
    {
        if (!Path.IsPathFullyQualified(file) || !File.Exists(file) || IsSymlink(file))
            throw new InvalidOperationException("Plaintext recovery source file is missing or unsafe.");
        RequirePrivateTmpfsDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
    }

    public static void RequirePrivateTmpfsParent(string futurePath)
    {
        if (!Path.IsPathFullyQualified(futurePath))
            throw new InvalidOperationException("Plaintext staging requires an absolute tmpfs path.");
        RequirePrivateTmpfsDirectory(Path.GetDirectoryName(Path.GetFullPath(futurePath))!);
    }

    public static void RequirePrivateTmpfsDirectory(string directory)
    {
        // Never silently switch to a persistent filesystem on another OS.
        // Windows/macOS need a separately reviewed encrypted scratch backend.
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("Plaintext recovery staging requires Linux tmpfs.");

        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new InvalidOperationException("Plaintext recovery tmpfs directory is missing.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        // Prevent symlink traversal through any ancestor, not only the leaf.
        for (string? component = full; component is not null;
             component = Path.GetDirectoryName(component))
        {
            if (IsSymlink(component))
                throw new InvalidOperationException("Plaintext recovery tmpfs path contains a symlink.");
            if (component == Path.GetPathRoot(component))
                break;
        }

        if ((File.GetUnixFileMode(full) & SharedAccess) != 0)
            throw new InvalidOperationException(
                "Plaintext recovery staging directory must be private (0700 or tighter).");

        if (FindTmpfsMount(full) is null)
            throw new InvalidOperationException(
                "Plaintext recovery staging must use an actual Linux tmpfs mount.");

        // A tmpfs filesystem may write private pages to an unencrypted swap
        // device. Without a cryptographically proven encrypted swap setup,
        // the safe baseline is to refuse ANY active swap on the host.
        var swaps = File.ReadAllLines("/proc/swaps");
        if (swaps.Length == 0 || swaps.Skip(1).Any(x => !string.IsNullOrWhiteSpace(x)))
            throw new InvalidOperationException(
                "Plaintext recovery tmpfs requires host swap to be disabled.");
    }

    /// <summary>
    /// A point-in-time check of the actual destination mount. Each nonempty
    /// file consumes whole tmpfs pages, even when its logical length is tiny.
    /// This does not reserve space or guarantee inode or cgroup availability.
    /// </summary>
    internal static void RequireCapacityForFiles(string futurePath, IEnumerable<long> lengths)
    {
        RequirePrivateTmpfsParent(futurePath);
        var parent = Path.GetDirectoryName(Path.GetFullPath(futurePath))!;
        var mount = FindTmpfsMount(parent) ??
            throw new InvalidOperationException("Plaintext recovery tmpfs mount changed.");
        var available = new DriveInfo(mount).AvailableFreeSpace;
        RequirePageCapacity(lengths, available, Environment.SystemPageSize);
    }

    internal static void RequirePageCapacity(IEnumerable<long> lengths, long availableBytes,
        int pageBytes)
    {
        if (availableBytes < 0 || pageBytes <= 0)
            throw new InvalidOperationException("Plaintext recovery staging capacity is invalid.");
        // Compare in pages to avoid overflow while rounding or adding sizes.
        long remainingPages = availableBytes / pageBytes;
        foreach (var length in lengths)
        {
            if (length <= 0)
                throw new InvalidOperationException("Plaintext recovery file length is invalid.");
            var pages = (length - 1) / pageBytes + 1;
            if (pages > remainingPages)
                throw new InvalidOperationException("Insufficient free tmpfs staging capacity.");
            remainingPages -= pages;
        }
    }

    private static string? FindTmpfsMount(string fullPath)
    {
        // Linux mountinfo field 5 is mount-point, while the type is the first
        // field after ' - '. Prefer the MOST SPECIFIC mount (bind mounts have
        // separate entries). Do not infer tmpfs from '/dev/shm' string names.
        int maxLength = -1;
        string? actualFilesystem = null;
        string? actualMount = null;
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            int separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0) continue;
            var mountFields = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var filesystemFields = line[(separator + 3)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (mountFields.Length < 5 || filesystemFields.Length < 1) continue;
            var mountPoint = DecodeMountPath(mountFields[4]);
            if (mountPoint.Length > 1)
                mountPoint = mountPoint.TrimEnd('/');
            if (fullPath != mountPoint &&
                !fullPath.StartsWith(mountPoint == "/" ? "/" : mountPoint + "/",
                    StringComparison.Ordinal))
                continue;
            if (mountPoint.Length > maxLength)
            {
                maxLength = mountPoint.Length;
                actualFilesystem = filesystemFields[0];
                actualMount = mountPoint;
            }
        }
        return string.Equals(actualFilesystem, "tmpfs", StringComparison.Ordinal)
            ? actualMount : null;
    }

    private static string DecodeMountPath(string raw) => raw
        .Replace(@"\040", " ", StringComparison.Ordinal)
        .Replace(@"\011", "\t", StringComparison.Ordinal)
        .Replace(@"\012", "\n", StringComparison.Ordinal)
        .Replace(@"\134", @"\", StringComparison.Ordinal);

    private static bool IsSymlink(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
}
