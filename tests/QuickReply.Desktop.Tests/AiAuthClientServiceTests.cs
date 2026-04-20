using System.Net;
using System.Net.Http.Headers;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;

namespace QuickReply.Desktop.Tests;

public sealed class AiAuthClientServiceTests
{
    [Fact]
    public async Task GetStatusAsync_UsesBuiltInServer_WhenServerBaseUrlIsEmpty()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            Assert.Equal($"{QuickReplyServiceEndpointDefaults.DefaultRemoteServiceBaseUrl}/api/quickreply/ai-auth/status", request.RequestUri?.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("token-1", request.Headers.Authorization?.Parameter);
            return Task.FromResult(HttpResponseFactory.Json(
                """
                {
                  "cliAvailable": true,
                  "loggedIn": true,
                  "loginMode": "chatgpt",
                  "loginModeDisplayName": "ChatGPT / Codex 登录",
                  "message": "已检测到 Codex CLI 的 ChatGPT 登录态。"
                }
                """));
        });
        var service = new AiAuthClientService((baseUri, token) =>
        {
            var client = new HttpClient(handler)
            {
                BaseAddress = baseUri
            };
            if (!string.IsNullOrWhiteSpace(token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return client;
        });

        var status = await service.GetStatusAsync(string.Empty, "token-1");

        Assert.True(status.LoggedIn);
        Assert.Equal("chatgpt", status.LoginMode);
        Assert.Contains(QuickReplyServiceEndpointDefaults.DefaultRemoteServiceBaseUrl, status.EndpointSummary);
    }

    [Fact]
    public async Task GetStatusAsync_FallsBackToLocal_WhenConfiguredServerReturnsNotFound()
    {
        var requestCount = 0;
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                Assert.Equal("https://server.example.com/api/quickreply/ai-auth/status", request.RequestUri?.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            Assert.Equal("http://127.0.0.1:5128/api/quickreply/ai-auth/status", request.RequestUri?.ToString());
            return Task.FromResult(HttpResponseFactory.Json(
                """
                {
                  "cliAvailable": true,
                  "loggedIn": false,
                  "loginMode": "none",
                  "loginModeDisplayName": "未登录",
                  "message": "Codex CLI 已安装，但当前未登录。"
                }
                """));
        });
        var service = new AiAuthClientService(
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            },
            _ => Task.FromResult(true));

        var status = await service.GetStatusAsync("https://server.example.com", null);

        Assert.False(status.LoggedIn);
        Assert.Equal("http://127.0.0.1:5128", status.EffectiveEndpointBaseUrl);
        Assert.Contains("回退", status.EndpointSummary);
        Assert.Contains("返回 404", status.ProbeSummary);
    }

    [Fact]
    public async Task LoginAsync_PostsToExpectedEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            Assert.Equal("https://server.example.com/api/quickreply/ai-auth/login", request.RequestUri?.ToString());
            return Task.FromResult(HttpResponseFactory.Json(
                """
                {
                  "success": true,
                  "cliAvailable": true,
                  "loggedIn": true,
                  "loginMode": "chatgpt",
                  "loginModeDisplayName": "ChatGPT / Codex 登录",
                  "message": "已通过 Codex CLI 登录。",
                  "commandOutput": ""
                }
                """));
        });
        var service = new AiAuthClientService((baseUri, _) => new HttpClient(handler)
        {
            BaseAddress = baseUri
        });

        var result = await service.LoginAsync("https://server.example.com", null, useDeviceAuth: true);

        Assert.True(result.Success);
        Assert.True(result.LoggedIn);
        Assert.Equal("chatgpt", result.LoginMode);
    }
}
