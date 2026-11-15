using System.Security.Cryptography;
using System.Text.Json;

namespace Peritial.Infrastructures;

public static partial class SealedRecoveryBundle
{
    /// <summary>
    /// Verify the HMAC-authenticated index and *every* AES-GCM ciphertext tag,
    /// without publishing or persisting any plaintext. Only canonical known
    /// file paths are returned, for transfer of an encrypted bundle.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ValidateEncryptedAsync(
        string directory, byte[] encryptionKey, CancellationToken ct = default)
    {
        if (encryptionKey is not { Length: 32 })
            throw new InvalidOperationException("A 32-byte encrypted bundle key is required.");
        if (!Path.IsPathFullyQualified(directory) ||
            !Directory.Exists(directory) || IsLink(directory))
            throw new InvalidOperationException("Encrypted bundle directory is missing or unsafe.");

        var manifestPath = Path.Combine(directory, IndexName);
        var signaturePath = Path.Combine(directory, IndexMacName);
        var payloadPath = Path.Combine(directory, PayloadDirectory);
        CheckRegularFile(manifestPath);
        CheckRegularFile(signaturePath);
        if (!Directory.Exists(payloadPath) || IsLink(payloadPath) ||
            new FileInfo(manifestPath).Length is <= 0 or > MaxIndexBytes ||
            new FileInfo(signaturePath).Length != 32 ||
            Directory.EnumerateFileSystemEntries(directory).Count() != 3)
            throw new InvalidOperationException("Encrypted bundle structure is unsafe.");

        var indexBytes = await File.ReadAllBytesAsync(manifestPath, ct);
        BundleIndex? index;
        try { index = JsonSerializer.Deserialize<BundleIndex>(indexBytes); }
        catch (JsonException) { throw new InvalidOperationException("Encrypted bundle index is malformed."); }
        if (index is null || index.Version != 1 || index.Salt?.Length != 44)
            throw new InvalidOperationException("Unsupported encrypted bundle index.");

        byte[] salt;
        try { salt = Convert.FromBase64String(index.Salt); }
        catch (FormatException) { throw new InvalidOperationException("Invalid encrypted bundle salt."); }
        if (salt.Length != 32)
            throw new InvalidOperationException("Invalid encrypted bundle salt.");
        try
        {
            var expected = HMACSHA256.HashData(DeriveKey(encryptionKey, salt, -1), indexBytes);
            var actual = await File.ReadAllBytesAsync(signaturePath, ct);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                throw new InvalidOperationException("Encrypted bundle index authentication failed.");

            ValidateEntries(index.Files);
            if (Directory.EnumerateFileSystemEntries(payloadPath).Count() != index.Files.Count)
                throw new InvalidOperationException("Unexpected encrypted bundle object count.");

            var result = new List<string> { IndexName, IndexMacName };
            var indexDigest = SHA256.HashData(indexBytes);
            for (var i = 0; i < index.Files.Count; i++)
            {
                var name = CipherName(i);
                var file = Path.Combine(payloadPath, name);
                CheckRegularFile(file);
                var length = index.Files[i].Length;
                if (new FileInfo(file).Length != checked(length + ChunkCount(length) * TagBytes))
                    throw new InvalidOperationException("Encrypted payload length mismatch.");

                var key = DeriveKey(encryptionKey, salt, i);
                try
                {
                    using var cipher = new AesGcm(key, TagBytes);
                    await using var stream = new FileStream(
                        file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    long consumed = 0;
                    for (long part = 0; part < ChunkCount(length); part++)
                    {
                        var size = (int)Math.Min(ChunkBytes, length - consumed);
                        var encrypted = new byte[size];
                        var tag = new byte[TagBytes];
                        var temporaryPlaintext = new byte[size];
                        try
                        {
                            await stream.ReadExactlyAsync(encrypted.AsMemory(), ct);
                            await stream.ReadExactlyAsync(tag.AsMemory(), ct);
                            cipher.Decrypt(Nonce(part), encrypted, tag, temporaryPlaintext,
                                AssociatedData(indexDigest, i, part));
                        }
                        catch (CryptographicException)
                        {
                            throw new InvalidOperationException("Encrypted payload GCM authentication failed.");
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(temporaryPlaintext);
                        }
                        consumed += size;
                    }
                    if (stream.ReadByte() != -1)
                        throw new InvalidOperationException("Unexpected encrypted payload trailing bytes.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
                result.Add(PayloadDirectory + "/" + name);
            }
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
        }
    }
}
