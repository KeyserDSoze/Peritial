using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Peritial.Business;

/// <summary>
/// Safe, read-only preflight for legacy Classic/Accertatore XML.
/// This does NOT import, persist or scan attachments and is not an
/// authorization for ingestion. The signed actor must have write scope.
/// </summary>
public sealed record LegacyXmlPreviewDto(
    string Format, string ExternalReference,
    DateOnly OccurredOn, DateOnly? ReportedOn, int AttachmentCount);

public sealed class LegacyXmlPreviewService(ITenantContext context)
{
    public const int MaxXmlBytes = 1024 * 1024;
    private const int MaxAttachmentCount = 128;
    private const string ClassicRoot = "it.autosoftws.ReIncaricoRecord";
    private const string ClassicBaseRoot = "it.autosoftws.ReIncaricoBaseRecord";
    private const string AccertatoreRoot = "it.autosoftws.IncaricoAccertatoreRecord";

    public LegacyXmlPreviewDto PreviewUtf8(string xml)
    {
        RequireWrite();
        ArgumentNullException.ThrowIfNull(xml);
        if (Encoding.UTF8.GetByteCount(xml) > MaxXmlBytes)
            throw new ArgumentException("XML preview exceeds 1 MiB.", nameof(xml));
        return Preview(Encoding.UTF8.GetBytes(xml));
    }

    public LegacyXmlPreviewDto Preview(ReadOnlyMemory<byte> xml)
    {
        RequireWrite();
        if (xml.IsEmpty || xml.Length > MaxXmlBytes)
            throw new ArgumentException("XML preview must contain 1..1048576 bytes.", nameof(xml));

        XDocument document;
        try
        {
            using var input = new MemoryStream(xml.ToArray(), writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxXmlBytes,
                MaxCharactersFromEntities = 0,
                CheckCharacters = true,
                ConformanceLevel = ConformanceLevel.Document,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            // Do not include attacker-controlled XML or resolver details in
            // returned errors, audit, logs or the DTO.
            throw new ArgumentException("Invalid or unsafe legacy XML.", nameof(xml), ex);
        }

        var root = document.Root ??
            throw new ArgumentException("Legacy XML root is required.", nameof(xml));
        // Exact, unqualified legacy root names. Namespace tricks/prefixes
        // must not reclassify a different input as an approved format.
        if (root.Name.NamespaceName.Length != 0)
            throw new ArgumentException("Unsupported legacy XML root.", nameof(xml));
        var format = root.Name.LocalName switch
        {
            ClassicRoot or ClassicBaseRoot => "classic",
            AccertatoreRoot => "accertatore",
            _ => throw new ArgumentException("Unsupported legacy XML root.", nameof(xml))
        };

        var number = ReadDirectScalar(root, "NumeroSinistro", required: true)!;
        if (number.Length > 100)
            throw new ArgumentException("Legacy claim reference is too long.", nameof(xml));

        var occurred = ParseDate(ReadDirectScalar(root, "DataOraSinistro", required: true)!);
        var reportedValue = ReadDirectScalar(root, "DataIncarico", required: false);
        var reported = reportedValue is null ? (DateOnly?)null : ParseDate(reportedValue);

        var attachments = root.Elements().Where(e => e.Name == "allegati").ToArray();
        if (attachments.Length > 1 ||
            attachments.Any(e => e.Name.NamespaceName.Length != 0))
            throw new ArgumentException("Invalid legacy attachments container.", nameof(xml));
        var count = 0;
        if (attachments.Length == 1)
        {
            foreach (var attachment in attachments[0].Elements())
            {
                if (attachment.Name.NamespaceName.Length != 0 ||
                    attachment.Name.LocalName != "it.autosoftws.AllegatoRecord" ||
                    ++count > MaxAttachmentCount)
                    throw new ArgumentException("Invalid legacy attachments.", nameof(xml));
            }
            if (attachments[0].Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                throw new ArgumentException("Invalid legacy attachments.", nameof(xml));
        }

        // Do not return names, addresses, persons, base64 attachment payloads
        // or storage references: preview is only an import preflight.
        return new LegacyXmlPreviewDto(format, number, occurred, reported, count);
    }

    private static string? ReadDirectScalar(XElement root, string name, bool required)
    {
        var values = root.Elements().Where(e => e.Name == name).ToArray();
        if (values.Length > 1 || values.Any(e => e.HasElements))
            throw new ArgumentException("Invalid legacy XML field: " + name);
        if (values.Length == 0 || string.IsNullOrWhiteSpace(values[0].Value))
        {
            if (required)
                throw new ArgumentException("Missing legacy XML field: " + name);
            return null;
        }
        var value = values[0].Value.Trim();
        if (value.Length > 128)
            throw new ArgumentException("Legacy XML field too long: " + name);
        return value;
    }

    private static DateOnly ParseDate(string text)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date))
            return date;
        if (DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            return DateOnly.FromDateTime(timestamp);
        throw new ArgumentException("Invalid legacy XML calendar date.");
    }

    private void RequireWrite()
    {
        if (!context.HasScope("peritial.write"))
            throw new UnauthorizedAccessException("Legacy XML preview requires peritial.write.");
    }
}
