using Microsoft.Extensions.Options;

namespace QuickReply.CloudService;

internal static class AiAuthEndpoints
{
    public static IEndpointRouteBuilder MapAiAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/quickreply/ai-auth")
            .RequireManagementAccess();

        group.MapGet("/status", async (
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

            var status = await authService.GetStatusAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return Results.Ok(status);
        });

        group.MapPost("/login", async (
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

            var result = await authService.LoginAsync(request?.UseDeviceAuth ?? true, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        });

        group.MapPost("/logout", async (
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

            var result = await authService.LogoutAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        });

        return endpoints;
    }
}
