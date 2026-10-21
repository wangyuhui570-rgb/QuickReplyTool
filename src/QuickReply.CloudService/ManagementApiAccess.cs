using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace QuickReply.CloudService;

internal static class ManagementApiAccess
{
    public static TBuilder RequireManagementAccess<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
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

    internal static bool IsLoopbackAllowed(HttpContext httpContext, KnowledgeCloudOptions options)
    {
        if (!options.AllowLoopbackWithoutAuth)
        {
            return false;
        }

        // 经过反向代理转发的请求，即使 TCP 对端是 127.0.0.1，真实来源也可能是公网，一律要求 Token。
        if (IsForwardedRequest(httpContext.Request))
        {
            return false;
        }

        // 只信任 TCP 连接的真实对端地址。Host 请求头由客户端任意填写，不能作为“本机访问”的依据。
        return httpContext.Connection.RemoteIpAddress is { } remoteAddress &&
               System.Net.IPAddress.IsLoopback(remoteAddress);
    }

    private static bool IsForwardedRequest(HttpRequest request)
    {
        return request.Headers.ContainsKey("X-Forwarded-For") ||
               request.Headers.ContainsKey("X-Real-IP") ||
               request.Headers.ContainsKey("Forwarded");
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
