using System.IO.Compression;
using System.Xml;

namespace Peritial.Business;

/// <summary>
/// Admission checks, not a malware scanner: never trust MIME or file name alone.
/// Actual parsing and antivirus quarantine are tracked separately in issue #5.
/// </summary>
internal static class UploadedDocumentValidation
{
    private static readonly byte[] PngMagic = [137, 80, 78, 71, 13, 10, 26, 10];

    public static string CanonicalFileName(string original)
    {
        if (string.IsNullOrWhiteSpace(original) || original.Length > 255)
            throw new ArgumentException("Invalid file name.", nameof(original));

        // Path.GetFileName on Linux does not consider Windows backslashes separators.
        var normalized = original.Replace('\\', '/');
        var safe = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or ".." || safe.Any(char.IsControl))
            throw new ArgumentException("Invalid file name.", nameof(original));
        return safe;
    }

    public static void ValidateContent(string extension, ReadOnlySpan<byte> content)
    {
        var validSignature = extension.ToLowerInvariant() switch
        {
            ".pdf" => content.StartsWith("%PDF-"u8),
            ".jpg" or ".jpeg" => content.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            ".png" => content.StartsWith(PngMagic),
            ".xml" => content.StartsWith("<?xml"u8) || content.StartsWith("<"u8) ||
                      content.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'<' }),
            ".docx" => content.StartsWith(new byte[] { (byte)'P', (byte)'K', 0x03, 0x04 }),
            _ => false
        };
        if (!validSignature)
            throw new ArgumentException("File signature does not match the file extension.");

        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            ValidateXml(content);
        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            ValidateDocx(content);
    }

    private static void ValidateXml(ReadOnlySpan<byte> data)
    {
        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = FileIngestionService.MaxUploadBytes,
                CheckCharacters = true,
                ConformanceLevel = ConformanceLevel.Document
            });
            if (reader.MoveToContent() != XmlNodeType.Element)
                throw new ArgumentException("XML root element required.");
            while (reader.Read()) { }
        }
        catch (XmlException ex)
        {
            throw new ArgumentException("Invalid or unsafe XML content.", nameof(data), ex);
        }
    }

    private static void ValidateDocx(ReadOnlySpan<byte> data)
    {
        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.GetEntry("[Content_Types].xml") is null ||
                archive.GetEntry("word/document.xml") is null)
                throw new ArgumentException("Invalid DOCX structure.");
        }
        catch (InvalidDataException ex)
        {
            throw new ArgumentException("Invalid DOCX archive.", nameof(data), ex);
        }
    }
}
