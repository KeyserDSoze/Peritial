using System.ComponentModel;
using ModelContextProtocol.Server;
using Peritial.Business;
using Peritial.Domains;

namespace Peritial.Applications;

/// <summary>All tools use the exact same scoped services as the REST API.</summary>
[McpServerToolType]
public sealed class McpTools(PeriziaService perizie, FileIngestionService ingestion)
{
    [McpServerTool(Name = "list_perizie"), Description("Lists perizie visible to the authenticated tenant (peritial.read).")]
    public Task<IReadOnlyList<PeriziaDto>> ListPerizie(CancellationToken ct = default) => perizie.ListAsync(ct);

    [McpServerTool(Name = "create_perizia"), Description("Creates a perizia in the authenticated tenant (peritial.write).")]
    public Task<PeriziaDto> CreatePerizia(string reference, CancellationToken ct = default) =>
        perizie.CreateAsync(reference, ct);

    [McpServerTool(Name = "assign_perizia"), Description("Assigns a registered perizia to an operator in the authenticated tenant (peritial.write).")]
    public Task<PeriziaDto> AssignPerizia(Guid id, string assignee, CancellationToken ct = default) =>
        perizie.AssignAsync(id, assignee, ct);

    [McpServerTool(Name = "advance_perizia"), Description("Advances a perizia one step; target must be exactly ToBeRevised, Revised, ToSend, Sent, or Billed (peritial.write).")]
    public Task<PeriziaDto> AdvancePerizia(Guid id, string target, CancellationToken ct = default)
    {
        // MCP transport accepts only documented symbolic state names; no
        // JSON numeric enum values, arbitrary state injection or tenant input.
        // The Business/domain layer still validates the actual transition
        // and enforces signed-tenant scope + atomic audited PostgreSQL CAS.
        if (target is not ("ToBeRevised" or "Revised" or "ToSend" or "Sent" or "Billed"))
            throw new ArgumentException("Invalid target perizia status.", nameof(target));
        return perizie.AdvanceAsync(id, Enum.Parse<PeriziaStatus>(target), ct);
    }

    [McpServerTool(Name = "cancel_perizia"), Description("Cancels a non-terminal perizia belonging to the authenticated tenant (peritial.write).")]
    public Task<PeriziaDto> CancelPerizia(Guid id, CancellationToken ct = default) =>
        perizie.CancelAsync(id, ct);

    [McpServerTool(Name = "ingest_file"), Description("Uploads a PDF, XML, JPEG, PNG or DOCX document (base64, max 4 MiB, peritial.write).")]
    public Task<DocumentDto> IngestFile(
        string fileName, string contentType, string base64, Guid? periziaId = null,
        string? idempotencyKey = null, CancellationToken ct = default) =>
        ingestion.IngestBase64Async(fileName, contentType, base64, periziaId, idempotencyKey, ct);
}
