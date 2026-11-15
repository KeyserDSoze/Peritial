using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// This test is intentionally NOT backed by the demo SeaweedFS service:
/// the provider must actually enforce COMPLIANCE on version-specific DELETE.
/// The CI server is disposable; its locked data volume is destroyed only after
/// the whole container is removed, never by attempting to bypass Object Lock.
/// </summary>
public sealed class ObjectLockWormIntegrationTests
{
    [Fact]
    public async Task Real_S3_compliance_retains_each_version_and_restore_survives_delete_marker()
    {
        var endpoint = Environment.GetEnvironmentVariable("PERITIAL_TEST_WORM_S3_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
            return;
        Assert.True(OperatingSystem.IsLinux());

        var id = Guid.NewGuid().ToString("N");
        var bucket = "peritial-worm-" + id;
        var root = Path.Combine("/dev/shm", "peritial-worm-" + id);
        var sealedDirectory = Path.Combine(root, "encrypted");
        var restored = Path.Combine(root, "restored");
        var decrypted = Path.Combine(root, "decrypted");
        var archive = Path.Combine(root, "objects");
        var pg = Path.Combine(root, "peritial.dump");
        Directory.CreateDirectory(root, UnixFileMode.UserRead |
                                        UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var client = new AmazonS3Client(
            new BasicAWSCredentials("peritialworm", "local-only-test-worm-password"),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });

        try
        {
            // Creating the bucket without ObjectLockEnabledForBucket is a
            // permanent choice on some S3 providers: require it at creation.
            await client.PutBucketAsync(new PutBucketRequest
            {
                BucketName = bucket,
                ObjectLockEnabledForBucket = true
            });
            await client.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = bucket,
                VersioningConfig = new S3BucketVersioningConfig
                {
                    Status = VersionStatus.Enabled
                }
            });
            await client.PutObjectLockConfigurationAsync(
                new PutObjectLockConfigurationRequest
                {
                    BucketName = bucket,
                    ObjectLockConfiguration = new ObjectLockConfiguration
                    {
                        ObjectLockEnabled = new ObjectLockEnabled("Enabled"),
                        Rule = new ObjectLockRule
                        {
                            DefaultRetention = new DefaultRetention
                            {
                                Mode = ObjectLockRetentionMode.Compliance,
                                Days = 32
                            }
                        }
                    }
                });

            await OffsiteObjectLockPolicy.RequireCompliantBucketAsync(client, bucket);
            byte[] source = "PGDMP\0WORM-recovery-data\n"u8.ToArray();
            await File.WriteAllBytesAsync(pg, source);
            await File.WriteAllTextAsync(pg + ".sha256",
                Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant() + "\n");
            Directory.CreateDirectory(archive, UnixFileMode.UserRead |
                                               UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.Combine(archive, "objects"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var signingKey = RandomNumberGenerator.GetBytes(32);
            var encryptionKey = RandomNumberGenerator.GetBytes(32);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(
                new ObjectRecoveryArchive.ArchiveManifest(1,
                    new ObjectRecoveryCheckpoint.Manifest(1,
                        Array.Empty<ObjectRecoveryCheckpoint.Document>()),
                    Array.Empty<string>()));
            await File.WriteAllBytesAsync(Path.Combine(archive, "manifest.json"), manifest);
            using (var hmac = IncrementalHash.CreateHMAC(
                HashAlgorithmName.SHA256, signingKey))
            {
                hmac.AppendData("Peritial.S3Archive.Manifest.HMACSHA256.v1\n"u8);
                hmac.AppendData(manifest);
                await File.WriteAllBytesAsync(Path.Combine(archive, "manifest.hmac"),
                    hmac.GetHashAndReset());
            }
            await SealedRecoveryBundle.SealAsync(pg, archive, sealedDirectory,
                encryptionKey, signingKey);

            // NO demo bypass: both upload and download must check Object Lock.
            await SealedBundleOffsite.UploadAsync(sealedDirectory, client, bucket,
                id, encryptionKey);
            var completeKey = "sealed-v1/" + id + "/complete";
            var head = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket, Key = completeKey
            });
            Assert.False(string.IsNullOrWhiteSpace(head.VersionId));
            await OffsiteObjectLockPolicy.RequireProtectedObjectVersionAsync(
                client, bucket, completeKey, head.VersionId, DateTime.UtcNow);
            var firstVersion = head.VersionId;

            // The real provider must deny DELETE by protected version ID,
            // even with administrative root credentials.
            var delete = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucket, Key = completeKey,
                    VersionId = firstVersion
                }));
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);

            await SealedBundleOffsite.DownloadAsync(client, bucket, id,
                restored, encryptionKey);
            await SealedRecoveryBundle.UnsealAsync(restored, decrypted,
                encryptionKey, signingKey);
            Assert.Equal(source, await File.ReadAllBytesAsync(
                Path.Combine(decrypted, "postgres", "peritial.dump")));

            // A key-only DELETE is allowed to create a delete marker. It must
            // never invalidate the protected prior version, and the ordinary
            // production restore must refuse the no-longer-current key.
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket, Key = completeKey
            });
            await using (var original = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket, Key = completeKey,
                VersionId = firstVersion
            }))
            {
                using var markerBody = new MemoryStream();
                await original.ResponseStream.CopyToAsync(markerBody);
                Assert.Equal(32, markerBody.Length);
            }
            var newer = Path.Combine(root, "current-key-rejected");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.DownloadAsync(client, bucket, id,
                    newer, encryptionKey));
            Assert.False(Directory.Exists(newer));
        }
        finally
        {
            // COMPLIANCE versions cannot and must not be deleted by a client.
            // They disappear when the CI disposable MinIO container/volume
            // itself is destroyed after the test job.
            Directory.Delete(root, recursive: true);
        }
    }
}
