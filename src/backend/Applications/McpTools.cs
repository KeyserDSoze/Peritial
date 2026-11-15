using System.ComponentModel;
using ModelContextProtocol.Server;
using Peritial.Business;

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

    [McpServerTool(Name = "ingest_file"), Description("Uploads a PDF, XML, JPEG, PNG or DOCX document (base64, max 4 MiB, peritial.write).")]
    public Task<DocumentDto> IngestFile(
        string fileName, string contentType, string base64, Guid? periziaId = null,
        string? idempotencyKey = null, CancellationToken ct = default) =>
        ingestion.IngestBase64Async(fileName, contentType, base64, periziaId, idempotencyKey, ct);
}
