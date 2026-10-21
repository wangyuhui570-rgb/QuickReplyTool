using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using System.IO;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class KnowledgeReviewClientService
{
    public const string DefaultLocalReviewBaseUrl = LocalCloudServiceBootstrapper.DefaultLocalServiceBaseUrl;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<Uri, string?, HttpClient> _httpClientFactory;
    private readonly Func<CancellationToken, Task<bool>> _ensureLocalReviewServiceRunningAsync;

    public KnowledgeReviewClientService(
        Func<Uri, string?, HttpClient>? httpClientFactory = null,
        Func<CancellationToken, Task<bool>>? ensureLocalReviewServiceRunningAsync = null)
    {
        _httpClientFactory = httpClientFactory ?? CreateDefaultHttpClient;
        _ensureLocalReviewServiceRunningAsync = ensureLocalReviewServiceRunningAsync ?? EnsureLocalReviewServiceRunningAsync;
    }

    public async Task<IReadOnlyList<KnowledgeReviewQueueItem>> GetReviewQueueAsync(
        string? serverBaseUrl,
        string? apiToken,
        CancellationToken cancellationToken = default)
    {
        var items = new List<KnowledgeReviewQueueItem>();
        var cursor = string.Empty;

        while (true)
        {
            var response = await GetJsonWithFallbackAsync<KnowledgeReviewQueueResponse>(
                serverBaseUrl,
                apiToken,
                $"/api/quickreply/knowledge/review/queue?limit=200&cursor={Uri.EscapeDataString(cursor)}",
                cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return items;
            }

            if (response.Items.Count > 0)
            {
                items.AddRange(response.Items);
            }

            if (!response.HasMore || string.IsNullOrWhiteSpace(response.NextCursor))
            {
                break;
            }

            cursor = response.NextCursor;
        }

        return items;
    }

    public async Task<IReadOnlyList<KnowledgeReviewSourceRecord>> GetReviewSourcesAsync(
        string? serverBaseUrl,
        string? apiToken,
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        var items = new List<KnowledgeReviewSourceRecord>();
        var cursor = string.Empty;

        while (true)
        {
            var response = await GetJsonWithFallbackAsync<KnowledgeReviewSourcesResponse>(
                serverBaseUrl,
                apiToken,
                $"/api/quickreply/knowledge/review/{candidateId}/sources?limit=200&cursor={Uri.EscapeDataString(cursor)}",
                cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return items;
            }

            if (response.Items.Count > 0)
            {
                items.AddRange(response.Items);
            }

            if (!response.HasMore || string.IsNullOrWhiteSpace(response.NextCursor))
            {
                break;
            }

            cursor = response.NextCursor;
        }

        return items;
    }

    public async Task<IReadOnlyList<KnowledgeReviewHistoryEventRecord>> GetReviewHistoryAsync(
        string? serverBaseUrl,
        string? apiToken,
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        var items = new List<KnowledgeReviewHistoryEventRecord>();
        var cursor = string.Empty;

        while (true)
        {
            var response = await GetJsonWithFallbackAsync<KnowledgeReviewHistoryResponse>(
                serverBaseUrl,
                apiToken,
                $"/api/quickreply/knowledge/review/{candidateId}/history?limit=100&cursor={Uri.EscapeDataString(cursor)}",
                cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return items;
            }

            if (response.Items.Count > 0)
            {
                items.AddRange(response.Items);
            }

            if (!response.HasMore || string.IsNullOrWhiteSpace(response.NextCursor))
            {
                break;
            }

            cursor = response.NextCursor;
        }

        return items;
    }

    public async Task ApproveAsync(
        string? serverBaseUrl,
        string? apiToken,
        Guid candidateId,
        ApproveKnowledgeCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        await PostJsonWithFallbackAsync(
            serverBaseUrl,
            apiToken,
            $"/api/quickreply/knowledge/review/{candidateId}/approve",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RejectAsync(
        string? serverBaseUrl,
        string? apiToken,
        Guid candidateId,
        RejectKnowledgeCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        await PostJsonWithFallbackAsync(
            serverBaseUrl,
            apiToken,
            $"/api/quickreply/knowledge/review/{candidateId}/reject",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RemovePublishedAsync(
        string? serverBaseUrl,
        string? apiToken,
        Guid candidateId,
        RemovePublishedKnowledgeCardRequest request,
        CancellationToken cancellationToken = default)
    {
        await PostJsonWithFallbackAsync(
            serverBaseUrl,
            apiToken,
            $"/api/quickreply/knowledge/published/{candidateId}/remove",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public static string ResolveEndpointBaseUrl(string? serverBaseUrl)
    {
        return SettingsInputValidator.ResolveOperationalServerBaseUrl(serverBaseUrl);
    }

    public static string DescribeEndpointPreference(string? serverBaseUrl)
    {
        var resolved = ResolveEndpointBaseUrl(serverBaseUrl);
        return string.Equals(resolved, DefaultLocalReviewBaseUrl, StringComparison.OrdinalIgnoreCase)
            ? $"审核服务地址：{resolved}"
            : $"审核服务地址：{resolved}；如果该地址没有审核接口，会自动回退到 {DefaultLocalReviewBaseUrl}";
    }

    private async Task<T?> GetJsonWithFallbackAsync<T>(
        string? serverBaseUrl,
        string? apiToken,
        string relativePath,
        CancellationToken cancellationToken)
    {
        HttpStatusCode? lastStatusCode = null;
        Exception? lastException = null;
        var localBootstrapAttempted = false;

        foreach (var endpointBaseUrl in GetEndpointCandidates(serverBaseUrl))
        {
            using var client = CreateClient(endpointBaseUrl, apiToken);
            try
            {
                using var response = await client.GetAsync(relativePath, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<T>(SerializerOptions, cancellationToken).ConfigureAwait(false);
                }

                lastStatusCode = response.StatusCode;
                if (response.StatusCode == HttpStatusCode.NotFound &&
                    !string.Equals(endpointBaseUrl, DefaultLocalReviewBaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                throw CreateFriendlyException(serverBaseUrl, lastStatusCode, null);
            }
            catch (HttpRequestException ex)
            {
                if (string.Equals(endpointBaseUrl, DefaultLocalReviewBaseUrl, StringComparison.OrdinalIgnoreCase) &&
                    !localBootstrapAttempted &&
                    await TryEnsureLocalServiceAndRetryAsync(cancellationToken).ConfigureAwait(false))
                {
                    localBootstrapAttempted = true;
                    using var retryClient = CreateClient(endpointBaseUrl, apiToken);
                    using var retryResponse = await retryClient.GetAsync(relativePath, cancellationToken).ConfigureAwait(false);
                    if (retryResponse.IsSuccessStatusCode)
                    {
                        return await retryResponse.Content.ReadFromJsonAsync<T>(SerializerOptions, cancellationToken).ConfigureAwait(false);
                    }

                    lastStatusCode = retryResponse.StatusCode;
                }
                else if (!string.Equals(endpointBaseUrl, DefaultLocalReviewBaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    lastException = ex;
                    continue;
                }
                else
                {
                    lastException = ex;
                }
            }
        }

        throw CreateFriendlyException(serverBaseUrl, lastStatusCode, lastException);
    }

    private async Task PostJsonWithFallbackAsync<TRequest>(
        string? serverBaseUrl,
        string? apiToken,
        string relativePath,
        TRequest request,
        CancellationToken cancellationToken)
    {
        HttpStatusCode? lastStatusCode = null;
        Exception? lastException = null;
        var localBootstrapAttempted = false;

        foreach (var endpointBaseUrl in GetEndpointCandidates(serverBaseUrl))
        {
            using var client = CreateClient(endpointBaseUrl, apiToken);
            try
            {
                using var response = await client.PostAsJsonAsync(
                    relativePath,
                    request,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                lastStatusCode = response.StatusCode;
                if (response.StatusCode == HttpStatusCode.NotFound &&
                    !string.Equals(endpointBaseUrl, DefaultLocalReviewBaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                throw CreateFriendlyException(serverBaseUrl, lastStatusCode, null);
            }
            catch (HttpRequestException ex)
            {
                if (string.Equals(endpointBaseUrl, DefaultLocalReviewBaseUrl, StringComparison.OrdinalIgnoreCase) &&
                    !localBootstrapAttempted &&
                    await TryEnsureLocalServiceAndRetryAsync(cancellationToken).ConfigureAwait(false))
                {
                    localBootstrapAttempted = true;
                    using var retryClient = CreateClient(endpointBaseUrl, apiToken);
                    using var retryResponse = await retryClient.PostAsJsonAsync(
                        relativePath,
                        request,
                        SerializerOptions,
                        cancellationToken).ConfigureAwait(false);
                    if (retryResponse.IsSuccessStatusCode)
                    {
                        return;
                    }

                    lastStatusCode = retryResponse.StatusCode;
                }
                else if (!string.Equals(endpointBaseUrl, DefaultLocalReviewBaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    lastException = ex;
                    continue;
                }
                else
                {
                    lastException = ex;
                }
            }
        }

        throw CreateFriendlyException(serverBaseUrl, lastStatusCode, lastException);
    }

    private HttpClient CreateClient(string endpointBaseUrl, string? apiToken)
    {
        var baseUri = new Uri($"{endpointBaseUrl.TrimEnd('/')}/", UriKind.Absolute);
        return _httpClientFactory(baseUri, apiToken);
    }

    private static HttpClient CreateDefaultHttpClient(Uri baseUri, string? apiToken)
    {
        var client = new HttpClient(SharedHttpMessageHandler.Instance, disposeHandler: false)
        {
            BaseAddress = baseUri
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(apiToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
        }

        return client;
    }

    private static IReadOnlyList<string> GetEndpointCandidates(string? serverBaseUrl)
    {
        var resolved = ResolveEndpointBaseUrl(serverBaseUrl);
        if (string.Equals(resolved, DefaultLocalReviewBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            return [resolved];
        }

        return [resolved, DefaultLocalReviewBaseUrl];
    }

    private async Task<bool> TryEnsureLocalServiceAndRetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _ensureLocalReviewServiceRunningAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> EnsureLocalReviewServiceRunningAsync(CancellationToken cancellationToken)
    {
        return await LocalCloudServiceBootstrapper.EnsureLocalServiceRunningAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Exception CreateFriendlyException(string? serverBaseUrl, HttpStatusCode? statusCode, Exception? exception)
    {
        if (statusCode == HttpStatusCode.NotFound)
        {
            return new InvalidOperationException(
                $"当前服务器 {ResolveEndpointBaseUrl(serverBaseUrl)} 没有部署知识审核接口。已尝试回退到本机 {DefaultLocalReviewBaseUrl}，并尝试自动拉起本地审核服务，但仍未成功。请确认 QuickReply.CloudService 可启动，或填写正确的审核服务地址。");
        }

        if (exception is not null)
        {
            return new InvalidOperationException(
                $"无法连接知识审核服务。已尝试当前服务器和本机 {DefaultLocalReviewBaseUrl}，并在需要时自动拉起本机审核服务。请确认审核服务可正常启动。原始错误：{exception.Message}",
                exception);
        }

        return new InvalidOperationException("知识审核服务返回了未预期的结果，请检查审核服务地址或稍后重试。");
    }
}
