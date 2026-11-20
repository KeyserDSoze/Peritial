using System.Text;
using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class LegacyXmlImportPlanTests
{
    private static readonly byte[] Sample = Encoding.UTF8.GetBytes(
        "<it.autosoftws.IncaricoAccertatoreRecord>" +
        "<NumeroSinistro>SAME-REF</NumeroSinistro>" +
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
    public async Task Rich_legacy_identity_is_never_claimed_to_match_on_number_and_date_alone()
    {
        var actor = new Actor(true);
        var lookup = new SpyLookup();
        var id = Guid.NewGuid();
        lookup.Add(actor.TenantId, "SAME-REF", id,
            new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 18));
        var svc = new LegacyXmlImportPlanService(actor,
            new LegacyXmlPreviewService(actor), lookup);

        // Actual Classic/Accertatore imports can carry a company, policy,
        // contractor and files; V003 has none of these identity fields.
        // An exact reference and matching date CANNOT establish identity.
        var rich = Encoding.UTF8.GetBytes(
            "<it.autosoftws.IncaricoAccertatoreRecord>" +
            "<NumeroSinistro>SAME-REF</NumeroSinistro>" +
            "<DataOraSinistro>2026-11-17</DataOraSinistro>" +
            "<DataIncarico>2026-11-18</DataIncarico>" +
            "<Compagnia><NomeCompagnia>SECRET-INSURER</NomeCompagnia></Compagnia>" +
            "<Polizza><Numero>SECRET-POLICY</Numero></Polizza>" +
            "<Contraente><Nome>SECRET-PERSON</Nome></Contraente>" +
            "</it.autosoftws.IncaricoAccertatoreRecord>");
        var plan = await svc.PlanAsync(rich);
        Assert.True(plan.Preview.HasUnmappedBusinessData);
        Assert.Equal("review-required", plan.Status);
        Assert.Null(plan.ExistingSinistroId);
        var serialized = System.Text.Json.JsonSerializer.Serialize(plan);
        Assert.DoesNotContain(id.ToString(), serialized);
        Assert.DoesNotContain("SECRET", serialized);
        Assert.Equal(1, lookup.Calls);

        // Minimal XML still has the previous reference/date-only advisory
        // behavior, but it is NOT a license to import rich data.
        var simple = await svc.PlanAsync(Sample);
        Assert.False(simple.Preview.HasUnmappedBusinessData);
        Assert.Equal("existing", simple.Status);
        Assert.Equal(id, simple.ExistingSinistroId);

        lookup.Clear();
        // No candidate remains "new", still with a conspicuous warning flag.
        var noMatch = await svc.PlanAsync(rich);
        Assert.Equal("new", noMatch.Status);
        Assert.True(noMatch.Preview.HasUnmappedBusinessData);
        Assert.Null(noMatch.ExistingSinistroId);
    }

    [Theory]
    [InlineData("2026-11-17 12:40:15", "2026-11-18")]
    [InlineData("2026-11-17", "2026-11-18 00:00:00")]
    public async Task Timestamp_precision_requires_review_even_for_a_single_matching_case(
        string occurred, string reported)
    {
        var actor = new Actor(true);
        var lookup = new SpyLookup();
        var id = Guid.NewGuid();
        lookup.Add(actor.TenantId, "SAME-REF", id,
            new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 18));
        var xml = Encoding.UTF8.GetBytes(
            "<it.autosoftws.IncaricoAccertatoreRecord>" +
            "<NumeroSinistro>SAME-REF</NumeroSinistro>" +
            "<DataOraSinistro>" + occurred + "</DataOraSinistro>" +
            "<DataIncarico>" + reported + "</DataIncarico>" +
            "</it.autosoftws.IncaricoAccertatoreRecord>");
        var plan = await new LegacyXmlImportPlanService(actor,
            new LegacyXmlPreviewService(actor), lookup).PlanAsync(xml);
        Assert.Equal("review-required", plan.Status);
        Assert.True(plan.Preview.HasUnmappedBusinessData);
        Assert.Null(plan.ExistingSinistroId);
        Assert.DoesNotContain(id.ToString(),
            System.Text.Json.JsonSerializer.Serialize(plan));
    }

    [Fact]
    public async Task Whitespace_normalized_reference_must_not_claim_existing_identity()
    {
        var actor = new Actor(true);
        var lookup = new SpyLookup();
        var id = Guid.NewGuid();
        lookup.Add(actor.TenantId, "SAME-REF", id,
            new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 18));
        var xml = Encoding.UTF8.GetBytes(
            "<it.autosoftws.ReIncaricoRecord>" +
            "<NumeroSinistro> SAME-REF </NumeroSinistro>" +
            "<DataOraSinistro>2026-11-17</DataOraSinistro>" +
            "<DataIncarico>2026-11-18</DataIncarico>" +
            "</it.autosoftws.ReIncaricoRecord>");
        var plan = await new LegacyXmlImportPlanService(actor,
            new LegacyXmlPreviewService(actor), lookup).PlanAsync(xml);
        Assert.Equal("review-required", plan.Status);
        Assert.True(plan.Preview.HasUnmappedBusinessData);
        Assert.Null(plan.ExistingSinistroId);
        Assert.DoesNotContain(id.ToString(),
            System.Text.Json.JsonSerializer.Serialize(plan));
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
