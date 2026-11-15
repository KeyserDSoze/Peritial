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
builder.Services.AddHttpContextAccessor();
// Reject oversized multipart sections before they reach the Business ingestion service.
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = FileIngestionService.MaxUploadBytes;
    options.MultipartHeadersLengthLimit = 16 * 1024;
});

var authIssuer = builder.Configuration["Auth:Authority"] ?? throw new InvalidOperationException("Auth:Authority required.");
var audience = builder.Configuration["Auth:Audience"] ?? throw new InvalidOperationException("Auth:Audience required.");
var publicResource = builder.Configuration["Auth:Resource"] ?? "http://localhost:5173/mcp";

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
        if (builder.Configuration["Auth:MetadataAddress"] is { Length: > 0 } metadata)
            options.MetadataAddress = metadata;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
            ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
            {
                context.Response.Headers.Append("WWW-Authenticate",
                    $"Bearer resource_metadata=\"{publicResource.TrimEnd('/')}/.well-known/oauth-protected-resource\"");
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<IPeriziaRepository, PgPeriziaRepository>();
builder.Services.AddScoped<IDocumentRepository, PgDocumentRepository>();
builder.Services.AddScoped<PeriziaService>();
builder.Services.AddScoped<FileIngestionService>();
if (string.Equals(builder.Configuration["Storage:Provider"], "Azure", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IObjectStore, AzureBlobObjectStore>();
else
    builder.Services.AddSingleton<IObjectStore, MinioObjectStore>();

builder.Services.AddMcpServer().WithHttpTransport().WithTools<McpTools>();
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
        InvalidDataException => StatusCodes.Status413PayloadTooLarge,
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
app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(new
{
    resource = publicResource,
    authorization_servers = new[] { authIssuer },
    scopes_supported = new[] { "peritial.read", "peritial.write" },
    bearer_methods_supported = new[] { "header" }
}));

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/perizie", (PeriziaService service, CancellationToken ct) => service.ListAsync(ct));
api.MapPost("/perizie", async (CreatePeriziaRequest input, PeriziaService service, CancellationToken ct) =>
{
    var created = await service.CreateAsync(input.Reference, ct);
    return Results.Created($"/api/perizie/{created.Id}", created);
});
api.MapPost("/perizie/{id:guid}/assign", (Guid id, AssignPeriziaRequest request, PeriziaService service, CancellationToken ct) =>
    service.AssignAsync(id, request.Assignee, ct));
api.MapPost("/perizie/{id:guid}/advance", (Guid id, AdvancePeriziaRequest request, PeriziaService service, CancellationToken ct) =>
    service.AdvanceAsync(id, request.Target, ct));

api.MapPost("/files", async (HttpRequest request, FileIngestionService service, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest("multipart/form-data required");
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null) return Results.BadRequest("file required");
    Guid? periziaId = Guid.TryParse(form["periziaId"].ToString(), out var id) ? id : null;
    await using var stream = file.OpenReadStream();
    var uploaded = await service.IngestAsync(file.FileName, file.ContentType, stream, file.Length, periziaId, ct);
    return Results.Created($"/api/files/{uploaded.Id}", uploaded);
}).DisableAntiforgery();

app.MapMcp("/mcp").RequireAuthorization();
app.Run();

public sealed record CreatePeriziaRequest(string Reference);
public sealed record AssignPeriziaRequest(string Assignee);
public sealed record AdvancePeriziaRequest(PeriziaStatus Target);
