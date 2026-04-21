using Microsoft.Extensions.Options;

namespace QuickReply.CloudService;

internal static class OperationalEndpoints
{
    public static IEndpointRouteBuilder MapOperationalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/healthz", () => Results.Ok(new
        {
            ok = true,
            service = "QuickReply.CloudService"
        }));

        endpoints.MapGet("/readyz", async (
            CloudServiceRuntimeInfoService runtimeInfoService,
            CancellationToken cancellationToken) =>
        {
            var runtimeInfo = await runtimeInfoService.GetRuntimeInfoAsync(cancellationToken).ConfigureAwait(false);
            return runtimeInfo.Ok
                ? Results.Ok(runtimeInfo)
                : Results.Json(runtimeInfo, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        endpoints.MapGet("/version", (IOptions<KnowledgeCloudOptions> options) => Results.Ok(new
        {
            service = "QuickReply.CloudService",
            version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
            aiProvider = options.Value.AiProvider,
            aiSynthesisEnabled = options.Value.EnableAiSynthesis,
            managementApiTokenConfigured = !string.IsNullOrWhiteSpace(options.Value.ManagementApiToken),
            loopbackBypassEnabled = options.Value.AllowLoopbackWithoutAuth
        }));

        endpoints.MapGet("/info", async (
            CloudServiceRuntimeInfoService runtimeInfoService,
            CancellationToken cancellationToken) =>
        {
            var runtimeInfo = await runtimeInfoService.GetRuntimeInfoAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(runtimeInfo);
        }).RequireManagementAccess();

        return endpoints;
    }
}
