using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// Security regression contract for a PRIVATE, offline multi-service bundle.
/// All fixtures are local fake data; the Compose drill separately exercises
/// real pg_dump/pg_restore and two separate SeaweedFS servers.
/// </summary>
public sealed class SealedRecoveryBundleTests
{
    [Fact]
    public async Task Both_Postgres_and_S3_bytes_are_sealed_and_restored_after_authentication()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm"))
            return;
        var baseDir = Path.Combine("/dev/shm", "sealed-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir, UnixFileMode.UserRead |
            UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var sourceDump = Path.Combine(baseDir, "source.dump");
            var s3Archive = Path.Combine(baseDir, "source-objects");
            var sealedDir = Path.Combine(baseDir, "sealed");
            var restored = Path.Combine(baseDir, "restored");
            var encryptionKey = RandomNumberGenerator.GetBytes(32);
            var signingKey = RandomNumberGenerator.GetBytes(32);
            byte[] postgres = new byte[1_200_000];
            byte[] blob = new byte[2_400_000];
            RandomNumberGenerator.Fill(postgres);
            RandomNumberGenerator.Fill(blob);
            "PGDMP"u8.CopyTo(postgres);
            await File.WriteAllBytesAsync(sourceDump, postgres);
            await File.WriteAllTextAsync(sourceDump + ".sha256",
                Convert.ToHexString(SHA256.HashData(postgres)).ToLowerInvariant() + "\n");
            Directory.CreateDirectory(s3Archive, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.Combine(s3Archive, "objects"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllBytesAsync(Path.Combine(s3Archive, "objects", "00000000.blob"), blob);

            var checkpoint = new ObjectRecoveryCheckpoint.Manifest(1,
                new[] { new ObjectRecoveryCheckpoint.Document(
                    Guid.NewGuid(), Guid.NewGuid(), "tenant/important.pdf",
                    blob.LongLength, Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant()) });
            var index = new ObjectRecoveryArchive.ArchiveManifest(
                1, checkpoint, new[] { "application/pdf" });
            var raw = JsonSerializer.SerializeToUtf8Bytes(index);
            await File.WriteAllBytesAsync(Path.Combine(s3Archive, "manifest.json"), raw);
            using (var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, signingKey))
            {
                mac.AppendData("Peritial.S3Archive.Manifest.HMACSHA256.v1\n"u8);
                mac.AppendData(raw);
                await File.WriteAllBytesAsync(
                    Path.Combine(s3Archive, "manifest.hmac"), mac.GetHashAndReset());
            }

            // No general recovery operation is permitted without TWO
            // independent secret keys of exactly 32 bytes.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedRecoveryBundle.SealAsync(
                    sourceDump, s3Archive, sealedDir, Array.Empty<byte>(), signingKey));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedRecoveryBundle.SealAsync(
                    sourceDump, s3Archive, sealedDir, signingKey, signingKey));

            await SealedRecoveryBundle.SealAsync(
                sourceDump, s3Archive, sealedDir, encryptionKey, signingKey);
            Assert.False(File.Exists(Path.Combine(sealedDir, "postgres", "peritial.dump")));
            Assert.False(Directory.Exists(Path.Combine(sealedDir, "s3")));
            Assert.False(Directory.Exists(Path.Combine(sealedDir, "source-objects")));
            Assert.True(File.Exists(Path.Combine(sealedDir, "index.hmac")));
            Assert.Equal(5, Directory.GetFiles(Path.Combine(sealedDir, "payload")).Length);

            // Wrong password/key and corrupt ciphertext must not publish even
            // a partial plaintext restore directory.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedRecoveryBundle.UnsealAsync(
                    sealedDir, restored, RandomNumberGenerator.GetBytes(32), signingKey));
            Assert.False(Directory.Exists(restored));

            var encrypted = Path.Combine(sealedDir, "payload", "00000004.enc");
            var originalCipher = await File.ReadAllBytesAsync(encrypted);
            var brokenCipher = originalCipher.ToArray();
            brokenCipher[500] ^= 0x20;
            await File.WriteAllBytesAsync(encrypted, brokenCipher);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedRecoveryBundle.UnsealAsync(
                    sealedDir, restored, encryptionKey, signingKey));
            Assert.False(Directory.Exists(restored));
            await File.WriteAllBytesAsync(encrypted, originalCipher);

            var signedIndex = Path.Combine(sealedDir, "index.json");
            var originalIndex = await File.ReadAllBytesAsync(signedIndex);
            var modifiedIndex = originalIndex.ToArray();
            modifiedIndex[^5] ^= 0x01;
            await File.WriteAllBytesAsync(signedIndex, modifiedIndex);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                SealedRecoveryBundle.UnsealAsync(
                    sealedDir, restored, encryptionKey, signingKey));
            Assert.False(Directory.Exists(restored));
            await File.WriteAllBytesAsync(signedIndex, originalIndex);

            // If the detached object HMAC is compromised independently, the
            // sealed bundle must still refuse to publish restored plaintext.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedRecoveryBundle.UnsealAsync(
                    sealedDir, restored, encryptionKey, RandomNumberGenerator.GetBytes(32)));
            Assert.False(Directory.Exists(restored));

            await SealedRecoveryBundle.UnsealAsync(
                sealedDir, restored, encryptionKey, signingKey);
            Assert.Equal(postgres, await File.ReadAllBytesAsync(
                Path.Combine(restored, "postgres", "peritial.dump")));
            Assert.Equal(blob, await File.ReadAllBytesAsync(
                Path.Combine(restored, "s3", "objects", "00000000.blob")));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedRecoveryBundle.UnsealAsync(
                    sealedDir, restored, encryptionKey, signingKey));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task Unauthenticated_postgres_digest_or_s3_manifest_cannot_be_sealed()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm"))
            return;
        var baseDir = Path.Combine("/dev/shm", "sealed-reject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir, UnixFileMode.UserRead |
            UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var pg = Path.Combine(baseDir, "db.dump");
            await File.WriteAllBytesAsync(pg, "PGDMPnot-a-real-dump-for-format-check"u8.ToArray());
            await File.WriteAllTextAsync(pg + ".sha256", new string('0', 64) + "\n");
            var archive = Path.Combine(baseDir, "s3");
            Directory.CreateDirectory(archive, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var bundle = Path.Combine(baseDir, "bundle");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedRecoveryBundle.SealAsync(
                    pg, archive, bundle,
                    RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32)));
            Assert.False(Directory.Exists(bundle));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }
}
