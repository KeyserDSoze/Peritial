using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Server;
using Npgsql;
using Peritial.Applications;
using Peritial.Business;
using Peritial.Domains;
using Peritial.Infrastructures;

var builder = WebApplication.CreateBuilder(args);
// Disabled by default. HTTPS collector required outside Development; do not
// enable generic ASP.NET instrumentation or export logs/user request payloads.
var otlp = ReadinessOtlpSettings.FromConfiguration(
    builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddReadinessOtlpExporter(otlp);
builder.Services.AddHttpContextAccessor();
// Reject oversized multipart sections before they reach the Business ingestion service.
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = FileIngestionService.MaxUploadBytes;
    options.MultipartHeadersLengthLimit = 16 * 1024;
});

var authIssuer = builder.Configuration["Auth:Authority"] ?? throw new InvalidOperationException("Auth:Authority required.");
var audience = builder.Configuration["Auth:Audience"] ?? throw new InvalidOperationException("Auth:Audience required.");
var publicResource = builder.Configuration["Auth:Resource"] ?? "http://localhost:8080/mcp";
if (string.IsNullOrWhiteSpace(builder.Configuration["Malware:Clamd:Host"]))
    throw new InvalidOperationException("A private ClamAV malware scanner must be configured.");
// Production cannot silently accept a scanner whose loaded signature database
// age is unknown. Development Compose deliberately uses preloaded, non-updating
// signatures; only that environment may omit the explicit freshness budget.
var clamdSignatures = ClamdSignaturePolicy.FromConfiguration(
    builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddSingleton(clamdSignatures);
var metadata = new OAuthProtectedResource(publicResource, builder.Environment.IsDevelopment());

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authIssuer;
        options.Audience = audience;
        options.MapInboundClaims = false;
        if (builder.Configuration["Auth:DevelopmentBackchannelOrigin"] is { Length: > 0 } internalOrigin)
        {
            if (!builder.Environment.IsDevelopment())
                throw new InvalidOperationException("Development backchannel rewrite is prohibited outside Development.");
            options.BackchannelHttpHandler = new OidcDevelopmentBackchannelHandler(authIssuer, internalOrigin);
        }
        options.RequireHttpsMetadata = builder.Configuration.GetValue("Auth:RequireHttpsMetadata", true);
        if (builder.Configuration["Auth:MetadataAddress"] is { Length: > 0 } issuerMetadataAddress)
            options.MetadataAddress = issuerMetadataAddress;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
            ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                // Reject structurally valid tokens lacking a signed tenant/subject
                // at authentication time, before endpoint selection or tool dispatch.
                if (!OAuthSecurity.HasSignedTenantAndSubject(context.Principal))
                {
                    var principal = context.Principal;
                    context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Peritial.OAuth")
                        .LogWarning(
                            "JWT rejected (claims only): tenantClaim={TenantPresent}, subClaim={SubPresent}, nameIdentifier={NamePresent}, authenticated={Authenticated}",
                            principal?.HasClaim(claim => claim.Type == "tenant_id") == true,
                            principal?.HasClaim(claim => claim.Type == "sub") == true,
                            principal?.HasClaim(claim => claim.Type == System.Security.Claims.ClaimTypes.NameIdentifier) == true,
                            principal?.Identity?.IsAuthenticated == true);
                    context.Fail("A valid signed tenant_id and subject claim are required.");
                }
                return Task.CompletedTask;
            },
            OnForbidden = async context =>
            {
                // ForbiddenContext.Principal is not populated by the JwtBearer
                // handler; HttpContext.User is the authenticated, validated actor.
                // Never persist HTTP payload, file content or bearer tokens.
                if (Guid.TryParse(context.HttpContext.User.FindFirst("tenant_id")?.Value, out var tenant) &&
                    tenant != Guid.Empty &&
                    context.HttpContext.User.FindFirst("sub")?.Value is { Length: > 0 } actor)
                {
                    await context.HttpContext.RequestServices
                        .GetRequiredService<ISecurityAuditTrail>()
                        .AppendAsync(new SecurityAuditEvent(
                            Guid.NewGuid(), TenantId.From(tenant), actor, "authorization.forbidden",
                            "denied", null, DateTime.UtcNow), context.HttpContext.RequestAborted);
                }
            },
            OnChallenge = context =>
            {
                if (context.Request.Path.StartsWithSegments("/mcp"))
                    context.Response.Headers.Append("WWW-Authenticate",
                        $"Bearer resource_metadata=\"{metadata.McpMetadataUrl}\"");
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("peritial.read", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(context => OAuthSecurity.HasAnyScope(
            context.User, "peritial.read", "peritial.write")));
    options.AddPolicy("peritial.write", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(context => OAuthSecurity.HasAnyScope(context.User, "peritial.write")));
    options.AddPolicy("peritial.access", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(context => OAuthSecurity.HasAnyScope(
            context.User, "peritial.read", "peritial.write")));
});

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<IPeriziaRepository, PgPeriziaRepository>();
builder.Services.AddScoped<ISinistroRepository, PgSinistroRepository>();
builder.Services.AddScoped<IPeriziaSinistroLinkRepository, PgPeriziaSinistroLinkRepository>();
builder.Services.AddScoped<IDocumentRepository, PgDocumentRepository>();
builder.Services.AddScoped<IDocumentCatalogRepository, PgDocumentCatalogRepository>();
builder.Services.AddScoped<IDocumentDownloadRepository, PgDocumentCatalogRepository>();
builder.Services.AddScoped<DocumentCatalogService>();
builder.Services.AddScoped<DocumentDownloadService>();
builder.Services.AddScoped<IUploadIntentLedger, PgUploadIntentLedger>();
builder.Services.AddScoped<IUploadIdempotencyStore, PgUploadIdempotencyStore>();
builder.Services.AddScoped<ISecurityAuditTrail, PgSecurityAuditTrail>();
builder.Services.AddScoped<PeriziaService>();
// Fail closed everywhere by default, including local Development. The
// explicit flag is only accepted in the disposable demo/test environment;
// production fails startup rather than silently enabling hard deletion.
builder.Services.AddSingleton(SinistroDeletionGate.ForEnvironment(
    builder.Environment.IsDevelopment(),
    builder.Configuration.GetValue<bool>("Sinistri:EnableDevelopmentDelete")));
builder.Services.AddScoped<SinistroService>();
builder.Services.AddScoped<PeriziaSinistroLinkService>();
builder.Services.AddSingleton<IFileMalwareScanner, ClamdFileMalwareScanner>();
// Probe the same OIDC issuer as JWT validation, with the existing Keycloak
// development backchannel mapping only when explicitly running Development.
builder.Services.AddSingleton<IOidcReadinessProbe>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var metadataAddress = config["Auth:MetadataAddress"] ??
        authIssuer.TrimEnd('/') + "/.well-known/openid-configuration";
    var internalOrigin = config["Auth:DevelopmentBackchannelOrigin"];
    if (!builder.Environment.IsDevelopment() && !string.IsNullOrEmpty(internalOrigin))
        throw new InvalidOperationException("OIDC development backchannel is forbidden in production.");
    HttpMessageHandler handler = !string.IsNullOrEmpty(internalOrigin)
        ? new OidcDevelopmentBackchannelHandler(authIssuer, internalOrigin, allowRedirects: false)
        : new HttpClientHandler { AllowAutoRedirect = false };
    return new OidcReadinessProbe(
        new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) },
        authIssuer, metadataAddress, allowHttpForDevelopment: builder.Environment.IsDevelopment());
});
// Enforce independent read-only storage credentials before accepting traffic.
// Only the explicit local Development Compose demo may share its test account.
var allowSharedReadinessCredentials = builder.Configuration.GetValue<bool>(
    "Storage:Readiness:AllowSharedDevelopmentCredentials");
if (allowSharedReadinessCredentials && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Shared storage readiness credentials are forbidden outside Development.");
builder.Services.AddSingleton<IStorageReadinessProbe>(
    StorageReadinessProbe.Create(builder.Configuration, allowSharedReadinessCredentials));

builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new ServiceReadinessProbe(
        sp.GetRequiredService<NpgsqlDataSource>(),
        config["Malware:Clamd:Host"] ?? throw new InvalidOperationException("Clamd host is required."),
        config.GetValue("Malware:Clamd:Port", 3310),
        sp.GetRequiredService<IOidcReadinessProbe>(),
        sp.GetRequiredService<IStorageReadinessProbe>(),
        sp.GetRequiredService<ClamdSignaturePolicy>());
});
var recoveryConnectionString = builder.Configuration["UploadRecovery:ConnectionString"];
if (string.IsNullOrWhiteSpace(recoveryConnectionString))
    throw new InvalidOperationException("Independent least-privilege upload recovery connection is required.");
builder.Services.AddSingleton(sp => new PgUploadReconciler(
    NpgsqlDataSource.Create(recoveryConnectionString), sp.GetRequiredService<IObjectStore>()));
builder.Services.AddHostedService<UploadRecoveryWorker>();
builder.Services.AddScoped<FileIngestionService>();
builder.Services.AddScoped<LegacyXmlPreviewService>();
builder.Services.AddScoped<ILegacyImportReferenceLookup, PgLegacyImportReferenceLookup>();
builder.Services.AddScoped<LegacyXmlImportPlanService>();
if (string.Equals(builder.Configuration["Storage:Provider"], "Azure", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IObjectStore, AzureBlobObjectStore>();
else
    builder.Services.AddSingleton<IObjectStore, MinioObjectStore>();
// Reuse exactly the same private store configured for ingestion, not a
// second client with different tenant/credential boundaries.
builder.Services.AddSingleton<IDocumentBlobReader>(sp =>
    (IDocumentBlobReader)sp.GetRequiredService<IObjectStore>());

// Pin the dual-era, stateless HTTP boundary explicitly. The MCP C# SDK 2.0
// serves modern 2026-07-28 requests with per-request _meta while keeping the
// 2025-11-25 initialize handshake for legacy clients. No sticky sessions:
// tenant, grants and tools are evaluated on EVERY HTTP request.
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<McpTools>();
builder.Services.AddCors(options => options.AddPolicy("web", policy =>
    policy.WithOrigins(builder.Configuration["Cors:Origin"] ?? "http://localhost:5173")
        .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

if (app.Configuration.GetValue("Database:Bootstrap", false))
{
    if (!app.Environment.IsDevelopment())
        throw new InvalidOperationException("Database bootstrap is prohibited outside Development.");
    await SchemaBootstrap.ApplyDevelopmentSchemaAsync(app.Services.GetRequiredService<NpgsqlDataSource>());
}

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = error switch
    {
        UnauthorizedAccessException => StatusCodes.Status403Forbidden,
        FileThreatDetectedException => StatusCodes.Status422UnprocessableEntity,
        UploadIdempotencyConflictException => StatusCodes.Status409Conflict,
        PeriziaConcurrencyConflictException => StatusCodes.Status409Conflict,
        SinistroConcurrencyConflictException => StatusCodes.Status409Conflict,
        SinistroLinkedPerizieException => StatusCodes.Status409Conflict,
        LegacyXmlReferenceConflictException => StatusCodes.Status409Conflict,
        UploadLeaseExpiredException => StatusCodes.Status503ServiceUnavailable,
        MalwareScannerUnavailableException => StatusCodes.Status503ServiceUnavailable,
        DocumentContentUnavailableException => StatusCodes.Status503ServiceUnavailable,
        InvalidDataException => StatusCodes.Status413PayloadTooLarge,
        // Minimal API JSON binding rejects malformed bodies before a service
        // is invoked. Preserve its 400 instead of disguising user input as 500.
        BadHttpRequestException badRequest when badRequest.StatusCode == 400 => StatusCodes.Status400BadRequest,
        BadHttpRequestException badRequest when badRequest.StatusCode == 413 => StatusCodes.Status413PayloadTooLarge,
        KeyNotFoundException => StatusCodes.Status404NotFound,
        ArgumentException or InvalidOperationException => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError
    };
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new { error = status == 500 ? "Internal server error." : error?.Message });
}));

// Limit the entire multipart HTTP request, not only the individual file section.
app.Use(async (context, next) =>
{
    // The PWA precaches only public static shell assets. Even outside the
    // service worker, no browser/proxy must store tenant-scoped REST or MCP
    // responses (including authentication failures and streamed MCP output).
    if (context.Request.Path.StartsWithSegments("/api") ||
        context.Request.Path.StartsWithSegments("/mcp"))
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Pragma"] = "no-cache";
            return Task.CompletedTask;
        });
    }
    if (context.Request.Path.Equals("/api/imports/xml/preview", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.Equals("/api/imports/xml/plan", StringComparison.OrdinalIgnoreCase))
    {
        var xmlBody = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (xmlBody is { IsReadOnly: false })
            xmlBody.MaxRequestBodySize = LegacyXmlPreviewService.MaxXmlBytes;
    }
    if (context.Request.Path.Equals("/api/files", StringComparison.OrdinalIgnoreCase))
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = FileIngestionService.MaxUploadBytes + 1024 * 1024;
    }
    await next(context);
});

app.UseCors("web");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
// Read-only, unauthenticated readiness: no details of internal hosts or credentials.
// Liveness remains healthy even while dependencies are temporarily unavailable.
app.MapGet("/health/ready", async (ServiceReadinessProbe probe, CancellationToken ct) =>
    await probe.CheckAsync(ct)
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "not_ready" },
            statusCode: StatusCodes.Status503ServiceUnavailable));
// RFC 9728: resource metadata must match the canonical protected resource URI.
// Clients visiting /mcp get the path-specific /.well-known/.../mcp metadata.
app.MapGet("/.well-known/oauth-protected-resource/mcp", () =>
    Results.Json(metadata.AsMcpMetadata(authIssuer)));
app.MapGet("/.well-known/oauth-protected-resource", () =>
    Results.Json(metadata.AsApiMetadata(authIssuer)));

var api = app.MapGroup("/api").RequireAuthorization();
// Import preview is strictly non-mutating: no Sinistri/Perizie/metadata or
// embedded attachments are ever written, decoded, returned or logged.
// The ingress budget is enforced both by Kestrel and by this streaming
// read, including for chunked requests without Content-Length.
api.MapPost("/imports/xml/preview", async (
    HttpRequest request, LegacyXmlPreviewService preview, CancellationToken ct) =>
{
    var mediaType = request.ContentType?.Split(';', 2)[0].Trim();
    if (!string.Equals(mediaType, "application/xml", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(mediaType, "text/xml", StringComparison.OrdinalIgnoreCase))
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    if (request.ContentLength is > LegacyXmlPreviewService.MaxXmlBytes)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    using var body = new MemoryStream();
    var chunk = new byte[16 * 1024];
    int bytesRead;
    while ((bytesRead = await request.Body.ReadAsync(chunk, ct)) > 0)
    {
        if (body.Length + bytesRead > LegacyXmlPreviewService.MaxXmlBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        await body.WriteAsync(chunk.AsMemory(0, bytesRead), ct);
    }
    return Results.Ok(preview.Preview(body.ToArray()));
}).RequireAuthorization("peritial.write");

// Reconciliation plan is a dry-run: no case creation/updates, no imports,
// no uploads, no success audit. Never guess an ID on ambiguous references.
api.MapPost("/imports/xml/plan", async (
    HttpRequest request, LegacyXmlImportPlanService planner, CancellationToken ct) =>
{
    var mediaType = request.ContentType?.Split(';', 2)[0].Trim();
    if (!string.Equals(mediaType, "application/xml", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(mediaType, "text/xml", StringComparison.OrdinalIgnoreCase))
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    if (request.ContentLength is > LegacyXmlPreviewService.MaxXmlBytes)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    using var body = new MemoryStream();
    var chunk = new byte[16 * 1024];
    int bytesRead;
    while ((bytesRead = await request.Body.ReadAsync(chunk, ct)) > 0)
    {
        if (body.Length + bytesRead > LegacyXmlPreviewService.MaxXmlBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        await body.WriteAsync(chunk.AsMemory(0, bytesRead), ct);
    }
    return Results.Ok(await planner.PlanAsync(body.ToArray(), ct));
}).RequireAuthorization("peritial.write");

api.MapGet("/perizie", (PeriziaService service, CancellationToken ct) => service.ListAsync(ct))
    .RequireAuthorization("peritial.read");
api.MapGet("/perizie/{id:guid}", (Guid id, PeriziaService service, CancellationToken ct) =>
    service.GetAsync(id, ct)).RequireAuthorization("peritial.read");
api.MapPost("/perizie", async (CreatePeriziaRequest input, PeriziaService service, CancellationToken ct) =>
{
    var created = await service.CreateAsync(input.Reference, ct);
    return Results.Created($"/api/perizie/{created.Id}", created);
}).RequireAuthorization("peritial.write");
api.MapPost("/perizie/{id:guid}/assign", (Guid id, AssignPeriziaRequest request, PeriziaService service, CancellationToken ct) =>
    service.AssignAsync(id, request.Assignee, ct)).RequireAuthorization("peritial.write");
api.MapPost("/perizie/{id:guid}/advance", (Guid id, AdvancePeriziaRequest request, PeriziaService service, CancellationToken ct) =>
    service.AdvanceAsync(id, PeriziaAdvanceTarget.Parse(request.Target), ct)).RequireAuthorization("peritial.write");
api.MapPost("/perizie/{id:guid}/cancel", (Guid id, PeriziaService service, CancellationToken ct) =>
    service.CancelAsync(id, ct)).RequireAuthorization("peritial.write");

// Both REST and MCP delegate to the same JWT-tenant-aware Business service.
// These routes require the explicit V003 schema migration; no startup DDL.
// V004 links are additive and manually migrated before enabling this surface.
api.MapGet("/sinistri/{id:guid}/perizie",
    (Guid id, PeriziaSinistroLinkService links, CancellationToken ct) =>
        links.ListPeriziaIdsAsync(id, ct)).RequireAuthorization("peritial.read");
api.MapGet("/perizie/{id:guid}/sinistro",
    (Guid id, PeriziaSinistroLinkService links, CancellationToken ct) =>
        links.GetPeriziaAssociationAsync(id, ct)).RequireAuthorization("peritial.read");
api.MapPut("/perizie/{id:guid}/sinistro",
    (Guid id, ReparentPeriziaSinistroRequest input,
        PeriziaSinistroLinkService links, CancellationToken ct) =>
        links.ReparentAsync(id, input.ExpectedSinistroId, input.NewSinistroId,
            input.ExpectedLinkVersion, ct))
    .RequireAuthorization("peritial.write");
api.MapDelete("/perizie/{id:guid}/sinistro",
    async (Guid id, Guid expectedSinistroId, long expectedLinkVersion,
        PeriziaSinistroLinkService links, CancellationToken ct) =>
    {
        await links.UnlinkAsync(id, expectedSinistroId, expectedLinkVersion, ct);
        return Results.NoContent();
    }).RequireAuthorization("peritial.write");
api.MapPost("/perizie/{id:guid}/sinistro",
    async (Guid id, LinkPeriziaSinistroRequest input,
        PeriziaSinistroLinkService links, CancellationToken ct) =>
    {
        var link = await links.LinkAsync(id, input.SinistroId, input.ExpectedLinkVersion, ct);
        return Results.Created($"/api/sinistri/{link.SinistroId}/perizie", link);
    }).RequireAuthorization("peritial.write");

api.MapGet("/sinistri", (int? limit, SinistroService service, CancellationToken ct) =>
    service.ListAsync(limit ?? 50, ct)).RequireAuthorization("peritial.read");
api.MapGet("/sinistri/page", (int? limit, string? cursor, SinistroService service, CancellationToken ct) =>
    service.PageAsync(limit ?? 50, cursor, ct)).RequireAuthorization("peritial.read");
api.MapGet("/sinistri/search/page", (string prefix, DateOnly? occurredFrom,
    DateOnly? occurredTo, int? limit, string? cursor,
    SinistroService service, CancellationToken ct) =>
    service.SearchPageAsync(prefix, occurredFrom, occurredTo, limit ?? 50, cursor, ct))
    .RequireAuthorization("peritial.read");
api.MapGet("/sinistri/search", (string prefix, DateOnly? occurredFrom, DateOnly? occurredTo,
    int? limit, SinistroService service, CancellationToken ct) =>
    service.SearchAsync(prefix, occurredFrom, occurredTo, limit ?? 10, ct))
    .RequireAuthorization("peritial.read");
api.MapGet("/sinistri/{id:guid}", (Guid id, SinistroService service, CancellationToken ct) =>
    service.GetAsync(id, ct)).RequireAuthorization("peritial.read");
api.MapPost("/sinistri", async (CreateSinistroRequest input, SinistroService service, CancellationToken ct) =>
{
    var item = await service.CreateAsync(input.ExternalReference, input.OccurredOn, input.ReportedOn, ct);
    return Results.Created($"/api/sinistri/{item.Id}", item);
}).RequireAuthorization("peritial.write");
api.MapPut("/sinistri/{id:guid}", (Guid id, UpdateSinistroRequest input, SinistroService service, CancellationToken ct) =>
    service.UpdateAsync(id, input.ExternalReference, input.OccurredOn,
        input.ReportedOn, input.ExpectedVersion, ct)).RequireAuthorization("peritial.write");

api.MapDelete("/sinistri/{id:guid}", async (Guid id, long expectedVersion,
    SinistroService service, CancellationToken ct) =>
{
    await service.DeleteAsync(id, expectedVersion, ct);
    return Results.NoContent();
}).RequireAuthorization("peritial.write");

// Read-only document catalog returns an allowlisted metadata projection:
 // never storage keys, scanned bytes, internal object URLs, or tenant IDs.
api.MapGet("/files/page",
    (Guid? periziaId, int? limit, string? cursor,
        DocumentCatalogService service, CancellationToken ct) =>
        service.PageAsync(periziaId, limit ?? 50, cursor, ct))
    .RequireAuthorization("peritial.read");
api.MapGet("/files", (Guid? periziaId, int? limit, DocumentCatalogService service, CancellationToken ct) =>
    service.ListAsync(periziaId, limit ?? 50, ct)).RequireAuthorization("peritial.read");
api.MapGet("/files/{id:guid}", (Guid id, DocumentCatalogService service, CancellationToken ct) =>
    service.GetAsync(id, ct)).RequireAuthorization("peritial.read");

// Private audited document bytes; a signed tenant-scoped database lookup
// always precedes object access. Never redirect to S3/Azure or expose a SAS.
// Force download rather than inline rendering of potentially active content.
api.MapGet("/files/{id:guid}/download", async (Guid id,
    DocumentDownloadService service, HttpContext http, CancellationToken ct) =>
{
    var file = await service.ReadAsync(id, ct);
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    http.Response.Headers["Content-Security-Policy"] = "sandbox";
    return Results.File(file.Bytes, contentType: file.ContentType,
        fileDownloadName: file.FileName, enableRangeProcessing: false);
}).RequireAuthorization("peritial.read");

api.MapPost("/files", async (HttpRequest request, FileIngestionService service, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest("multipart/form-data required");
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null) return Results.BadRequest("file required");
    Guid? periziaId = Guid.TryParse(form["periziaId"].ToString(), out var id) ? id : null;
    await using var stream = file.OpenReadStream();
    var idempotencyKey = request.Headers["Idempotency-Key"].ToString();
    var uploaded = await service.IngestAsync(file.FileName, file.ContentType, stream, file.Length,
        periziaId, string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey, ct);
    return Results.Created($"/api/files/{uploaded.Id}", uploaded);
}).DisableAntiforgery().RequireAuthorization("peritial.write");

app.MapMcp("/mcp").RequireAuthorization("peritial.access");
app.Run();

public sealed record ReparentPeriziaSinistroRequest(
    Guid ExpectedSinistroId, Guid NewSinistroId, long ExpectedLinkVersion);
public sealed record LinkPeriziaSinistroRequest(Guid SinistroId, long ExpectedLinkVersion);
public sealed record CreateSinistroRequest(string ExternalReference, DateOnly OccurredOn, DateOnly? ReportedOn);
public sealed record UpdateSinistroRequest(
    string ExternalReference, DateOnly OccurredOn, DateOnly? ReportedOn, long ExpectedVersion);
public sealed record CreatePeriziaRequest(string Reference);
public sealed record AssignPeriziaRequest(string Assignee);
public sealed record AdvancePeriziaRequest(string Target);
