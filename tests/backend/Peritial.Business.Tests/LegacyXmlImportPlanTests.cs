using System.Text;
using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class LegacyXmlImportPlanTests
{
    private static readonly byte[] Sample = Encoding.UTF8.GetBytes(
        "<it.autosoftws.IncaricoAccertatoreRecord>" +
        "<NumeroSinistro> SAME-REF </NumeroSinistro>" +
        "<DataOraSinistro>2026-11-17</DataOraSinistro>" +
        "<DataIncarico>2026-11-18</DataIncarico>" +
        "</it.autosoftws.IncaricoAccertatoreRecord>");

    [Fact]
    public async Task New_reference_has_no_match_and_no_mutations()
    {
        var actor = new Actor(true);
        var lookup = new SpyLookup();
        var plan = await new LegacyXmlImportPlanService(actor,
            new LegacyXmlPreviewService(actor), lookup).PlanAsync(Sample);
        Assert.Equal("new", plan.Status);
        Assert.Null(plan.ExistingSinistroId);
        Assert.Equal("SAME-REF", plan.Preview.ExternalReference);
        Assert.Equal(actor.TenantId, lookup.LastTenant);
        Assert.Equal("SAME-REF", lookup.LastReference);
        Assert.Equal(1, lookup.Calls);
    }

    [Fact]
    public async Task Existing_case_requires_matching_date_but_optional_report_date_is_not_authoritative()
    {
        var actor = new Actor(true);
        var lookup = new SpyLookup();
        var id = Guid.NewGuid();
        lookup.Add(actor.TenantId, "SAME-REF", id,
            new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 18));
        var svc = new LegacyXmlImportPlanService(actor,
            new LegacyXmlPreviewService(actor), lookup);
        var matched = await svc.PlanAsync(Sample);
        Assert.Equal("existing", matched.Status);
        Assert.Equal(id, matched.ExistingSinistroId);

        lookup.Clear();
        lookup.Add(actor.TenantId, "SAME-REF", id,
            new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 16));
        var conflict = await svc.PlanAsync(Sample);
        Assert.Equal("conflict", conflict.Status);
        Assert.Null(conflict.ExistingSinistroId);
        Assert.DoesNotContain(id.ToString(), System.Text.Json.JsonSerializer.Serialize(conflict));

        var noReport = Encoding.UTF8.GetBytes(
            "<it.autosoftws.ReIncaricoRecord><NumeroSinistro>SAME-REF</NumeroSinistro>" +
            "<DataOraSinistro>2026-11-17</DataOraSinistro></it.autosoftws.ReIncaricoRecord>");
        var acceptable = await svc.PlanAsync(noReport);
        Assert.Equal("existing", acceptable.Status);
        Assert.Equal(id, acceptable.ExistingSinistroId);
    }

    [Fact]
    public async Task Ambiguous_and_foreign_candidates_must_not_select_an_id()
    {
        var actor = new Actor(true);
        var lookup = new SpyLookup();
        lookup.Add(TenantId.From(Guid.NewGuid()), "SAME-REF", Guid.NewGuid(),
            new DateOnly(2026, 11, 17), null);
        var svc = new LegacyXmlImportPlanService(actor,
            new LegacyXmlPreviewService(actor), lookup);
        Assert.Equal("new", (await svc.PlanAsync(Sample)).Status);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        lookup.Add(actor.TenantId, "SAME-REF", a, new DateOnly(2026, 11, 17), null);
        lookup.Add(actor.TenantId, "SAME-REF", b, new DateOnly(2026, 11, 16), null);
        var ambiguous = await svc.PlanAsync(Sample);
        Assert.Equal("ambiguous", ambiguous.Status);
        Assert.Null(ambiguous.ExistingSinistroId);
        Assert.DoesNotContain(a.ToString(), System.Text.Json.JsonSerializer.Serialize(ambiguous));
        Assert.DoesNotContain(b.ToString(), System.Text.Json.JsonSerializer.Serialize(ambiguous));
        Assert.Equal(2, lookup.LastLimit);
    }

    [Fact]
    public async Task Unauthorized_and_invalid_xml_stop_before_database_access()
    {
        var noScope = new Actor(false);
        var deniedLookup = new SpyLookup();
        var denied = new LegacyXmlImportPlanService(noScope,
            new LegacyXmlPreviewService(noScope), deniedLookup);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.PlanAsync(Sample));
        Assert.Equal(0, deniedLookup.Calls);

        var actor = new Actor(true);
        var lookup = new SpyLookup();
        var service = new LegacyXmlImportPlanService(actor,
            new LegacyXmlPreviewService(actor), lookup);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.PlanAsync(Encoding.UTF8.GetBytes("<unknown/>")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.PlanAsync(new byte[LegacyXmlPreviewService.MaxXmlBytes + 1]));
        Assert.Equal(0, lookup.Calls);
    }

    private sealed class Actor(bool write) : ITenantContext
    {
        public TenantId TenantId { get; } = TenantId.From(Guid.NewGuid());
        public string Subject => "signed-plan-tester";
        public bool HasScope(string scope) => write && scope == "peritial.write";
    }

    private sealed class SpyLookup : ILegacyImportReferenceLookup
    {
        private readonly List<(TenantId Tenant, string Reference, LegacySinistroMatch Match)> data = [];
        public TenantId LastTenant { get; private set; }
        public string? LastReference { get; private set; }
        public int Calls { get; private set; }
        public int LastLimit { get; private set; }
        public void Add(TenantId tenant, string reference, Guid id, DateOnly occurred,
            DateOnly? reported) => data.Add((tenant, reference, new(id, occurred, reported)));
        public void Clear() => data.Clear();
        public Task<IReadOnlyList<LegacySinistroMatch>> FindExactAsync(
            TenantId tenant, string reference, int limit, CancellationToken ct = default)
        {
            Calls++;
            LastTenant = tenant;
            LastReference = reference;
            LastLimit = limit;
            return Task.FromResult<IReadOnlyList<LegacySinistroMatch>>(data
                .Where(x => x.Tenant == tenant &&
                    string.Equals(x.Reference, reference, StringComparison.Ordinal))
                .Take(limit).Select(x => x.Match).ToArray());
        }
    }
}
