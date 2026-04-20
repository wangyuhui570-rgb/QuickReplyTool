using QuickReply.CloudService;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<KnowledgeCloudOptions>(builder.Configuration.GetSection(KnowledgeCloudOptions.SectionName));
builder.Services.AddSingleton<KnowledgeCloudStore>();
builder.Services.AddSingleton<KnowledgeCandidateImportService>();
builder.Services.AddSingleton<ICodexCliCommandRunner, CodexCliCommandRunner>();
builder.Services.AddSingleton<CodexCliAuthService>();
builder.Services.AddHttpClient(nameof(OllamaKnowledgeCandidateSynthesizer), (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<KnowledgeCloudOptions>>().Value;
    client.BaseAddress = new Uri(options.OllamaBaseUrl);
});
builder.Services.AddHttpClient(nameof(OpenAiKnowledgeCandidateSynthesizer), (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<KnowledgeCloudOptions>>().Value;
    client.BaseAddress = new Uri(options.OpenAiBaseUrl);
});
builder.Services.AddSingleton<HeuristicKnowledgeCandidateSynthesizer>();
builder.Services.AddSingleton<CodexCliKnowledgeCandidateSynthesizer>();
builder.Services.AddSingleton<OllamaKnowledgeCandidateSynthesizer>();
builder.Services.AddSingleton<OpenAiKnowledgeCandidateSynthesizer>();
builder.Services.AddSingleton<IKnowledgeCandidateSynthesizer, HybridKnowledgeCandidateSynthesizer>();
builder.Services.AddSingleton<KnowledgeCandidateSynthesisService>();
builder.Services.AddSingleton<CloudServiceRuntimeInfoService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<KnowledgeCloudStore>();
    await store.InitializeAsync();
}

app.MapGet("/healthz", () => Results.Ok(new { ok = true, service = "QuickReply.CloudService" }));

app.MapGet("/readyz", async (
    CloudServiceRuntimeInfoService runtimeInfoService,
    CancellationToken cancellationToken) =>
{
    var runtimeInfo = await runtimeInfoService.GetRuntimeInfoAsync(cancellationToken).ConfigureAwait(false);
    return runtimeInfo.Ok
        ? Results.Ok(runtimeInfo)
        : Results.Json(runtimeInfo, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/version", (IOptions<KnowledgeCloudOptions> options) => Results.Ok(new
{
    service = "QuickReply.CloudService",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
    aiProvider = options.Value.AiProvider,
    aiSynthesisEnabled = options.Value.EnableAiSynthesis,
    managementApiTokenConfigured = !string.IsNullOrWhiteSpace(options.Value.ManagementApiToken),
    loopbackBypassEnabled = options.Value.AllowLoopbackWithoutAuth
}));

app.MapGet("/info", async (
    CloudServiceRuntimeInfoService runtimeInfoService,
    CancellationToken cancellationToken) =>
{
    var runtimeInfo = await runtimeInfoService.GetRuntimeInfoAsync(cancellationToken).ConfigureAwait(false);
    return Results.Ok(runtimeInfo);
}).RequireManagementAccess();

app.MapGet("/api/quickreply/ai-auth/status", async (
    HttpContext httpContext,
    IOptions<KnowledgeCloudOptions> options,
    CodexCliAuthService authService,
    CancellationToken cancellationToken) =>
{
    if (!ManagementApiAccess.CanUseInteractiveAiAuth(httpContext, options.Value))
    {
        return Results.Problem(
            detail: "AI 认证状态接口默认只允许本机访问。",
            statusCode: StatusCodes.Status403Forbidden);
    }

    var status = await authService.GetStatusAsync(cancellationToken: cancellationToken);
    return Results.Ok(status);
}).RequireManagementAccess();

app.MapPost("/api/quickreply/ai-auth/login", async (
    HttpContext httpContext,
    IOptions<KnowledgeCloudOptions> options,
    CodexCliLoginRequest? request,
    CodexCliAuthService authService,
    CancellationToken cancellationToken) =>
{
    if (!ManagementApiAccess.CanUseInteractiveAiAuth(httpContext, options.Value))
    {
        return Results.Problem(
            detail: "AI 登录流程默认只允许本机访问。",
            statusCode: StatusCodes.Status403Forbidden);
    }

    var result = await authService.LoginAsync(request?.UseDeviceAuth ?? true, cancellationToken);
    return Results.Ok(result);
}).RequireManagementAccess();

app.MapPost("/api/quickreply/ai-auth/logout", async (
    HttpContext httpContext,
    IOptions<KnowledgeCloudOptions> options,
    CodexCliAuthService authService,
    CancellationToken cancellationToken) =>
{
    if (!ManagementApiAccess.CanUseInteractiveAiAuth(httpContext, options.Value))
    {
        return Results.Problem(
            detail: "AI 退出登录默认只允许本机访问。",
            statusCode: StatusCodes.Status403Forbidden);
    }

    var result = await authService.LogoutAsync(cancellationToken);
    return Results.Ok(result);
}).RequireManagementAccess();

app.MapGet("/api/quickreply/knowledge/published", async (
    DateTimeOffset? since,
    long? sinceRevision,
    string? cursor,
    int? limit,
    KnowledgeCloudStore store,
    CancellationToken cancellationToken) =>
{
    var response = await store.GetPublishedKnowledgeCardsAsync(
        sinceRevision,
        cursor,
        limit ?? 200,
        since,
        cancellationToken);
    return Results.Ok(response);
});

app.MapGet("/api/quickreply/knowledge/review/queue", async (
    string? cursor,
    int? limit,
    KnowledgeCloudStore store,
    CancellationToken cancellationToken) =>
{
    var queue = await store.GetKnowledgeReviewQueuePageAsync(cursor, limit ?? 200, cancellationToken);
    return Results.Ok(queue);
}).RequireManagementAccess();

app.MapGet("/api/quickreply/knowledge/review/{id:guid}/sources", async (
    Guid id,
    string? cursor,
    int? limit,
    KnowledgeCloudStore store,
    CancellationToken cancellationToken) =>
{
    var sources = await store.GetKnowledgeCandidateSourcesPageAsync(id, cursor, limit ?? 200, cancellationToken);
    return Results.Ok(sources);
}).RequireManagementAccess();

app.MapGet("/api/quickreply/knowledge/review/{id:guid}/history", async (
    Guid id,
    string? cursor,
    int? limit,
    KnowledgeCloudStore store,
    CancellationToken cancellationToken) =>
{
    var history = await store.GetKnowledgeReviewHistoryPageAsync(id, cursor, limit ?? 100, cancellationToken);
    return Results.Ok(history);
}).RequireManagementAccess();

app.MapGet("/api/quickreply/knowledge/jobs/runs", async (
    int? limit,
    KnowledgeCloudStore store,
    CancellationToken cancellationToken) =>
{
    var runs = await store.GetKnowledgeJobRunsAsync(limit ?? 20, cancellationToken);
    return Results.Ok(runs);
}).RequireManagementAccess();

app.MapPost("/api/quickreply/knowledge/jobs/import-logs", async (
    ImportKnowledgeCandidatesRequest request,
    KnowledgeCandidateImportService importService,
    CancellationToken cancellationToken) =>
{
    var result = await importService.ImportFromPathAsync(request.InputPath, cancellationToken);
    return Results.Ok(result);
}).RequireManagementAccess();

app.MapPost("/api/quickreply/knowledge/jobs/synthesize-drafts", async (
    int? limit,
    KnowledgeCandidateSynthesisService synthesisService,
    CancellationToken cancellationToken) =>
{
    var result = await synthesisService.SynthesizeDraftCandidatesAsync(limit ?? 50, cancellationToken);
    return Results.Ok(result);
}).RequireManagementAccess();

app.MapPost("/api/quickreply/knowledge/review/{id:guid}/approve", async (
    Guid id,
    ApproveKnowledgeCandidateRequest request,
    KnowledgeCloudStore store,
    CancellationToken cancellationToken) =>
{
    var result = await store.ApproveKnowledgeCandidateAsync(id, request, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
}).RequireManagementAccess();

app.MapPost("/api/quickreply/knowledge/review/{id:guid}/reject", async (
    Guid id,
    RejectKnowledgeCandidateRequest request,
    KnowledgeCloudStore store,
    CancellationToken cancellationToken) =>
{
    var result = await store.RejectKnowledgeCandidateAsync(id, request, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
}).RequireManagementAccess();

app.MapPost("/api/quickreply/knowledge/published/{id:guid}/remove", async (
    Guid id,
    RemovePublishedKnowledgeCardRequest request,
    KnowledgeCloudStore store,
    CancellationToken cancellationToken) =>
{
    var removed = await store.RemovePublishedKnowledgeCardAsync(id, request, cancellationToken);
    return removed ? Results.Ok(new { id, removed = true }) : Results.NotFound();
}).RequireManagementAccess();

app.Run();

public partial class Program;
