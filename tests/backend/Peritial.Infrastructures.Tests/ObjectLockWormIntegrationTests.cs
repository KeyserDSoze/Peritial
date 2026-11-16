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
        // Single fresh disposable S3 instance per CI runner.
        const string bucket = "peritial-worm-ci";
        var root = Path.Combine("/dev/shm", "peritial-worm-" + id);
        var sealedDirectory = Path.Combine(root, "encrypted");
        var restored = Path.Combine(root, "restored");
        var decrypted = Path.Combine(root, "decrypted");
        var archive = Path.Combine(root, "objects");
        var pg = Path.Combine(root, "peritial.dump");
        Directory.CreateDirectory(root, UnixFileMode.UserRead |
                                        UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var config = new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true };
        using var client = new AmazonS3Client(
            new BasicAWSCredentials("peritialworm", "local-only-test-worm-password"),
            config); // admin only for bucket setup and controlled DELETE comparisons
        var writerSecret = Environment.GetEnvironmentVariable("PERITIAL_TEST_WORM_WRITER_SECRET")
            ?? throw new InvalidOperationException("Scoped writer credentials required.");
        var readerSecret = Environment.GetEnvironmentVariable("PERITIAL_TEST_WORM_READER_SECRET")
            ?? throw new InvalidOperationException("Scoped reader credentials required.");
        using var writer = new AmazonS3Client(
            new BasicAWSCredentials("peritial-writer-ci", writerSecret), config);
        using var reader = new AmazonS3Client(
            new BasicAWSCredentials("peritial-reader-ci", readerSecret), config);

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
            await SealedBundleOffsite.UploadAsync(sealedDirectory, writer, bucket,
                id, encryptionKey, verificationSource: reader);
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
            // IAM failure checks against an UNPROTECTED version ensure that
            // a denied delete isn't merely COMPLIANCE's lock rejecting it.
            await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                reader.DeleteObjectAsync(new DeleteObjectRequest
                { BucketName = controlBucket, Key = "control.bin",
                  VersionId = unprotected.VersionId }));
            await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                writer.DeleteObjectAsync(new DeleteObjectRequest
                { BucketName = controlBucket, Key = "control.bin",
                  VersionId = unprotected.VersionId }));
            await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                reader.PutObjectAsync(new PutObjectRequest
                { BucketName = bucket, Key = "sealed-v1/" + id + "/forbidden.bin",
                  ContentBody = "must not appear" }));
            await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                writer.PutBucketVersioningAsync(new PutBucketVersioningRequest
                {
                    BucketName = bucket,
                    VersioningConfig = new S3BucketVersioningConfig
                    { Status = VersionStatus.Suspended }
                }));
            // SILO conflates GetObject and GetObjectVersion. The uploader
            // therefore receives NO GET permission whatsoever; a separate
            // reader is used to verify the uploaded bytes. Both reads fail.
            await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                writer.GetObjectAsync(bucket, completeKey));
            await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                writer.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = bucket, Key = completeKey,
                    VersionId = firstVersion
                }));
            // Bucket-name isolation is not sufficient: neither identity may
            // read or upload outside sealed-v1/* even inside the same bucket.
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = "unrelated-private.txt",
                ContentBody = "outside encrypted backup namespace"
            });
            await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                reader.GetObjectAsync(bucket, "unrelated-private.txt"));
            await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                writer.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket, Key = "unrelated-upload.txt",
                    ContentBody = "must be rejected"
                }));
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

            await SealedBundleOffsite.DownloadAsync(reader, bucket, id,
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
                SealedBundleOffsite.DownloadAsync(reader, bucket, id,
                    newer, encryptionKey));
            Assert.False(Directory.Exists(newer));

            // Also mask one ciphertext file, not just 'complete'. A coherent
            // historical restore MUST pin EVERY encrypted object version.
            var payloadKey = "sealed-v1/" + id + "/payload/00000004.enc";
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket, Key = payloadKey
            });
            var versions = await reader.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucket, Prefix = "sealed-v1/" + id + "/",
                MaxKeys = 100
            });
            Assert.False(versions.IsTruncated);
            var selected = versions.Versions
                .Where(v => v.IsDeleteMarker != true)
                .ToDictionary(v => v.Key[("sealed-v1/" + id + "/").Length..],
                    v => v.VersionId, StringComparer.Ordinal);
            Assert.Contains("complete", selected.Keys);
            Assert.Contains("payload/00000004.enc", selected.Keys);
            Assert.True(versions.Versions.Any(v =>
                v.Key == completeKey && v.IsDeleteMarker == true));
            Assert.True(versions.Versions.Any(v =>
                v.Key == payloadKey && v.IsDeleteMarker == true));

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
                SealedBundleOffsite.DownloadHistoricalAsync(reader, bucket, id,
                    incompleteDir, encryptionKey, missing));
            Assert.False(Directory.Exists(incompleteDir));

            var tampered = new Dictionary<string, string>(selected,
                StringComparer.Ordinal);
            tampered["complete"] = selected["index.json"];
            var tamperedDir = Path.Combine(root, "historical-forged");
            await Assert.ThrowsAnyAsync<Exception>(() =>
                SealedBundleOffsite.DownloadHistoricalAsync(reader, bucket, id,
                    tamperedDir, encryptionKey, tampered));
            Assert.False(Directory.Exists(tamperedDir));

            var wrongKeyDir = Path.Combine(root, "historical-wrong-key");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.DownloadHistoricalAsync(reader, bucket, id,
                    wrongKeyDir, RandomNumberGenerator.GetBytes(32), selected));
            Assert.False(Directory.Exists(wrongKeyDir));

            var historical = Path.Combine(root, "historical-encrypted");
            var historicPlain = Path.Combine(root, "historical-plain");
            await SealedBundleOffsite.DownloadHistoricalAsync(reader, bucket, id,
                historical, encryptionKey, parsed);
            await SealedRecoveryBundle.UnsealAsync(historical, historicPlain,
                encryptionKey, signingKey);
            Assert.Equal(source, await File.ReadAllBytesAsync(
                Path.Combine(historicPlain, "postgres", "peritial.dump")));
            Assert.Equal(blob, await File.ReadAllBytesAsync(
                Path.Combine(historicPlain, "s3", "objects", "00000000.blob")));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.DownloadHistoricalAsync(reader, bucket, id,
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
