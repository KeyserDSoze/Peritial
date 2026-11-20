using System.Text;
using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class LegacyXmlCaseImportTests
{
    private static byte[] Xml(string reference = "IMP-001", string extra = "") =>
        Encoding.UTF8.GetBytes(
            "<it.autosoftws.ReIncaricoRecord>" +
            "<NumeroSinistro>" + reference + "</NumeroSinistro>" +
            "<DataOraSinistro>2026-11-19</DataOraSinistro>" +
            "<DataIncarico>2026-11-20</DataIncarico>" +
            extra + "</it.autosoftws.ReIncaricoRecord>");

    [Fact]
    public async Task Commit_has_validated_case_only_facts_and_hashed_payload_before_repository()
    {
        var actor = new Actor(true);
        var store = new Store();
        var service = new LegacyXmlCaseImportService(
            actor, new LegacyXmlPreviewService(actor), store);
        const string key = "test-case-import-0001";

        var response = await service.CommitAsync(Xml(), key);
        Assert.Equal("created", response.Status);
        Assert.Equal(store.Id, response.SinistroId);
        Assert.Equal("IMP-001", response.Preview.ExternalReference);
        Assert.Equal(1, store.Calls);
        Assert.Equal(actor.TenantId, store.Tenant);
        Assert.Equal(actor.Subject, store.Subject);
        Assert.Equal(key, store.Key);
        Assert.Equal(32, store.RequestDigest!.Length);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(Xml()), store.RequestDigest);

        store.Replay = true;
        var again = await service.CommitAsync(Xml(), key);
        Assert.Equal("replayed", again.Status);
        Assert.Equal(store.Id, again.SinistroId);
    }

    [Theory]
    [InlineData("<Contraente><Nome>SECRET-PERSON</Nome></Contraente>")]
    [InlineData("<Compagnia><NomeCompagnia>SECRET-COMPANY</NomeCompagnia></Compagnia>")]
    [InlineData("<allegati><it.autosoftws.AllegatoRecord><Allegato>UERG</Allegato></it.autosoftws.AllegatoRecord></allegati>")]
    [InlineData("<Danneggiati/>")]
    [InlineData("<Unexpected>IGNORED-IF-UNSAFE</Unexpected>")]
    public async Task Rich_legacy_xml_is_rejected_without_a_silent_partial_import(string extra)
    {
        var actor = new Actor(true);
        var store = new Store();
        var service = new LegacyXmlCaseImportService(
            actor, new LegacyXmlPreviewService(actor), store);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CommitAsync(Xml(extra: extra), "case-only-key-012345678"));
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("2026-11-19 18:44:20", "2026-11-20")]
    [InlineData("2026-11-19 00:00:00", "2026-11-20")]
    [InlineData("2026-11-19", "2026-11-20 09:30:15")]
    public async Task Case_only_commit_never_truncates_a_legacy_timestamp(
        string occurred, string reported)
    {
        var actor = new Actor(true);
        var store = new Store();
        var svc = new LegacyXmlCaseImportService(
            actor, new LegacyXmlPreviewService(actor), store);
        var xml = Encoding.UTF8.GetBytes(
            "<it.autosoftws.ReIncaricoRecord>" +
            "<NumeroSinistro>IMP-001</NumeroSinistro>" +
            "<DataOraSinistro>" + occurred + "</DataOraSinistro>" +
            "<DataIncarico>" + reported + "</DataIncarico>" +
            "</it.autosoftws.ReIncaricoRecord>");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CommitAsync(xml, "time-truncation-key-0001"));
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("<!--PRIVATE-CLAIM-METADATA-->")]
    [InlineData("<?routing private='claim'?>")]
    [InlineData("<NumeroSinistro>EXTRA</NumeroSinistro><!--SECRET-->")]
    public async Task Case_only_commit_rejects_comments_and_pi_instead_of_dropping_them(
        string metadata)
    {
        var actor = new Actor(true);
        var store = new Store();
        var svc = new LegacyXmlCaseImportService(
            actor, new LegacyXmlPreviewService(actor), store);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CommitAsync(Xml(extra: metadata), "hidden-metadata-key-0001"));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task No_scope_invalid_key_and_unsafe_xml_stop_before_write()    [Fact]
    public async Task No_scope_invalid_key_and_unsafe_xml_stop_before_write()
    {
        var deniedActor = new Actor(false);
        var deniedStore = new Store();
        var denied = new LegacyXmlCaseImportService(deniedActor,
            new LegacyXmlPreviewService(deniedActor), deniedStore);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            denied.CommitAsync(Xml(), "valid-idempotency-key-001"));
        Assert.Equal(0, deniedStore.Calls);

        var actor = new Actor(true);
        var store = new Store();
        var svc = new LegacyXmlCaseImportService(actor,
            new LegacyXmlPreviewService(actor), store);
        foreach (var key in new[] { "", "short", "invalid key with spaces",
            "not-valid-ümlaut-1234", new string('a', 129) })
            await Assert.ThrowsAsync<ArgumentException>(() =>
                svc.CommitAsync(Xml(), key));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CommitAsync(Encoding.UTF8.GetBytes(
                "<!DOCTYPE it.autosoftws.ReIncaricoRecord SYSTEM 'file:///etc/passwd'>" +
                "<it.autosoftws.ReIncaricoRecord><NumeroSinistro>IMP-001</NumeroSinistro>" +
                "<DataOraSinistro>2026-11-19</DataOraSinistro></it.autosoftws.ReIncaricoRecord>"),
                "valid-idempotency-key-001"));
        Assert.Equal(0, store.Calls);
    }

    private sealed class Actor(bool canWrite) : ITenantContext
    {
        public TenantId TenantId { get; } = TenantId.From(Guid.NewGuid());
        public string Subject => "signed-test-actor";
        public bool HasScope(string scope) => canWrite && scope == "peritial.write";
    }

    private sealed class Store : ILegacyXmlCaseImportStore
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool Replay { get; set; }
        public int Calls { get; private set; }
        public TenantId Tenant { get; private set; }
        public string? Subject { get; private set; }
        public string? Key { get; private set; }
        public byte[]? RequestDigest { get; private set; }

        public Task<LegacyXmlCaseImportResult> CommitAsync(TenantId tenant,
            string subject, string key, byte[] requestDigest,
            LegacyXmlPreviewDto preview, CancellationToken ct = default)
        {
            Calls++;
            Tenant = tenant;
            Subject = subject;
            Key = key;
            RequestDigest = requestDigest;
            Assert.Equal("IMP-001", preview.ExternalReference);
            return Task.FromResult(new LegacyXmlCaseImportResult(Id, Replay));
        }
    }
}
