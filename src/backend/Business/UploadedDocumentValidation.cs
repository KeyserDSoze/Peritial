using System.IO.Compression;
using System.Text;
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

    // OOXML is a ZIP container. Cap both expansion and entry count BEFORE
    // malware scanning and storage, including entries with forged ZIP lengths.
    // These are application admission budgets, not the ClamAV scan limits.
    private const int MaxDocxEntries = 1024;
    private const long MaxDocxPartBytes = 25L * 1024 * 1024;
    private const long MaxDocxExpandedBytes = 64L * 1024 * 1024;
    private const long MaxDocxCompressionRatio = 200;

    private static bool IsReservedWindowsDeviceName(string segment)
    {
        // These names bind to DOS devices on Windows (also with extensions),
        // not ordinary filesystem entries. Reject them in ANY ZIP path
        // segment to protect downstream Windows viewers and extractors.
        // Superscript 1/2/3 are also recognized as COM/LPT digits by Windows.
        var dot = segment.IndexOf('.');
        var stem = (dot < 0 ? segment : segment[..dot]).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            return true;
        return stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.Ordinal) ||
                stem.StartsWith("LPT", StringComparison.Ordinal)) &&
               "123456789\u00B9\u00B2\u00B3".Contains(stem[3]);
    }

    private static void ValidateOpcRelationship(XmlReader reader, string relationshipsPartName)
    {
        if (reader.NodeType != XmlNodeType.Element ||
            !reader.LocalName.Equals("Relationship", StringComparison.OrdinalIgnoreCase))
            return;

        // A later OOXML renderer may follow a relation outside the ZIP.
        // Both unknown and explicitly external modes fail closed.
        var mode = reader.GetAttribute("TargetMode");
        if (mode is not null &&
            !string.Equals(mode, "Internal", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Unsupported external DOCX relationship.");

        var target = reader.GetAttribute("Target")?.Trim();
        if (string.IsNullOrEmpty(target))
            return;

        // XmlReader decodes XML entities; URI percent escapes must also be
        // interpreted before validating the eventual target. Reject nested
        // escaping, UNC paths and URI schemes without resolving anything.
        var unescaped = Uri.UnescapeDataString(target);
        var packageRootPath = unescaped.StartsWith("/", StringComparison.Ordinal) &&
            !unescaped.StartsWith("//", StringComparison.Ordinal);
        var delimiter = unescaped.IndexOfAny(['?', '#']);
        var path = delimiter < 0 ? unescaped : unescaped[..delimiter];
        if (unescaped.Contains('%') || unescaped.Contains('\\') ||
            unescaped.Any(char.IsControl) ||
            unescaped.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains(':') ||
            (!packageRootPath && Uri.TryCreate(unescaped, UriKind.Absolute, out _)))
            throw new ArgumentException("Unsupported external DOCX relationship.");

        // OPC relationship targets are resolved against the *source part*,
        // not the .rels part itself. Example: word/_rels/document.xml.rels
        // belongs to word/document.xml, whose directory depth is one.
        // Package-root paths (/word/...) instead begin at depth zero.
        var relsDir = relationshipsPartName.LastIndexOf("/_rels/",
            StringComparison.OrdinalIgnoreCase);
        string sourceDirectory;
        if (relsDir >= 0)
            sourceDirectory = relationshipsPartName[..relsDir];
        else if (relationshipsPartName.StartsWith("_rels/",
                     StringComparison.OrdinalIgnoreCase))
            sourceDirectory = "";
        else
        {
            var slash = relationshipsPartName.LastIndexOf('/');
            sourceDirectory = slash < 0 ? "" : relationshipsPartName[..slash];
        }

        var depth = packageRootPath || sourceDirectory.Length == 0
            ? 0
            : 1 + sourceDirectory.Count(c => c == '/');
        foreach (var segment in path.Split('/'))
        {
            if (segment == "..")
            {
                if (--depth < 0)
                    throw new ArgumentException("DOCX relationship escapes the package.");
            }
            else if (segment.Length != 0 && segment != ".")
                depth++;
        }
    }

    private static void ValidateDocx(ReadOnlySpan<byte> data)
    {
        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.Entries.Count is 0 or > MaxDocxEntries)
                throw new ArgumentException("DOCX contains too many ZIP entries.");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // An implicit directory is required by every nested member. A
            // regular ZIP file may never claim that same path, regardless
            // of entry order or whether a directory entry is explicit.
            var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long declaredTotal = 0;
            var hasContentTypes = false;
            var hasDocument = false;
            foreach (var entry in archive.Entries)
            {
                // Neither a downstream OOXML consumer nor an archive extractor
                // must be offered traversal, absolute, ambiguous or duplicate
                // (on Windows) paths from an uploaded document.
                var name = entry.FullName;
                // ZIP central-directory external attributes can describe
                // Unix symlinks, FIFOs, sockets and device nodes rather than
                // ordinary files. We do not extract entries today, but
                // accepting special entries would make later OOXML parsing
                // or extraction unsafe. For Unix mode bits, permit only
                // regular files (0100000), directories (0040000) or absent
                // type metadata (older/Windows ZIP writers).
                // Mask from unsigned bits: ExternalAttributes is an int and
                // the high 16 bits carry the Unix mode where present.
                var unixType = ((uint)entry.ExternalAttributes >> 16) & 0xF000u;
                if (unixType != 0 && unixType != 0x8000u && unixType != 0x4000u)
                    throw new ArgumentException("Unsupported DOCX ZIP entry type.");
                // A ZIP entry explicitly marked as a Unix directory must be a
                // directory path, and a regular file must not masquerade as
                // one. Reject ambiguous metadata before any decompression,
                // scanning, or downstream document processing.
                var isDirectoryPath = name.EndsWith('/');
                if ((unixType == 0x4000u && !isDirectoryPath) ||
                    (unixType == 0x8000u && isDirectoryPath))
                    throw new ArgumentException("Inconsistent DOCX ZIP entry type.");
                // Some filesystems normalize canonically equivalent Unicode
                // spellings to the same name (NFC vs decomposed). Reuse the
                // canonical path for both duplicate detection and the
                // implicit file/directory tree, without rewriting ZIP bytes.
                var canonicalPath = name.TrimEnd('/').Normalize(NormalizationForm.FormC);
                var segments = name.Split('/');
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') ||
                    name.Any(char.IsControl) ||
                    name.Contains('\\') || name.Contains(':') ||
                    segments.Any(s => s is "." or ".." ||
                        s.EndsWith('.') || s.EndsWith(' ') ||
                        IsReservedWindowsDeviceName(s)) ||
                    segments.SkipLast(1).Any(string.IsNullOrEmpty) ||
                    (!isDirectoryPath && segments.Any(string.IsNullOrEmpty)) ||
                    !names.Add(canonicalPath))
                    throw new ArgumentException("Unsafe DOCX ZIP entry name.");

                // A directory entry with actual content is ambiguous: some
                // ZIP consumers ignore its bytes while others materialize
                // them. Reject the payload before scanning or storage.
                if (isDirectoryPath && entry.Length != 0)
                    throw new ArgumentException("DOCX ZIP directory cannot contain data.");

                // Trailing dot/space segments are filesystem aliases on
                // Windows; a file also cannot shadow an implicit directory.
                // Check both entry orders, without extracting ZIP members.
                if (!isDirectoryPath && directoryPaths.Contains(canonicalPath))
                    throw new ArgumentException("Ambiguous DOCX ZIP file/directory path.");
                for (var slash = canonicalPath.IndexOf('/');
                     slash >= 0;
                     slash = canonicalPath.IndexOf('/', slash + 1))
                {
                    var parent = canonicalPath[..slash];
                    if (filePaths.Contains(parent))
                        throw new ArgumentException("Ambiguous DOCX ZIP file/directory path.");
                    directoryPaths.Add(parent);
                }
                if (isDirectoryPath)
                    directoryPaths.Add(canonicalPath);
                else
                    filePaths.Add(canonicalPath);

                if (entry.Length < 0 || entry.CompressedLength < 0 ||
                    entry.Length > MaxDocxPartBytes ||
                    entry.Length > MaxDocxExpandedBytes - declaredTotal ||
                    (entry.Length >= 1024 * 1024 &&
                     (entry.CompressedLength == 0 ||
                      entry.Length / entry.CompressedLength > MaxDocxCompressionRatio)))
                    throw new ArgumentException("DOCX ZIP expansion limit exceeded.");

                declaredTotal += entry.Length;
                hasContentTypes |= name == "[Content_Types].xml";
                hasDocument |= name == "word/document.xml";
            }

            if (!hasContentTypes || !hasDocument)
                throw new ArgumentException("Invalid DOCX structure.");

            // Never trust central-directory size fields alone. Actually
            // decompress each part with an independent bounded byte budget.
            // This prevents a forged size from bypassing the metadata limit
            // and also rejects corrupt entries before ClamAV sees the ZIP.
            long expanded = 0;
            var scratch = new byte[64 * 1024];
            foreach (var entry in archive.Entries)
            {
                // OOXML XML parts can be hidden outside word/document.xml,
                // including the OPC .rels relationship files. A valid ZIP
                // header alone is not a guarantee that its XML is safe for
                // a downstream document processor. Buffer only XML parts,
                // after bounding their real inflated size, and reject DTDs.
                var isRelationshipsPart = entry.FullName.EndsWith(".rels",
                    StringComparison.OrdinalIgnoreCase);
                var xmlPart = entry.FullName.EndsWith(".xml",
                        StringComparison.OrdinalIgnoreCase) || isRelationshipsPart;
                using var inflatedXml = xmlPart
                    ? new MemoryStream((int)Math.Min(entry.Length, 1024 * 1024))
                    : null;
                using var part = entry.Open();
                long readPart = 0;
                int count;
                while ((count = part.Read(scratch)) != 0)
                {
                    readPart += count;
                    expanded += count;
                    if (readPart > MaxDocxPartBytes || expanded > MaxDocxExpandedBytes)
                        throw new ArgumentException("DOCX ZIP expansion limit exceeded.");
                    inflatedXml?.Write(scratch, 0, count);
                }
                if (readPart != entry.Length)
                    throw new ArgumentException("Invalid DOCX ZIP entry length.");

                if (inflatedXml is not null)
                {
                    inflatedXml.Position = 0;
                    using var reader = XmlReader.Create(inflatedXml, new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = MaxDocxPartBytes,
                        CheckCharacters = true,
                        ConformanceLevel = ConformanceLevel.Document
                    });
                    if (reader.MoveToContent() != XmlNodeType.Element)
                        throw new ArgumentException("DOCX XML part requires a root element.");
                    do
                    {
                        if (isRelationshipsPart)
                            ValidateOpcRelationship(reader, entry.FullName);
                    }
                    while (reader.Read());
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new ArgumentException("Invalid DOCX archive.", nameof(data), ex);
        }
        catch (XmlException)
        {
            // Do not echo any attacker-controlled XML or system-entity URI
            // in an API error or audit. The caller has not scanned/stored it.
            throw new ArgumentException("Invalid or unsafe DOCX XML part.", nameof(data));
        }
    }
}
