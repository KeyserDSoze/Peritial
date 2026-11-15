using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class SealedBundleOffsiteContractTests
{
    [Fact]
    public async Task Remote_sealed_replica_requires_authenticated_complete_set_and_new_destination()
    {
        var endpoint = Environment.GetEnvironmentVariable("PERITIAL_TEST_OFFSITE_S3_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        if (!OperatingSystem.IsLinux()) return;

        var id = Guid.NewGuid().ToString("N");
        var incompleteId = Guid.NewGuid().ToString("N");
        var replayId = Guid.NewGuid().ToString("N");
        var scratch = Path.Combine("/dev/shm", "peritial-offsite-" + id);
        var disk = Path.Combine(Path.GetTempPath(), "peritial-offsite-encrypted-" + id);
        Directory.CreateDirectory(scratch, UnixFileMode.UserRead |
                                           UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(disk);
        var pg = Path.Combine(scratch, "peritial.dump");
        var archive = Path.Combine(scratch, "objects");
        var encrypted = Path.Combine(disk, "sealed");
        var retrieved = Path.Combine(disk, "retrieved");
        var unsealed = Path.Combine(scratch, "reopened");
        var aesKey = RandomNumberGenerator.GetBytes(32);
        var s3Key = RandomNumberGenerator.GetBytes(32);
        const string bucket = "peritial";
        using var client = new AmazonS3Client(
            new BasicAWSCredentials("localminio", "local-only-minio-secret"),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });

        try
        {
            byte[] dump = "PGDMP\0encrypted offsite smoke\n"u8.ToArray();
            await File.WriteAllBytesAsync(pg, dump);
            await File.WriteAllTextAsync(pg + ".sha256",
                Convert.ToHexString(SHA256.HashData(dump)).ToLowerInvariant() + "\n");
            Directory.CreateDirectory(archive, UnixFileMode.UserRead |
                                               UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.Combine(archive, "objects"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(
                new ObjectRecoveryArchive.ArchiveManifest(1,
                    new ObjectRecoveryCheckpoint.Manifest(1,
                        Array.Empty<ObjectRecoveryCheckpoint.Document>()),
                    Array.Empty<string>()));
            await File.WriteAllBytesAsync(Path.Combine(archive, "manifest.json"), manifest);
            using (var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, s3Key))
            {
                h.AppendData("Peritial.S3Archive.Manifest.HMACSHA256.v1\n"u8);
                h.AppendData(manifest);
                await File.WriteAllBytesAsync(Path.Combine(archive, "manifest.hmac"),
                    h.GetHashAndReset());
            }

            await SealedRecoveryBundle.SealAsync(pg, archive, encrypted, aesKey, s3Key);
            // Fail closed by default: SeaweedFS CI has NO verified S3 Object
            // Lock COMPLIANCE retention. A production upload must write nothing.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.UploadAsync(encrypted, client, bucket, id, aesKey,
                    disposableUnprotectedDemo: true));
            var before = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket, Prefix = "sealed-v1/" + id + "/"
            });
            Assert.True(before.S3Objects is null || before.S3Objects.Count == 0);
            // Only this explicitly disposable CI server can use the opt-out.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.UploadAsync(encrypted, client, bucket, id,
                    RandomNumberGenerator.GetBytes(32), disposableUnprotectedDemo: true));
            await SealedBundleOffsite.UploadAsync(
                encrypted, client, bucket, id, aesKey, disposableUnprotectedDemo: true);

            // Production download also fails closed without proven Object Lock.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.DownloadAsync(client, bucket, id, retrieved, aesKey));
            Assert.False(Directory.Exists(retrieved));
            // The disposable demo still rejects existing destination keys.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.UploadAsync(encrypted, client, bucket, id, aesKey,
                    disposableUnprotectedDemo: true));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.DownloadAsync(client, bucket, id, retrieved,
                    RandomNumberGenerator.GetBytes(32), disposableUnprotectedDemo: true));
            Assert.False(Directory.Exists(retrieved));

            // A copied bundle cannot impersonate another backup ID even when
            // all ciphertext files and their SHA-256 digests still match.
            var originalPrefix = "sealed-v1/" + id + "/";
            var replayPrefix = "sealed-v1/" + replayId + "/";
            var listing = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket, Prefix = originalPrefix
            });
            foreach (var item in listing.S3Objects ?? [])
            {
                using var from = await client.GetObjectAsync(bucket, item.Key);
                await using var copied = new MemoryStream();
                await from.ResponseStream.CopyToAsync(copied);
                copied.Position = 0;
                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = replayPrefix + item.Key[originalPrefix.Length..],
                    InputStream = copied,
                    AutoCloseStream = false,
                    IfNoneMatch = "*"
                });
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.DownloadAsync(client, bucket, replayId, retrieved, aesKey, disposableUnprotectedDemo: true));
            Assert.False(Directory.Exists(retrieved));

            // An incomplete upload without the last-write completion marker
            // is not a valid remote backup and must never publish local files.
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = "sealed-v1/" + incompleteId + "/index.json",
                ContentBody = "incomplete", IfNoneMatch = "*"
            });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.DownloadAsync(client, bucket, incompleteId,
                    retrieved, aesKey, disposableUnprotectedDemo: true));
            Assert.False(Directory.Exists(retrieved));

            // Same-length ciphertext corruption after the remote upload is
            // caught using the authenticated transfer inventory.
            var remoteCipher = "sealed-v1/" + id + "/payload/00000000.enc";
            using var remote = await client.GetObjectAsync(bucket, remoteCipher);
            await using var saved = new MemoryStream();
            await remote.ResponseStream.CopyToAsync(saved);
            var clean = saved.ToArray();
            var damaged = clean.ToArray();
            damaged[0] ^= 0x20;
            await PutAsync(client, bucket, remoteCipher, damaged);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.DownloadAsync(client, bucket, id, retrieved, aesKey, disposableUnprotectedDemo: true));
            Assert.False(Directory.Exists(retrieved));
            await PutAsync(client, bucket, remoteCipher, clean);

            await SealedBundleOffsite.DownloadAsync(client, bucket, id, retrieved, aesKey, disposableUnprotectedDemo: true);
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(encrypted, "payload", "00000000.enc")),
                await File.ReadAllBytesAsync(Path.Combine(retrieved, "payload", "00000000.enc")));
            await SealedRecoveryBundle.UnsealAsync(retrieved, unsealed, aesKey, s3Key);
            Assert.Equal(dump,
                await File.ReadAllBytesAsync(Path.Combine(unsealed, "postgres", "peritial.dump")));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.DownloadAsync(client, bucket, id, retrieved, aesKey, disposableUnprotectedDemo: true));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SealedBundleOffsite.UploadAsync(encrypted, client, bucket, "../unsafe", aesKey,
                    disposableUnprotectedDemo: true));
        }
        finally
        {
            foreach (var prefix in new[] { id, incompleteId, replayId })
            {
                string token = null!;
                do
                {
                    var list = await client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = bucket,
                        Prefix = "sealed-v1/" + prefix + "/",
                        ContinuationToken = token
                    });
                    foreach (var obj in list.S3Objects ?? [])
                        await client.DeleteObjectAsync(bucket, obj.Key);
                    token = list.IsTruncated == true ? list.NextContinuationToken : null!;
                } while (token is not null);
            }
            Directory.Delete(scratch, true);
            Directory.Delete(disk, true);
        }
    }

    private static async Task PutAsync(IAmazonS3 client, string bucket, string key, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = key, InputStream = stream,
            AutoCloseStream = false
        });
    }
}
