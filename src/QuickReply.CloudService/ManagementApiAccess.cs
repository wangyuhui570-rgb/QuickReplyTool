using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace QuickReply.CloudService;

internal static class ManagementApiAccess
{
    public static RouteHandlerBuilder RequireManagementAccess(this RouteHandlerBuilder builder)
    {
        return builder.AddEndpointFilterFactory((_, next) => async context =>
        {
            var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<KnowledgeCloudOptions>>().Value;
            if (IsLoopbackAllowed(context.HttpContext, options))
            {
                return await next(context).ConfigureAwait(false);
            }

            var configuredToken = options.ManagementApiToken?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(configuredToken))
            {
                return Results.Problem(
                    detail: "当前服务未配置 ManagementApiToken，已拒绝远程管理请求。",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!TryReadBearerToken(context.HttpContext, out var presentedToken))
            {
                return Results.Unauthorized();
            }

            if (!FixedTimeEquals(configuredToken, presentedToken))
            {
                return Results.Unauthorized();
            }

            return await next(context).ConfigureAwait(false);
        });
    }

    public static bool CanUseInteractiveAiAuth(HttpContext httpContext, KnowledgeCloudOptions options)
    {
        return IsLoopbackAllowed(httpContext, options);
    }

    private static bool IsLoopbackAllowed(HttpContext httpContext, KnowledgeCloudOptions options)
    {
        if (!options.AllowLoopbackWithoutAuth)
        {
            return false;
        }

        if (httpContext.Connection.RemoteIpAddress is { } remoteAddress &&
            System.Net.IPAddress.IsLoopback(remoteAddress))
        {
            return true;
        }

        var host = httpContext.Request.Host.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return System.Net.IPAddress.TryParse(host, out var hostAddress) &&
               System.Net.IPAddress.IsLoopback(hostAddress);
    }

    private static bool TryReadBearerToken(HttpContext httpContext, out string token)
    {
        token = string.Empty;
        var headerValue = httpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(headerValue) ||
            !headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = headerValue["Bearer ".Length..].Trim();
        return !string.IsNullOrWhiteSpace(token);
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
