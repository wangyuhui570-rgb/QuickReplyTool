using System.Net.Http;

namespace QuickReply.Desktop.Services;

/// <summary>
/// 全进程共享的 HTTP 连接池。
/// 各服务仍然可以按需 new HttpClient 并设置自己的 BaseAddress、Timeout 和请求头，
/// 但底层 TCP/TLS 连接会被复用，避免每次同步、检查更新都重新握手，以及频繁请求时耗尽端口。
/// 使用方式：new HttpClient(SharedHttpMessageHandler.Instance, disposeHandler: false)。
/// </summary>
internal static class SharedHttpMessageHandler
{
    public static HttpMessageHandler Instance { get; } = new SocketsHttpHandler
    {
        // 定期轮换连接，确保服务器 DNS/IP 变更后能被感知。
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10)
    };
}
