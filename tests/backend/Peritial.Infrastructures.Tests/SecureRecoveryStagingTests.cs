using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// Plaintext PGDMP and S3 recovery bytes must NEVER use disk-backed scratch.
/// Linux tmpfs is allowed only with host swap disabled and private 0700 leaf.
/// </summary>
public sealed class SecureRecoveryStagingTests
{
    [Theory]
    [InlineData(4096, 1, 4096)]
    [InlineData(8192, 4097, 8192)]
    [InlineData(8192, 4096, 4096)]
    public void Page_capacity_accepts_exact_rounded_size(long free, long first, long second)
    {
        SecureRecoveryStaging.RequirePageCapacity(new[] { first }, free, 4096);
        SecureRecoveryStaging.RequirePageCapacity(new[] { second }, free, 4096);
    }

    [Fact]
    public void Tiny_files_are_charged_separate_pages()
    {
        SecureRecoveryStaging.RequirePageCapacity(new long[] { 1, 1 }, 8192, 4096);
        Assert.Throws<InvalidOperationException>(() =>
            SecureRecoveryStaging.RequirePageCapacity(new long[] { 1, 1 }, 8191, 4096));
        Assert.Throws<InvalidOperationException>(() =>
            SecureRecoveryStaging.RequirePageCapacity(new long[] { 4097, 1 }, 8192, 4096));
    }

    [Fact]
    public void Invalid_capacity_and_lengths_cannot_wrap_or_bypass_the_gate()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SecureRecoveryStaging.RequirePageCapacity(new[] { long.MaxValue },
                long.MaxValue, 4096));
        Assert.Throws<InvalidOperationException>(() =>
            SecureRecoveryStaging.RequirePageCapacity(new long[] { 0 }, 8192, 4096));
        Assert.Throws<InvalidOperationException>(() =>
            SecureRecoveryStaging.RequirePageCapacity(new long[] { -1 }, 8192, 4096));
        Assert.Throws<InvalidOperationException>(() =>
            SecureRecoveryStaging.RequirePageCapacity(Array.Empty<long>(), -1, 4096));
        Assert.Throws<InvalidOperationException>(() =>
            SecureRecoveryStaging.RequirePageCapacity(Array.Empty<long>(), 8192, 0));
    }

    [Fact]
    public void Private_tmpfs_accepts_only_safe_plaintext_paths()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm"))
            return;

        var root = Path.Combine("/dev/shm", "peritial-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead |
                                       UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            SecureRecoveryStaging.RequirePrivateTmpfsDirectory(root);
            var future = Path.Combine(root, "new-unseal");
            SecureRecoveryStaging.RequirePrivateTmpfsParent(future);
            SecureRecoveryStaging.RequireCapacityForFiles(future, new long[] { 1, 32 });
            var capacityError = Assert.Throws<InvalidOperationException>(() =>
                SecureRecoveryStaging.RequireCapacityForFiles(future, new[] { long.MaxValue }));
            Assert.Contains("capacity", capacityError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(future));
            var pg = Path.Combine(root, "peritial.dump");
            File.WriteAllBytes(pg, "PGDMP"u8.ToArray());
            SecureRecoveryStaging.RequirePrivateTmpfsFile(pg);

            // An archive's leaf directory itself must remain private.
            var publicDirectory = Path.Combine(root, "public");
            Directory.CreateDirectory(publicDirectory);
            File.SetUnixFileMode(publicDirectory, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Assert.Throws<InvalidOperationException>(() =>
                SecureRecoveryStaging.RequirePrivateTmpfsDirectory(publicDirectory));
            Assert.Throws<InvalidOperationException>(() =>
                SecureRecoveryStaging.RequirePrivateTmpfsParent(
                    Path.Combine(publicDirectory, "plaintext")));

            var link = Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(link, publicDirectory);
            Assert.Throws<InvalidOperationException>(() =>
                SecureRecoveryStaging.RequirePrivateTmpfsDirectory(link));
            Assert.Throws<InvalidOperationException>(() =>
                SecureRecoveryStaging.RequirePrivateTmpfsParent(
                    Path.Combine(link, "plaintext")));

            // A writable directory on the ordinary disk is NEVER acceptable,
            // even when its owner permissions are correctly restricted.
            var ordinary = Path.Combine(Path.GetTempPath(),
                "peritial-disk-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ordinary, UnixFileMode.UserRead |
                                                UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                var message = Assert.Throws<InvalidOperationException>(() =>
                    SecureRecoveryStaging.RequirePrivateTmpfsParent(
                        Path.Combine(ordinary, "unsafe-plaintext"))).Message;
                Assert.Contains("tmpfs", message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(ordinary);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_sealed_bundle_must_reject_disk_backed_plaintext_even_with_correct_keys()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var root = Path.Combine(Path.GetTempPath(),
            "peritial-unseal-disk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead |
                                       UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var encryptionKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var s3Key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedRecoveryBundle.UnsealAsync(
                    Path.Combine(root, "not-even-a-real-bundle"),
                    Path.Combine(root, "unsafe-output"), encryptionKey, s3Key));
            Assert.Contains("tmpfs", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(root, "unsafe-output")));

            // Check enforcement at the object-archive API boundary as well.
            // No network/DB access is permitted before the scratch guard.
            var exportError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ObjectRecoveryArchive.ExportAsync(
                    null!, null!, "unused-bucket",
                    Path.Combine(root, "unsafe-s3-archive"),
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            Assert.Contains("tmpfs", exportError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(root, "unsafe-s3-archive")));

            // Even a correctly keyed seal must not read a disk-backed PGDMP.
            var pg = Path.Combine(root, "source.dump");
            await File.WriteAllBytesAsync(pg, "PGDMP"u8.ToArray());
            var sealError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedRecoveryBundle.SealAsync(
                    pg, Path.Combine(root, "s3-plaintext"),
                    Path.Combine(root, "sealed-output"),
                    encryptionKey, s3Key));
            Assert.Contains("tmpfs", sealError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(root, "sealed-output")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
