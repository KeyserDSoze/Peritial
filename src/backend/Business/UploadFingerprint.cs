using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Peritial.Business;

/// <summary>Canonical metadata and file bytes, SHA-256 with length-delimited fields.</summary>
public static class UploadFingerprint
{
    public static byte[] Compute(string name, string mime, Guid? periziaId, ReadOnlySpan<byte> bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, "peritial-upload-v1");
        Add(hash, name);
        Add(hash, mime.ToLowerInvariant());
        Add(hash, periziaId?.ToString("D") ?? "");
        hash.AppendData(bytes);
        return hash.GetHashAndReset();
    }
    private static void Add(IncrementalHash hash, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
