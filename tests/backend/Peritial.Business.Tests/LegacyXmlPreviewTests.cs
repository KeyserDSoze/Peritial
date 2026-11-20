using System.Text;
using System.Text.Json;
using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class LegacyXmlPreviewTests
{
    private static readonly LegacyXmlPreviewService Service = new(new Actor(true));

    [Theory]
    [InlineData("it.autosoftws.ReIncaricoRecord")]
    [InlineData("it.autosoftws.ReIncaricoBaseRecord")]
    public void Classic_roots_detect_essential_values_and_attachment_count(string root)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <{root}>
              <NumeroSinistro> SIN-001 </NumeroSinistro>
              <DataOraSinistro>2026-11-19 18:44:20</DataOraSinistro>
              <DataIncarico>2026-11-20</DataIncarico>
              <Contraente><Nome>PRIVATE NAME</Nome></Contraente>
              <allegati>
                <it.autosoftws.AllegatoRecord><Nome>PRIVATE.pdf</Nome><Allegato>UERG</Allegato></it.autosoftws.AllegatoRecord>
                <it.autosoftws.AllegatoRecord><Nome>PRIVATE-2.pdf</Nome></it.autosoftws.AllegatoRecord>
              </allegati>
            </{root}>
            """;
        var result = Service.Preview(Encoding.UTF8.GetBytes(xml));
        Assert.Equal("classic", result.Format);
        Assert.Equal("SIN-001", result.ExternalReference);
        Assert.Equal(new DateOnly(2026, 11, 19), result.OccurredOn);
        Assert.Equal(new DateOnly(2026, 11, 20), result.ReportedOn);
        Assert.Equal(2, result.AttachmentCount);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("PRIVATE", json);
        Assert.DoesNotContain("UERG", json);
        Assert.DoesNotContain("Tenant", json);
    }

    [Fact]
    public void Accertatore_detects_and_validates_optional_date()
    {
        const string xml = """
            <it.autosoftws.IncaricoAccertatoreRecord>
              <NumeroSinistro>ACC-987</NumeroSinistro>
              <DataOraSinistro>2026-11-18</DataOraSinistro>
              <Compagnia><NomeCompagnia>Example</NomeCompagnia></Compagnia>
              <allegati><it.autosoftws.AllegatoRecord><Allegato>SECRET</Allegato></it.autosoftws.AllegatoRecord></allegati>
            </it.autosoftws.IncaricoAccertatoreRecord>
            """;
        var result = Service.Preview(Encoding.UTF8.GetBytes(xml));
        Assert.Equal("accertatore", result.Format);
        Assert.Equal("ACC-987", result.ExternalReference);
        Assert.Equal(new DateOnly(2026, 11, 18), result.OccurredOn);
        Assert.Null(result.ReportedOn);
        Assert.Equal(1, result.AttachmentCount);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Preview_exposes_only_boolean_unmapped_data_signal_never_business_values()
    {
        const string minimal =
            "<it.autosoftws.ReIncaricoRecord>" +
            "<NumeroSinistro>SIN-001</NumeroSinistro>" +
            "<DataOraSinistro>2026-11-19</DataOraSinistro>" +
            "</it.autosoftws.ReIncaricoRecord>";
        Assert.False(Service.Preview(Encoding.UTF8.GetBytes(minimal))
            .HasUnmappedBusinessData);

        const string rich =
            "<it.autosoftws.ReIncaricoRecord custom='PRIVATE-ATTRIBUTE'>" +
            "<NumeroSinistro>SIN-001</NumeroSinistro>" +
            "<DataOraSinistro>2026-11-19</DataOraSinistro>" +
            "<Assicurato><Compagnia><NomeCompagnia>PRIVATE-INSURER</NomeCompagnia>" +
            "</Compagnia></Assicurato>" +
            "</it.autosoftws.ReIncaricoRecord>";
        var preview = Service.Preview(Encoding.UTF8.GetBytes(rich));
        Assert.True(preview.HasUnmappedBusinessData);
        var serialized = JsonSerializer.Serialize(preview);
        Assert.DoesNotContain("PRIVATE-ATTRIBUTE", serialized);
        Assert.DoesNotContain("PRIVATE-INSURER", serialized);
        Assert.DoesNotContain("Assicurato", serialized);
    }

    [Theory]
    [InlineData("<unknown><NumeroSinistro>X</NumeroSinistro><DataOraSinistro>2026-11-18</DataOraSinistro></unknown>")]
    [InlineData("<it.autosoftws.ReIncaricoRecord xmlns='urn:other'><NumeroSinistro>X</NumeroSinistro><DataOraSinistro>2026-11-18</DataOraSinistro></it.autosoftws.ReIncaricoRecord>")]
    [InlineData("<it.autosoftws.ReIncaricoRecord><NumeroSinistro>X</NumeroSinistro><DataOraSinistro>2026-11-18</DataOraSinistro>")]
    [InlineData("<!DOCTYPE it.autosoftws.ReIncaricoRecord [<!ENTITY p 'evil'>]><it.autosoftws.ReIncaricoRecord><NumeroSinistro>&p;</NumeroSinistro><DataOraSinistro>2026-11-18</DataOraSinistro></it.autosoftws.ReIncaricoRecord>")]
    [InlineData("<!DOCTYPE it.autosoftws.ReIncaricoRecord SYSTEM 'file:///etc/passwd'><it.autosoftws.ReIncaricoRecord><NumeroSinistro>X</NumeroSinistro><DataOraSinistro>2026-11-18</DataOraSinistro></it.autosoftws.ReIncaricoRecord>")]
    [InlineData("<it.autosoftws.ReIncaricoRecord><NumeroSinistro>A</NumeroSinistro><NumeroSinistro>B</NumeroSinistro><DataOraSinistro>2026-11-18</DataOraSinistro></it.autosoftws.ReIncaricoRecord>")]
    [InlineData("<it.autosoftws.ReIncaricoRecord><NumeroSinistro> </NumeroSinistro><DataOraSinistro>2026-11-18</DataOraSinistro></it.autosoftws.ReIncaricoRecord>")]
    [InlineData("<it.autosoftws.ReIncaricoRecord><NumeroSinistro>A</NumeroSinistro><DataOraSinistro>not-a-date</DataOraSinistro></it.autosoftws.ReIncaricoRecord>")]
    [InlineData("<it.autosoftws.ReIncaricoRecord><NumeroSinistro>A</NumeroSinistro><DataOraSinistro>2026-02-30</DataOraSinistro></it.autosoftws.ReIncaricoRecord>")]
    [InlineData("<it.autosoftws.ReIncaricoRecord><NumeroSinistro><value>A</value></NumeroSinistro><DataOraSinistro>2026-11-18</DataOraSinistro></it.autosoftws.ReIncaricoRecord>")]
    [InlineData("<it.autosoftws.ReIncaricoRecord><Other><NumeroSinistro>A</NumeroSinistro></Other><DataOraSinistro>2026-11-18</DataOraSinistro></it.autosoftws.ReIncaricoRecord>")]
    [InlineData("<it.autosoftws.ReIncaricoRecord><NumeroSinistro>A</NumeroSinistro><DataOraSinistro>2026-11-18</DataOraSinistro><DataIncarico>yesterday</DataIncarico></it.autosoftws.ReIncaricoRecord>")]
    [InlineData("<it.autosoftws.ReIncaricoRecord><NumeroSinistro>A</NumeroSinistro><DataOraSinistro>2026-11-18</DataOraSinistro><allegati><wrong/></allegati></it.autosoftws.ReIncaricoRecord>")]
    public void Invalid_xml_and_invalid_field_shapes_fail_closed(string xml)
    {
        Assert.Throws<ArgumentException>(() => Service.Preview(Encoding.UTF8.GetBytes(xml)));
    }

    [Fact]
    public void Budget_and_authorization_are_enforced_before_parsing()
    {
        var tooLarge = new byte[LegacyXmlPreviewService.MaxXmlBytes + 1];
        Assert.Throws<ArgumentException>(() => Service.Preview(tooLarge));
        Assert.Throws<ArgumentException>(() => Service.Preview(ReadOnlyMemory<byte>.Empty));

        var denied = new LegacyXmlPreviewService(new Actor(false));
        Assert.Throws<UnauthorizedAccessException>(() =>
            denied.Preview(Encoding.UTF8.GetBytes("not XML")));
        Assert.Throws<UnauthorizedAccessException>(() =>
            denied.Preview(Encoding.UTF8.GetBytes(
                "<it.autosoftws.ReIncaricoRecord/>")));
    }

    private sealed class Actor(bool write) : ITenantContext
    {
        public TenantId TenantId { get; } = TenantId.From(Guid.NewGuid());
        public string Subject => "signed-actor";
        public bool HasScope(string scope) => write && scope == "peritial.write";
    }
}
