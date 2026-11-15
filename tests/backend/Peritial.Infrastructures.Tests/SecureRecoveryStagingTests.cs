using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// Plaintext PGDMP and S3 recovery bytes must NEVER use disk-backed scratch.
/// Linux tmpfs is allowed only with host swap disabled and private 0700 leaf.
/// </summary>
public sealed class SecureRecoveryStagingTests
{
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
            SecureRecoveryStaging.RequirePrivateTmpfsParent(Path.Combine(root, "new-unseal"));
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
        }
        finally
        {
            Directory.Delete(root);
        }
    }
}
