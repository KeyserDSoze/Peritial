using System.ComponentModel;
using ModelContextProtocol.Server;
using Peritial.Business;
using Peritial.Domains;

namespace Peritial.Applications;

/// <summary>All tools use the exact same scoped services as the REST API.</summary>
[McpServerToolType]
public sealed class McpTools(
    PeriziaService perizie, FileIngestionService ingestion, DocumentCatalogService documents,
    SinistroService sinistri, PeriziaSinistroLinkService periziaSinistroLinks,
    LegacyXmlPreviewService xmlPreview, LegacyXmlImportPlanService xmlPlan)
{
    [McpServerTool(Name = "list_perizie"), Description("Lists perizie visible to the authenticated tenant (peritial.read).")]
    public Task<IReadOnlyList<PeriziaDto>> ListPerizie(CancellationToken ct = default) => perizie.ListAsync(ct);

    [McpServerTool(Name = "get_perizia"), Description("Gets a single perizia by ID within the signed tenant (peritial.read or peritial.write); foreign and missing IDs are indistinguishable.")]
    public Task<PeriziaDto> GetPerizia(Guid id, CancellationToken ct = default) =>
        perizie.GetAsync(id, ct);

    [McpServerTool(Name = "create_perizia"), Description("Creates a perizia in the authenticated tenant (peritial.write).")]
    public Task<PeriziaDto> CreatePerizia(string reference, CancellationToken ct = default) =>
        perizie.CreateAsync(reference, ct);

    [McpServerTool(Name = "assign_perizia"), Description("Assigns a registered perizia to an operator in the authenticated tenant (peritial.write).")]
    public Task<PeriziaDto> AssignPerizia(Guid id, string assignee, CancellationToken ct = default) =>
        perizie.AssignAsync(id, assignee, ct);

    [McpServerTool(Name = "advance_perizia"), Description("Advances a perizia one step; target must be exactly ToBeRevised, Revised, ToSend, Sent, or Billed (peritial.write).")]
    public Task<PeriziaDto> AdvancePerizia(Guid id, string target, CancellationToken ct = default)
    {
        // REST and MCP share an exact symbolic-state contract; Business
        // validates the actual transition, scope, tenant and audited CAS.
        return perizie.AdvanceAsync(id, PeriziaAdvanceTarget.Parse(target), ct);
    }

    [McpServerTool(Name = "cancel_perizia"), Description("Cancels a non-terminal perizia belonging to the authenticated tenant (peritial.write).")]
    public Task<PeriziaDto> CancelPerizia(Guid id, CancellationToken ct = default) =>
        perizie.CancelAsync(id, ct);

    [McpServerTool(Name = "get_perizia_sinistro"), Description("Returns a perizia's current sinistro association or null for an unlinked perizia in the signed tenant; foreign and missing perizie are indistinguishable. Requires peritial.read or peritial.write.")]
    public Task<PeriziaSinistroAssociationDto> GetPeriziaSinistro(
        Guid periziaId, CancellationToken ct = default) =>
        periziaSinistroLinks.GetPeriziaAssociationAsync(periziaId, ct);

    [McpServerTool(Name = "link_perizia_sinistro"), Description("V006: links with positive expectedLinkVersion from get_perizia_sinistro. Requires signed peritial.write, increments revision and writes audit/history in the same transaction. Missing version is rejected.")]
    public Task<PeriziaSinistroLinkDto> LinkPeriziaSinistro(
        Guid periziaId, Guid sinistroId, long expectedLinkVersion,
        CancellationToken ct = default) =>
        periziaSinistroLinks.LinkAsync(periziaId, sinistroId, expectedLinkVersion, ct);

    [McpServerTool(Name = "reparent_perizia_sinistro"), Description("V006: atomic reparent requires expectedSinistroId, newSinistroId AND positive expectedLinkVersion from get_perizia_sinistro. ABA returns 409. Requires signed peritial.write, tenant RLS and atomic audit/history; never unlinks transiently.")]
    public Task<PeriziaSinistroLinkDto> ReparentPeriziaSinistro(
        Guid periziaId, Guid expectedSinistroId, Guid newSinistroId,
        long expectedLinkVersion, CancellationToken ct = default) =>
        periziaSinistroLinks.ReparentAsync(periziaId, expectedSinistroId, newSinistroId,
            expectedLinkVersion, ct);

    [McpServerTool(Name = "unlink_perizia_sinistro"), Description("V006: unlink requires expectedSinistroId AND positive expectedLinkVersion read from get_perizia_sinistro; ABA returns conflict. Signed peritial.write, RLS and atomic audit/history. Does not delete cases.")]
    public Task<PeriziaSinistroAssociationDto> UnlinkPeriziaSinistro(
        Guid periziaId, Guid expectedSinistroId, long expectedLinkVersion,
        CancellationToken ct = default) =>
        periziaSinistroLinks.UnlinkAsync(periziaId, expectedSinistroId, expectedLinkVersion, ct);

    [McpServerTool(Name = "list_perizie_for_sinistro"), Description("Returns perizia UUIDs linked to a sinistro in the signed tenant. Requires peritial.read or peritial.write; foreign and missing sinistri are indistinguishable.")]
    public Task<IReadOnlyList<Guid>> ListPerizieForSinistro(
        Guid sinistroId, CancellationToken ct = default) =>
        periziaSinistroLinks.ListPeriziaIdsAsync(sinistroId, ct);

    [McpServerTool(Name = "list_sinistri"), Description("Lists up to 100 sinistri in the signed tenant (peritial.read or peritial.write); no tenant IDs disclosed.")]
    public Task<IReadOnlyList<SinistroDto>> ListSinistri(int limit = 50, CancellationToken ct = default) =>
        sinistri.ListAsync(limit, ct);

    [McpServerTool(Name = "page_sinistri"), Description("Pages the complete tenant Sinistri catalog with 1..50 rows, opaque nextCursor and deterministic date/UUID order. Requires peritial.read or peritial.write. The cursor is positional metadata, never an authorization token.")]
    public Task<SinistroPageDto> PageSinistri(
        string? cursor = null, int limit = 50, CancellationToken ct = default) =>
        sinistri.PageAsync(limit, cursor, ct);

    [McpServerTool(Name = "search_sinistri"), Description("Case-insensitive literal prefix autocomplete for external references in the signed tenant. Optional occurredFrom/occurredTo ISO dates and limit 1..50; requires peritial.read or peritial.write. SQL filters before limiting.")]
    public Task<IReadOnlyList<SinistroDto>> SearchSinistri(
        string prefix, DateOnly? occurredFrom = null, DateOnly? occurredTo = null,
        int limit = 10, CancellationToken ct = default) =>
        sinistri.SearchAsync(prefix, occurredFrom, occurredTo, limit, ct);

    [McpServerTool(Name = "search_sinistri_page"), Description("Keyset-pages a tenant-scoped literal-reference prefix search, with optional inclusive occurredFrom/occurredTo and limit 1..50. The opaque cursor is bound to those filters, never an authorization credential. Requires peritial.read or peritial.write.")]
    public Task<SinistroPageDto> SearchSinistriPage(
        string prefix, DateOnly? occurredFrom = null, DateOnly? occurredTo = null,
        int limit = 50, string? cursor = null, CancellationToken ct = default) =>
        sinistri.SearchPageAsync(prefix, occurredFrom, occurredTo, limit, cursor, ct);

    [McpServerTool(Name = "get_sinistro"), Description("Gets a sinistro belonging to the signed tenant; missing/foreign IDs are indistinguishable (peritial.read).")]
    public Task<SinistroDto> GetSinistro(Guid id, CancellationToken ct = default) =>
        sinistri.GetAsync(id, ct);

    [McpServerTool(Name = "create_sinistro"), Description("Creates a sinistro using signed tenant, required external reference and ISO calendar date; optional report date (peritial.write).")]
    public Task<SinistroDto> CreateSinistro(
        string externalReference, DateOnly occurredOn, DateOnly? reportedOn = null,
        CancellationToken ct = default) =>
        sinistri.CreateAsync(externalReference, occurredOn, reportedOn, ct);

    [McpServerTool(Name = "update_sinistro"), Description("Updates a sinistro using a required last-seen version; stale versions are rejected (peritial.write).")]
    public Task<SinistroDto> UpdateSinistro(
        Guid id, string externalReference, DateOnly occurredOn, long expectedVersion,
        DateOnly? reportedOn = null, CancellationToken ct = default) =>
        sinistri.UpdateAsync(id, externalReference, occurredOn, reportedOn, expectedVersion, ct);

    [McpServerTool(Name = "delete_sinistro"), Description("Development-only when the hard-delete gate is explicitly enabled; requires signed peritial.write AND peritial.sinistri.delete. Without both scopes and the gate, deletion is denied and audited. Retention/legal hold and production approval are not implemented. Linked perizie and stale expectedVersion conflict; SQL deletion and audit are atomic.")]
    public async Task<bool> DeleteSinistro(Guid id, long expectedVersion, CancellationToken ct = default)
    {
        await sinistri.DeleteAsync(id, expectedVersion, ct);
        return true;
    }

    [McpServerTool(Name = "list_documents"), Description("Lists up to 100 document metadata entries in the authenticated tenant (peritial.read or peritial.write); optional periziaId filter. Never exposes storage keys or file bytes.")]
    public Task<IReadOnlyList<DocumentSummaryDto>> ListDocuments(
        Guid? periziaId = null, int limit = 50, CancellationToken ct = default) =>
        documents.ListAsync(periziaId, limit, ct);

    [McpServerTool(Name = "page_documents"), Description("Keyset-pages 1..50 safe tenant document metadata entries, optional periziaId filter, with opaque filter-bound nextCursor. Requires peritial.read or peritial.write. Never exposes storage keys or file bytes.")]
    public Task<DocumentPageDto> PageDocuments(
        Guid? periziaId = null, int limit = 50, string? cursor = null,
        CancellationToken ct = default) =>
        documents.PageAsync(periziaId, limit, cursor, ct);

    [McpServerTool(Name = "get_document"), Description("Gets safe metadata for one document in the signed tenant (peritial.read or peritial.write); foreign and missing IDs are indistinguishable.")]
    public Task<DocumentSummaryDto> GetDocument(Guid id, CancellationToken ct = default) =>
        documents.GetAsync(id, ct);

    [McpServerTool(Name = "preview_legacy_xml"), Description("Validates and previews XML Classic / Classic Base / Accertatore without persisting, importing or decoding attachments. Requires signed peritial.write, max 1 MiB; returns format, case reference, calendar dates, attachment count and a boolean for unmapped business data; never returns attachment content.")]
    public LegacyXmlPreviewDto PreviewLegacyXml(string xml) =>
        xmlPreview.PreviewUtf8(xml);

    [McpServerTool(Name = "plan_legacy_xml"), Description("Read-only Classic/Accertatore import reconciliation: compares exact reference and dates within the signed tenant. Returns new/existing/review-required/conflict/ambiguous without writes. Review-required never contains a candidate UUID. Requires peritial.write; max 1 MiB UTF-8. Not authorization to import.")]
    public Task<LegacyXmlImportPlanDto> PlanLegacyXml(
        string xml, CancellationToken ct = default) =>
        xmlPlan.PlanUtf8Async(xml, ct);

    [McpServerTool(Name = "ingest_file"), Description("Uploads a PDF, XML, JPEG, PNG or DOCX document (base64, max 4 MiB, peritial.write).")]
    public Task<DocumentDto> IngestFile(
        string fileName, string contentType, string base64, Guid? periziaId = null,
        string? idempotencyKey = null, CancellationToken ct = default) =>
        ingestion.IngestBase64Async(fileName, contentType, base64, periziaId, idempotencyKey, ct);
}
