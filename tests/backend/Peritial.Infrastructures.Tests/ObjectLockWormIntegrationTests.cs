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
        if (!OperatingSystem.IsLinux())
            return;

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
            // Include a real multi-chunk encrypted document, not only PGDMP.
            byte[] blob = RandomNumberGenerator.GetBytes(1_200_000);
            await File.WriteAllBytesAsync(
                Path.Combine(archive, "objects", "00000000.blob"), blob);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(
                new ObjectRecoveryArchive.ArchiveManifest(1,
                    new ObjectRecoveryCheckpoint.Manifest(1,
                        new[] { new ObjectRecoveryCheckpoint.Document(
                            Guid.NewGuid(), Guid.NewGuid(), "tenant/locked.pdf",
                            blob.LongLength,
                            Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant()) }),
                    new[] { "application/pdf" }));
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
            Assert.True(
                delete.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest,
                "Locked object DELETE returned unexpected HTTP " +
                (int)delete.StatusCode + " / " + delete.ErrorCode);
            // Positive control: the SAME S3 server must support deleting a
            // version of an ordinary versioned (non Object Lock) bucket.
            // This rules out a 400 caused simply by broken DELETE support.
            var controlBucket = "peritial-control-" + id;
            await client.PutBucketAsync(new PutBucketRequest
            {
                BucketName = controlBucket
            });
            await client.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = controlBucket,
                VersioningConfig = new S3BucketVersioningConfig
                {
                    Status = VersionStatus.Enabled
                }
            });
            var unprotected = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = controlBucket,
                Key = "control.bin",
                ContentBody = "control data"
            });
            Assert.False(string.IsNullOrWhiteSpace(unprotected.VersionId));
            var permittedDelete = await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = controlBucket,
                Key = "control.bin",
                VersionId = unprotected.VersionId
            });
            Assert.True((int)permittedDelete.HttpStatusCode is >= 200 and < 300);
            // Confirm that the protected version remains retained.
            await OffsiteObjectLockPolicy.RequireProtectedObjectVersionAsync(
                client, bucket, completeKey, firstVersion, DateTime.UtcNow, default, 0);

            await SealedBundleOffsite.DownloadAsync(client, bucket, id,
                restored, encryptionKey);
            await SealedRecoveryBundle.UnsealAsync(restored, decrypted,
                encryptionKey, signingKey);
            Assert.Equal(source, await File.ReadAllBytesAsync(
                Path.Combine(decrypted, "postgres", "peritial.dump")));
            Assert.Equal(blob, await File.ReadAllBytesAsync(
                Path.Combine(decrypted, "s3", "objects", "00000000.blob")));

            // A key-only DELETE is allowed to create a delete marker. It must
            // never invalidate the protected prior version, and the ordinary
            // production restore must refuse the no-longer-current key.
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket, Key = completeKey
            });
            using (var original = await client.GetObjectAsync(new GetObjectRequest
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

            // Also mask one ciphertext file, not just 'complete'. A coherent
            // historical restore MUST pin EVERY encrypted object version.
            var payloadKey = "sealed-v1/" + id + "/payload/00000004.enc";
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket, Key = payloadKey
            });
            var versions = await client.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucket, Prefix = "sealed-v1/" + id + "/",
                MaxKeys = 100
            });
            Assert.False(versions.IsTruncated);
            var selected = versions.Versions
                .Where(v => !v.IsDeleteMarker)
                .ToDictionary(v => v.Key[("sealed-v1/" + id + "/").Length..],
                    v => v.VersionId, StringComparer.Ordinal);
            Assert.Contains("complete", selected.Keys);
            Assert.Contains("payload/00000004.enc", selected.Keys);
            Assert.True(versions.Versions.Any(v =>
                v.Key == completeKey && v.IsDeleteMarker));
            Assert.True(versions.Versions.Any(v =>
                v.Key == payloadKey && v.IsDeleteMarker));

            // Operators must supply a strict, complete mapping for the CLI.
            // It is a selector, NOT the integrity proof: the transfer HMAC
            // and GCM tags authenticate all recovered ciphertext separately.
            var selectionPath = Path.Combine(root, "historical-versions.json");
            await File.WriteAllBytesAsync(selectionPath,
                JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Versions = selected }));
            var parsed = SealedBundleOffsite.LoadHistoricalVersionSelection(selectionPath);
            Assert.Equal(selected.Count, parsed.Count);
            Assert.Equal(selected["complete"], parsed["complete"]);
            var invalidSelection = Path.Combine(root, "invalid-versions.json");
            await File.WriteAllTextAsync(invalidSelection,
                "{\"Version\":1,\"Versions\":{\"complete\":\"v1\",\"complete\":\"v2\"}}");
            Assert.Throws<InvalidOperationException>(() =>
                SealedBundleOffsite.LoadHistoricalVersionSelection(invalidSelection));
            await File.WriteAllTextAsync(invalidSelection,
                "{\"Version\":1,\"Versions\":{\"../escape\":\"v1\"}}");
            Assert.Throws<InvalidOperationException>(() =>
                SealedBundleOffsite.LoadHistoricalVersionSelection(invalidSelection));

            var missing = new Dictionary<string, string>(selected,
                StringComparer.Ordinal);
            missing.Remove("complete");
            var incompleteDir = Path.Combine(root, "historical-missing");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.DownloadHistoricalAsync(client, bucket, id,
                    incompleteDir, encryptionKey, missing));
            Assert.False(Directory.Exists(incompleteDir));

            var tampered = new Dictionary<string, string>(selected,
                StringComparer.Ordinal);
            tampered["complete"] = selected["index.json"];
            var tamperedDir = Path.Combine(root, "historical-forged");
            await Assert.ThrowsAnyAsync<Exception>(() =>
                SealedBundleOffsite.DownloadHistoricalAsync(client, bucket, id,
                    tamperedDir, encryptionKey, tampered));
            Assert.False(Directory.Exists(tamperedDir));

            var wrongKeyDir = Path.Combine(root, "historical-wrong-key");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.DownloadHistoricalAsync(client, bucket, id,
                    wrongKeyDir, RandomNumberGenerator.GetBytes(32), selected));
            Assert.False(Directory.Exists(wrongKeyDir));

            var historical = Path.Combine(root, "historical-encrypted");
            var historicPlain = Path.Combine(root, "historical-plain");
            await SealedBundleOffsite.DownloadHistoricalAsync(client, bucket, id,
                historical, encryptionKey, parsed);
            await SealedRecoveryBundle.UnsealAsync(historical, historicPlain,
                encryptionKey, signingKey);
            Assert.Equal(source, await File.ReadAllBytesAsync(
                Path.Combine(historicPlain, "postgres", "peritial.dump")));
            Assert.Equal(blob, await File.ReadAllBytesAsync(
                Path.Combine(historicPlain, "s3", "objects", "00000000.blob")));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.DownloadHistoricalAsync(client, bucket, id,
                    historical, encryptionKey, selected));
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
