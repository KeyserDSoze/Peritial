using System.ComponentModel;
using ModelContextProtocol.Server;
using Peritial.Business;
using Peritial.Domains;

namespace Peritial.Applications;

/// <summary>All tools use the exact same scoped services as the REST API.</summary>
[McpServerToolType]
public sealed class McpTools(
    PeriziaService perizie, FileIngestionService ingestion, DocumentCatalogService documents, SinistroService sinistri)
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

    [McpServerTool(Name = "list_sinistri"), Description("Lists up to 100 sinistri in the signed tenant (peritial.read or peritial.write); no tenant IDs disclosed.")]
    public Task<IReadOnlyList<SinistroDto>> ListSinistri(int limit = 50, CancellationToken ct = default) =>
        sinistri.ListAsync(limit, ct);

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

    [McpServerTool(Name = "list_documents"), Description("Lists up to 100 document metadata entries in the authenticated tenant (peritial.read or peritial.write); optional periziaId filter. Never exposes storage keys or file bytes.")]
    public Task<IReadOnlyList<DocumentSummaryDto>> ListDocuments(
        Guid? periziaId = null, int limit = 50, CancellationToken ct = default) =>
        documents.ListAsync(periziaId, limit, ct);

    [McpServerTool(Name = "get_document"), Description("Gets safe metadata for one document in the signed tenant (peritial.read or peritial.write); foreign and missing IDs are indistinguishable.")]
    public Task<DocumentSummaryDto> GetDocument(Guid id, CancellationToken ct = default) =>
        documents.GetAsync(id, ct);

    [McpServerTool(Name = "ingest_file"), Description("Uploads a PDF, XML, JPEG, PNG or DOCX document (base64, max 4 MiB, peritial.write).")]
    public Task<DocumentDto> IngestFile(
        string fileName, string contentType, string base64, Guid? periziaId = null,
        string? idempotencyKey = null, CancellationToken ct = default) =>
        ingestion.IngestBase64Async(fileName, contentType, base64, periziaId, idempotencyKey, ct);
}
