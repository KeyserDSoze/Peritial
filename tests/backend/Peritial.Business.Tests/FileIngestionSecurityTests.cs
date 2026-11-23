using System.Text;
using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class FileIngestionSecurityTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
    private static readonly byte[] Xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><document><item>ok</item></document>");
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0];

    private const string DocxMime =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    [Theory]
    [InlineData("word/comments.xml", "<comments><broken></comments>")]
    [InlineData("word/_rels/document.xml.rels", "<Relationships><Relationship>")]
    [InlineData("word/header1.xml", "<!DOCTYPE x [<!ENTITY file SYSTEM 'file:///etc/passwd'>]><x>&file;</x>")]
    [InlineData("word/styles.XML", "<!DOCTYPE x SYSTEM 'https://invalid.example/xxe'><x/>")]
    public async Task Docx_con_parte_xml_malformata_o_dtd_non_raggiunge_scanner_ne_storage(
        string partName, string xml)
    {
        // Valid ZIP metadata and a normal word/document.xml are insufficient
        // if a secondary OOXML part can carry malformed or external XML.
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var bytes = CreateDocx((partName, Encoding.UTF8.GetBytes(xml)));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapporto.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));

        Assert.Equal(0, scanner.Scans);
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
        Assert.Empty(docs.AuditEvents);
    }

    [Theory]
    [InlineData("External", "https://attacker.example/remote.png")]
    [InlineData("external", "file:///etc/passwd")]
    [InlineData("EXTERNAL", "../media/image.png")]
    [InlineData("Bogus", "../media/image.png")]
    [InlineData(null, "https://attacker.example/remote.png")]
    [InlineData("Internal", "file:///etc/passwd")]
    [InlineData("Internal", "//attacker.example/remote.png")]
    [InlineData("Internal", @"\\server\share\secret.png")]
    [InlineData(null, "urn:example:external")]
    [InlineData("Internal", " https://attacker.example/remote.png ")]
    [InlineData(null, "https&#58;//attacker.example/remote.png")]
    public async Task Docx_con_relazione_opc_esterna_o_non_ammessa_e_rifiutato_prima_della_scansione(
        string? mode, string target)
    {
        // Well-formed OPC relationship metadata can still instruct a future
        // document renderer to fetch remote resources or local files.
        // XML attribute entities must be resolved by XmlReader before checking.
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var modeAttribute = mode is null ? "" : $" TargetMode=\"{mode}\"";
        var xml = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"urn:testing\" Target=\"{target}\"{modeAttribute}/></Relationships>";
        var bytes = CreateDocx(("word/_rels/document.xml.rels", Encoding.UTF8.GetBytes(xml)));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapport.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));

        Assert.Equal(0, scanner.Scans);
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
        Assert.Empty(docs.AuditEvents);
    }

    [Theory]
    [InlineData(null, "../media/image.png")]
    [InlineData("Internal", "/word/media/image.png")]
    [InlineData("Internal", "../styles.xml#format")]
    public async Task Docx_con_relazione_opc_interna_rimane_ammesso(
        string? mode, string target)
    {
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var modeAttribute = mode is null ? "" : $" TargetMode=\"{mode}\"";
        var xml = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"urn:testing\" Target=\"{target}\"{modeAttribute}/></Relationships>";
        var bytes = CreateDocx(("word/_rels/document.xml.rels", Encoding.UTF8.GetBytes(xml)));

        var saved = await service.IngestAsync("rapport.docx", DocxMime,
            new MemoryStream(bytes), bytes.Length);
        Assert.Equal(bytes.Length, saved.Length);
        Assert.Equal(1, scanner.Scans);
        Assert.Equal(1, store.Uploads);
        Assert.Single(docs.Records);
    }

    [Theory]
    [InlineData("word/_rels/document.xml.rels", "../../private.xml")]
    [InlineData("word/_rels/document.xml.rels", "../%2e%2e/private.xml")]
    [InlineData("word/_rels/document.xml.rels", "%2e%2e%2f%2e%2e%2fprivate.xml")]
    [InlineData("word/_rels/document.xml.rels", "/../../private.xml")]
    [InlineData("_rels/.rels", "../private.xml")]
    [InlineData("word/sub/_rels/part.xml.rels", "../../../private.xml")]
    [InlineData("word/_rels/document.xml.rels", "%2f%2fattacker.example/private.xml")]
    [InlineData("word/_rels/document.xml.rels", "https%3a%2f%2fattacker.example/private.xml")]
    [InlineData("word/_rels/document.xml.rels", @"..\..\private.xml")]
    [InlineData("word/_rels/document.xml.rels", @"../media\private.xml")]
    [InlineData("word/_rels/document.xml.rels", "%252e%252e%252f%252e%252e%252fprivate.xml")]
    public async Task Docx_con_target_opc_che_esce_dal_pacchetto_o_decodifica_in_risorsa_esterna_e_rifiutato(
        string relsEntry, string target)
    {
        // A relationship declared "Internal" must not escape the ZIP
        // package or turn into a network/file reference after percent decode.
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var xml = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"urn:testing\" TargetMode=\"Internal\" Target=\"" +
            target + "\"/></Relationships>";
        var bytes = CreateDocx((relsEntry, Encoding.UTF8.GetBytes(xml)));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapport.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));
        Assert.Equal(0, scanner.Scans);
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
        Assert.Empty(docs.AuditEvents);
    }

    [Theory]
    [InlineData("word/_rels/document.xml.rels", "../media/image.png")]
    [InlineData("word/_rels/document.xml.rels", "../media/my%20image.png")]
    [InlineData("word/_rels/document.xml.rels", "/word/media/image.png")]
    [InlineData("word/sub/_rels/part.xml.rels", "../../media/image.png")]
    [InlineData("_rels/.rels", "word/document.xml")]
    [InlineData("word/_rels/document.xml.rels", "#bookmark")]
    public async Task Docx_con_target_opc_che_restano_nella_radice_del_pacchetto_e_ammesso(
        string relsEntry, string target)
    {
        // Package-root and relative paths remain valid; an OPC relationship
        // does not have to reference a member that is present at admission.
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var xml = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"urn:testing\" TargetMode=\"Internal\" Target=\"" +
            target + "\"/></Relationships>";
        var bytes = CreateDocx((relsEntry, Encoding.UTF8.GetBytes(xml)));

        var saved = await service.IngestAsync("rapport.docx", DocxMime,
            new MemoryStream(bytes), bytes.Length);
        Assert.Equal(bytes.Length, saved.Length);
        Assert.Equal(1, scanner.Scans);
        Assert.Equal(1, store.Uploads);
        Assert.Single(docs.Records);
    }

    [Fact]
    public async Task Docx_con_parti_xml_e_relazioni_ben_formate_resta_ammesso()
    {
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var bytes = CreateDocx(
            ("word/styles.xml", "<styles><style/></styles>"u8.ToArray()),
            ("word/_rels/document.xml.rels",
                "<Relationships><Relationship/></Relationships>"u8.ToArray()),
            ("word/media/photo.bin", new byte[] { 1, 2, 3 }));
        var saved = await service.IngestAsync("rapporto.docx", DocxMime,
            new MemoryStream(bytes), bytes.Length);

        Assert.Equal(bytes.Length, saved.Length);
        Assert.Equal(1, scanner.Scans);
        Assert.Equal(1, store.Uploads);
        Assert.Single(docs.Records);
    }

    [Fact]
    public async Task Docx_valido_rimane_ammesso_dopo_i_controlli_anti_zip_bomb()
    {
        var (service, store, docs) = NewService();
        var bytes = CreateDocx();
        var saved = await service.IngestAsync("rapporto.docx", DocxMime,
            new MemoryStream(bytes), bytes.Length);
        Assert.Equal(bytes.Length, saved.Length);
        Assert.Equal(1, store.Uploads);
        Assert.Single(docs.Records);
    }

    [Fact]
    public async Task Docx_con_zip_bomb_altamente_comprimibile_e_bloccato_prima_della_scansione()
    {
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        // Eight MiB compresses to a handful of KiB; decompression
        // must not be accepted just because the ZIP itself is small.
        var bytes = CreateDocx(("word/media/huge.bin", new byte[8 * 1024 * 1024]));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapporto.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));
        Assert.Equal(0, scanner.Scans);
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
    }

    [Fact]
    public async Task Docx_con_troppe_entry_zip_e_rifiutato()
    {
        var (service, store, docs) = NewService();
        var entries = Enumerable.Range(0, 1023)
            .Select(i => ($"word/media/entry-{i}.bin", Array.Empty<byte>()))
            .ToArray();
        // Two mandatory OOXML files + 1023 additional files = 1025 entries.
        var bytes = CreateDocx(entries);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapporto.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
    }

    [Fact]
    public async Task Docx_con_backslash_zip_path_e_rifiutato()
    {
        var (service, store, docs) = NewService();
        var bytes = CreateDocx(("word\\..\\outside.xml", "<x/>"u8.ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapporto.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
    }

    [Fact]
    public async Task Docx_con_zip_slip_path_e_bloccato_prima_dello_storage()
    {
        var (service, store, docs) = NewService();
        var bytes = CreateDocx(("../escape.xml", "<root/>"u8.ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapporto.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
    }

    [Fact]
    public async Task Docx_con_entry_duplicate_case_insensitive_e_bloccato()
    {
        var (service, store, docs) = NewService();
        var bytes = CreateDocx(("[content_types].XML", "<Types/>"u8.ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapporto.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Docx_con_due_member_zip_unicode_equivalenti_normalizzati_e_rifiutato(
        bool decomposedFirst)
    {
        // Same filename after Unicode NFC despite distinct original ZIP
        // name bytes. Windows/Mac filesystems and converters can alias them.
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var composed = ("word/media/r\u00E9sum\u00E9.bin", new byte[] { 1 });
        var decomposed = ("word/media/re\u0301sume\u0301.bin", new byte[] { 2 });
        var bytes = decomposedFirst
            ? CreateDocx(decomposed, composed)
            : CreateDocx(composed, decomposed);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("report.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));

        Assert.Equal(0, scanner.Scans);
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
        Assert.Empty(docs.AuditEvents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Docx_con_file_parent_e_directory_child_equivalenti_unicode_e_rifiutato(
        bool childFirst)
    {
        // Normalize the entire package path before checking whether a
        // regular file shadows an implicit directory.
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var parent = ("word/media/caf\u00E9", new byte[] { 1 });
        var child = ("word/media/cafe\u0301/payload.bin", new byte[] { 2 });
        var bytes = childFirst
            ? CreateDocx(child, parent)
            : CreateDocx(parent, child);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("report.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));

        Assert.Equal(0, scanner.Scans);
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
        Assert.Empty(docs.AuditEvents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Docx_con_directory_esplicita_e_file_omonimo_unicode_e_rifiutato(
        bool fileFirst)
    {
        // A directory entry and a regular file must never be aliases,
        // regardless of composition and entry order.
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var directory = ("word/media/caf\u00E9/", Array.Empty<byte>());
        var file = ("word/media/cafe\u0301", new byte[] { 1 });
        var bytes = fileFirst ? CreateDocx(file, directory) : CreateDocx(directory, file);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("report.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));

        Assert.Equal(0, scanner.Scans);
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
        Assert.Empty(docs.AuditEvents);
    }

    [Fact]
    public async Task Docx_con_un_unico_nome_unicode_decomposto_rimane_ammesso()
    {
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var bytes = CreateDocx(("word/media/cafe\u0301.bin", new byte[] { 1, 2 }));
        var result = await service.IngestAsync("report.docx", DocxMime,
            new MemoryStream(bytes), bytes.Length);

        Assert.Equal(bytes.Length, result.Length);
        Assert.Equal(1, scanner.Scans);
        Assert.Equal(1, store.Uploads);
        Assert.Single(docs.Records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Docx_con_file_che_oscura_una_directory_implicitamente_richiesta_e_rifiutato(
        bool childFirst)
    {
        // Either ZIP entry order is invalid: a path cannot simultaneously
        // name a regular file and be an ancestor directory for another entry.
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var parent = ("word/media/collision.bin", new byte[] { 1, 2 });
        var child = ("word/media/collision.bin/payload.dat", new byte[] { 3 });
        var bytes = childFirst ? CreateDocx(child, parent) : CreateDocx(parent, child);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapporto.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));

        Assert.Equal(0, scanner.Scans);
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
        Assert.Empty(docs.AuditEvents);
    }

    [Theory]
    [InlineData("word/media/ambiguous./part.bin")]
    [InlineData("word/media/ambiguous /part.bin")]
    [InlineData("word/media/ambiguous.bin.")]
    [InlineData("word/media/ambiguous.bin ")]
    public async Task Docx_con_segmento_zip_che_finirebbe_in_punto_o_spazio_e_rifiutato(
        string unsafeEntryName)
    {
        // Some downstream extractors/filesystems trim these suffixes,
        // potentially shadowing a different OOXML member.
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var bytes = CreateDocx((unsafeEntryName, new byte[] { 1, 2, 3 }));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapporto.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));

        Assert.Equal(0, scanner.Scans);
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
        Assert.Empty(docs.AuditEvents);
    }

    [Fact]
    public async Task Docx_con_directory_zip_esplicita_e_figlio_regolare_e_ammesso()
    {
        // A normal explicit directory is not a collision with its own child.
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var bytes = CreateDocx(
            ("word/media/", Array.Empty<byte>()),
            ("word/media/image.bin", new byte[] { 1, 2, 3 }));

        var saved = await service.IngestAsync("rapporto.docx", DocxMime,
            new MemoryStream(bytes), bytes.Length);

        Assert.Equal(bytes.Length, saved.Length);
        Assert.Equal(1, scanner.Scans);
        Assert.Equal(1, store.Uploads);
        Assert.Single(docs.Records);
    }

    [Theory]
    [InlineData("word/media/CON.bin")]
    [InlineData("word/media/con.TXT")]
    [InlineData("word/media/NUL")]
    [InlineData("word/media/AUX.xml")]
    [InlineData("word/media/PRN.jpg")]
    [InlineData("word/media/COM1.bin")]
    [InlineData("word/media/LPT9.bin")]
    [InlineData("word/media/COM\u00B9.bin")]
    [InlineData("word/media/CONIN$.dat")]
    [InlineData("word/media/CONOUT$.dat")]
    public async Task Docx_con_nome_zip_riservato_windows_e_rifiutato_prima_di_scanner_e_storage(
        string memberName)
    {
        // Windows device names resolve differently from ordinary filenames
        // even with extensions; future extraction must never consume them.
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        // XML-named members must also contain valid XML, otherwise the
        // pre-existing XML parser would reject them for the wrong reason.
        var partBytes = memberName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            ? "<valid/>"u8.ToArray() : new byte[] { 1, 2, 3 };
        var bytes = CreateDocx((memberName, partBytes));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapporto.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));

        Assert.Equal(0, scanner.Scans);
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
        Assert.Empty(docs.AuditEvents);
    }

    [Theory]
    [InlineData("word/media/COM0.bin")]
    [InlineData("word/media/LPT10.bin")]
    [InlineData("word/media/consolidato.bin")]
    [InlineData("word/media/normal-file.bin")]
    public async Task Docx_con_nome_zip_non_riservato_rimane_ammesso(
        string memberName)
    {
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var bytes = CreateDocx((memberName, new byte[] { 1, 2, 3 }));
        var saved = await service.IngestAsync("rapporto.docx", DocxMime,
            new MemoryStream(bytes), bytes.Length);

        Assert.Equal(bytes.Length, saved.Length);
        Assert.Equal(1, scanner.Scans);
        Assert.Equal(1, store.Uploads);
        Assert.Single(docs.Records);
    }

    [Fact]
    public async Task Docx_con_directory_zip_che_contiene_byte_e_rifiutato()
    {
        // A directory path carrying payload bytes is ambiguous for future
        // ZIP consumers; it must not reach the scanner or permanent storage.
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var bytes = CreateDocx(("word/media/", new byte[] { 7, 8, 9 }));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapporto.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));

        Assert.Equal(0, scanner.Scans);
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
        Assert.Empty(docs.AuditEvents);
    }

    [Theory]
    [InlineData(0xA000)] // Unix symlink
    [InlineData(0x1000)] // Unix FIFO
    [InlineData(0x2000)] // Unix character device
    [InlineData(0x6000)] // Unix block device
    [InlineData(0xC000)] // Unix socket
    public async Task Docx_con_entry_unix_non_regolare_viene_bloccato_prima_di_scanner_e_storage(
        int unixFileType)
    {
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var bytes = DocxWithUnixEntry(unixFileType);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapporto.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));

        Assert.Equal(0, scanner.Scans);
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
        Assert.Empty(docs.AuditEvents);
    }

    [Theory]
    [InlineData(9)]  // tab
    [InlineData(10)] // newline
    [InlineData(13)] // carriage return
    public async Task Docx_con_caratteri_di_controllo_nel_nome_zip_non_raggiunge_scanner_o_storage(
        int controlCode)
    {
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var unsafeName = "word/media/unsafe" + (char)controlCode + "part.bin";
        var bytes = CreateDocx((unsafeName, new byte[] { 1, 2, 3 }));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapporto.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));

        Assert.Equal(0, scanner.Scans);
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
        Assert.Empty(docs.AuditEvents);
    }

    [Fact]
    public async Task Docx_con_tipo_unix_directory_su_un_file_e_rifiutato()
    {
        // A .bin path declared as a Unix directory is ambiguous for
        // downstream ZIP extractors, even though no extraction occurs here.
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var bytes = DocxWithUnixEntry(0x4000);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("rapporto.docx", DocxMime,
                new MemoryStream(bytes), bytes.Length));

        Assert.Equal(0, scanner.Scans);
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
        Assert.Empty(docs.AuditEvents);
    }

    [Fact]
    public async Task Docx_con_normale_entry_unix_regolare_rimane_ammesso()
    {
        var scanner = new CountingScanner();
        var (service, store, docs) = NewService(scanner: scanner);
        var bytes = DocxWithUnixEntry(0x8000); // regular file, mode 0777
        var saved = await service.IngestAsync("rapporto.docx", DocxMime,
            new MemoryStream(bytes), bytes.Length);

        Assert.Equal(bytes.Length, saved.Length);
        Assert.Equal(1, scanner.Scans);
        Assert.Equal(1, store.Uploads);
        Assert.Single(docs.Records);
    }

    private static byte[] DocxWithUnixEntry(int unixFileType)
    {
        // Create a real ZIP with standard OOXML parts, then mark one member
        // with Unix mode bits in central-directory ExternalAttributes.
        var original = CreateDocx(("word/media/embedded.bin", "../../outside"u8.ToArray()));
        using var output = new MemoryStream();
        output.Write(original);
        output.Position = 0;
        using (var archive = new System.IO.Compression.ZipArchive(output,
            System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            var member = archive.GetEntry("word/media/embedded.bin")
                ?? throw new InvalidOperationException("Missing test member.");
            member.ExternalAttributes = unchecked((int)(((uint)(unixFileType | 0x1FF)) << 16));
        }
        return output.ToArray();
    }

    private static byte[] CreateDocx(params (string Name, byte[] Bytes)[] additional)
    {
        using var output = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
            output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            AddPart("[Content_Types].xml", "<Types/>"u8.ToArray());
            AddPart("word/document.xml", "<document/>"u8.ToArray());
            foreach (var part in additional)
                AddPart(part.Name, part.Bytes);

            void AddPart(string name, byte[] bytes)
            {
                var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
                using var contents = entry.Open();
                contents.Write(bytes);
            }
        }
        return output.ToArray();
    }

    [Fact]
    public async Task Falso_pdf_non_raggiunge_storage_o_database()
    {
        var (service, store, docs) = NewService();
        byte[] bytes = Encoding.UTF8.GetBytes("This is not a PDF");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("fattura.pdf", "application/pdf", new MemoryStream(bytes), bytes.Length));
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
    }

    [Fact]
    public async Task Firma_diversa_da_mime_ed_estensione_e_rifiutata()
    {
        var (service, store, docs) = NewService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("foto.png", "image/png", new MemoryStream(Pdf), Pdf.Length));
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Lunghezza_dichiarata_diversa_dai_byte_effettivi_e_rifiutata(int delta)
    {
        var (service, store, docs) = NewService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("fattura.pdf", "application/pdf",
                new NonSeekable(Pdf), Pdf.Length + delta));
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
    }

    [Fact]
    public async Task Xml_con_DTD_esterna_viene_rifiutato()
    {
        var (service, store, docs) = NewService();
        var xml = Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY ext SYSTEM \"file:///etc/passwd\">]><x>&ext;</x>");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("sinistro.xml", "text/xml", new MemoryStream(xml), xml.Length));
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
    }

    [Fact]
    public async Task PDF_valido_non_seekable_viene_salvato_una_volta()
    {
        var (service, store, docs) = NewService();
        var saved = await service.IngestAsync("fattura.pdf", "application/pdf", new NonSeekable(Pdf), Pdf.Length);
        Assert.Equal(Pdf.Length, saved.Length);
        Assert.Equal(1, store.Uploads);
        Assert.Equal(Pdf, store.LastBytes);
        Assert.Equal(saved.Id, Assert.Single(docs.Records).Id);
        var auditEvent = Assert.Single(docs.AuditEvents);
        Assert.Equal("document.ingest", auditEvent.Action);
        Assert.Equal("succeeded", auditEvent.Outcome);
        Assert.Equal(saved.Id, auditEvent.ResourceId);
    }

    [Fact]
    public async Task Png_e_xml_validi_vengono_accettati()
    {
        var (service, store, docs) = NewService();
        await service.IngestAsync("foto.png", "image/png", new MemoryStream(Png), Png.Length);
        await service.IngestAsync("sinistro.xml", "application/xml", new MemoryStream(Xml), Xml.Length);
        Assert.Equal(2, store.Uploads);
        Assert.Equal(2, docs.Records.Count);
    }

    [Fact]
    public async Task Nome_windows_non_diventa_percorso_in_storage_o_metadata()
    {
        var (service, _, docs) = NewService();
        await service.IngestAsync(@"C:\temp\segreto.pdf", "application/pdf", new MemoryStream(Pdf), Pdf.Length);
        var doc = Assert.Single(docs.Records);
        Assert.Equal("segreto.pdf", doc.FileName);
        Assert.DoesNotContain("segreto", doc.StorageKey);
    }

    [Fact]
    public async Task Scope_assente_impedisce_scrittura()
    {
        var (service, store, docs) = NewService(false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.IngestAsync("fattura.pdf", "application/pdf", new MemoryStream(Pdf), Pdf.Length));
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
    }

    [Fact]
    public async Task File_vuoto_e_rifiutato_prima_di_scrivere()
    {
        var (service, store, docs) = NewService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("vuoto.pdf", "application/pdf", new MemoryStream(), 0));
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
    }

    [Fact]
    public async Task Lunghezza_dichiarata_oltre_il_limite_e_rifiutata_prima_di_leggere()
    {
        var (service, store, docs) = NewService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync("enorme.pdf", "application/pdf", new MemoryStream(),
                FileIngestionService.MaxUploadBytes + 1));
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
    }

    [Fact]
    public async Task Perizia_di_altro_tenant_blocca_ingestion_e_non_scrive_file()
    {
        var foreign = Perizia.Create(TenantId.From(Guid.NewGuid()), "FOREIGN");
        var store = new SpyStore();
        var docs = new SpyDocuments();
        var service = new FileIngestionService(new FakeTenant(true), new FakePerizie(foreign), docs, store, new FakeAudit(), new CleanScanner(), new FakeIntents(), new FakeIdempotency());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.IngestAsync("fattura.pdf", "application/pdf",
                new MemoryStream(Pdf), Pdf.Length, foreign.Id));
        Assert.Equal(0, store.Uploads);
        Assert.Empty(docs.Records);
    }

    private static (FileIngestionService Service, SpyStore Store, SpyDocuments Docs) NewService(
        bool canWrite = true, IFileMalwareScanner? scanner = null)
    {
        var store = new SpyStore();
        var docs = new SpyDocuments();
        return (new FileIngestionService(new FakeTenant(canWrite), new FakePerizie(), docs, store, new FakeAudit(), scanner ?? new CleanScanner(), new FakeIntents(), new FakeIdempotency()), store, docs);
    }

    private sealed class CountingScanner : IFileMalwareScanner
    {
        public int Scans { get; private set; }

        public Task<MalwareScanResult> ScanAsync(
            ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Scans++;
            return Task.FromResult(MalwareScanResult.Clean);
        }
    }

    private sealed class CleanScanner : IFileMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
            Task.FromResult(MalwareScanResult.Clean);
    }

    private sealed class FakeIdempotency : IUploadIdempotencyStore
    {
        public Task<UploadIdempotencyReservation> ReserveAsync(
            TenantId tenant, string subject, string key, byte[] digest, Guid id, CancellationToken ct = default) =>
            Task.FromResult(new UploadIdempotencyReservation(UploadIdempotencyState.Acquired));
        public Task ReleaseAsync(TenantId tenant, Guid documentId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeIntents : IUploadIntentLedger
    {
        public Task RecordAsync(TenantId tenant, Guid id, string key, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<bool> RenewAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task ForgetAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeAudit : ISecurityAuditTrail
    {
        public Task AppendAsync(SecurityAuditEvent entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeTenant(bool canWrite) : ITenantContext
    {
        public TenantId TenantId { get; } = TenantId.From(Guid.NewGuid());
        public string Subject => "test";
        public bool HasScope(string scope) => canWrite && scope == "peritial.write";
    }

    private sealed class FakePerizie(Perizia? existing = null) : IPeriziaRepository
    {
        public Task<IReadOnlyList<Perizia>> ListAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Perizia>>(Array.Empty<Perizia>());
        public Task<Perizia?> FindAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            Task.FromResult<Perizia?>(existing is not null && existing.Id == id && existing.TenantId == tenant ? existing : null);
        public Task AddAsync(Perizia item, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(Perizia item, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddAuditedAsync(Perizia item, SecurityAuditEvent audit, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAuditedAsync(Perizia item, SecurityAuditEvent audit, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class SpyDocuments : IDocumentRepository
    {
        public List<DocumentRecord> Records { get; } = [];
        public Task AddAsync(DocumentRecord item, CancellationToken ct = default)
        {
            Records.Add(item);
            return Task.CompletedTask;
        }

        public List<SecurityAuditEvent> AuditEvents { get; } = [];
        public async Task AddAuditedAsync(DocumentRecord item, SecurityAuditEvent entry, CancellationToken ct = default)
        {
            AuditEvents.Add(entry);
            await AddAsync(item, ct);
        }
    }

    private sealed class SpyStore : IObjectStore
    {
        public int Uploads { get; private set; }
        public byte[] LastBytes { get; private set; } = [];
        public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
        {
            Uploads++;
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, ct);
            LastBytes = memory.ToArray();
        }
        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NonSeekable(byte[] bytes) : Stream
    {
        private readonly MemoryStream _data = new(bytes, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _data.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _data.ReadAsync(buffer, ct);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
