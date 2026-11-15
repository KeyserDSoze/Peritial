using Peritial.Business;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class UploadFingerprintTests
{
    private static readonly byte[] Bytes = "%PDF-1.7\none\n"u8.ToArray();

    [Fact]
    public void Identical_files_and_metadata_produce_identical_sha256_hash()
    {
        Assert.Equal(UploadFingerprint.Compute("proof.pdf", "application/pdf", null, Bytes),
            UploadFingerprint.Compute("proof.pdf", "application/pdf", null, Bytes));
    }

    [Fact]
    public void Changed_bytes_name_mime_or_parent_change_the_digest()
    {
        var current = UploadFingerprint.Compute("proof.pdf", "application/pdf", null, Bytes);
        foreach (var digest in new[]
        {
            UploadFingerprint.Compute("proof.pdf", "application/pdf", null,
                "%PDF-1.7\ntwo\n"u8.ToArray()),
            UploadFingerprint.Compute("other.pdf", "application/pdf", null, Bytes),
            UploadFingerprint.Compute("proof.pdf", "image/png", null, Bytes),
            UploadFingerprint.Compute("proof.pdf", "application/pdf", Guid.NewGuid(), Bytes)
        })
            Assert.NotEqual(current, digest);
    }
}
