using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

internal static class LegacyBackupProof
{
    /// <summary>
    /// Operational safety gate, not a cryptographic signature or a guarantee
    /// that the archive represents the currently stopped database.
    /// </summary>
    internal static async Task VerifyAsync(string archive, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(archive) || !File.Exists(archive) ||
            !File.Exists(archive + ".sha256"))
            throw new InvalidOperationException("A complete absolute PostgreSQL backup path is required.");

        var archiveInfo = new FileInfo(archive);
        var checksumInfo = new FileInfo(archive + ".sha256");
        if (archiveInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            checksumInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            archiveInfo.Length < 8 || checksumInfo.Length > 128)
            throw new InvalidOperationException("Backup proof contains unsafe files.");

        var expected = (await File.ReadAllTextAsync(checksumInfo.FullName, ct)).Trim();
        if (expected.Length != 64 || expected.Any(c => c is not (
                >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidOperationException("Invalid SHA-256 manifest.");

        await using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, FileOptions.SequentialScan);
        var header = new byte[5];
        if (await stream.ReadAsync(header, ct) != header.Length ||
            !header.AsSpan().SequenceEqual("PGDMP"u8))
            throw new InvalidOperationException("Not a PostgreSQL custom-format archive.");
        stream.Position = 0;
        var actual = await SHA256.HashDataAsync(stream, ct);
        if (!CryptographicOperations.FixedTimeEquals(
            actual, Convert.FromHexString(expected)))
            throw new InvalidOperationException("Backup checksum mismatch.");

        // A hash proves file integrity, not that it can be restored. Inspect
        // the archive TOC with PostgreSQL client tools before any schema DDL.
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("pg_restore")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--list");
        process.StartInfo.ArgumentList.Add(archive);
        if (!process.Start())
            throw new InvalidOperationException("Cannot start pg_restore.");
        await Task.WhenAll(
            process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct),
            process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct),
            process.WaitForExitAsync(ct));
        if (process.ExitCode != 0)
            throw new InvalidOperationException("PostgreSQL backup archive is unreadable.");
    }
}
