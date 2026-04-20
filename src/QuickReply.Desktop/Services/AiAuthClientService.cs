using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace QuickReply.Desktop.Services;

public sealed class AiAuthClientService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<Uri, string?, HttpClient> _httpClientFactory;
    private readonly Func<CancellationToken, Task<bool>> _ensureLocalServiceRunningAsync;

    public AiAuthClientService(
        Func<Uri, string?, HttpClient>? httpClientFactory = null,
        Func<CancellationToken, Task<bool>>? ensureLocalServiceRunningAsync = null)
    {
        _httpClientFactory = httpClientFactory ?? CreateDefaultHttpClient;
        _ensureLocalServiceRunningAsync = ensureLocalServiceRunningAsync ?? LocalCloudServiceBootstrapper.EnsureLocalServiceRunningAsync;
    }

    public async Task<AiAuthStatusEndpointResult> GetStatusAsync(
        string? serverBaseUrl,
        string? apiToken,
        CancellationToken cancellationToken = default)
    {
        var primaryEndpoint = ResolveEndpointBaseUrl(serverBaseUrl);
        if (string.Equals(primaryEndpoint, LocalCloudServiceBootstrapper.DefaultLocalServiceBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            var localStatus = await GetStatusFromEndpointAsync(primaryEndpoint, apiToken, ensureLocalService: true, cancellationToken).ConfigureAwait(false);
            return BuildStatusResult(
                localStatus,
                primaryEndpoint,
                $"AI 认证服务地址：{primaryEndpoint}",
                "当前未配置独立服务器，直接使用本机 AI 服务。");
        }

        try
        {
            var remoteStatus = await GetStatusFromEndpointAsync(primaryEndpoint, apiToken, ensureLocalService: false, cancellationToken).ConfigureAwait(false);
            return BuildStatusResult(
                remoteStatus,
                primaryEndpoint,
                $"AI 认证服务地址：{primaryEndpoint}",
                $"当前服务器 {primaryEndpoint} 已部署 AI 认证接口。");
        }
        catch (Exception ex) when (ShouldFallbackToLocal(ex))
        {
            var fallbackStatus = await GetStatusFromEndpointAsync(
                LocalCloudServiceBootstrapper.DefaultLocalServiceBaseUrl,
                apiToken,
                ensureLocalService: true,
                cancellationToken).ConfigureAwait(false);
            return BuildStatusResult(
                fallbackStatus,
                LocalCloudServiceBootstrapper.DefaultLocalServiceBaseUrl,
                $"AI 认证服务地址：{LocalCloudServiceBootstrapper.DefaultLocalServiceBaseUrl}（已从 {primaryEndpoint} 回退）",
                $"当前服务器 {primaryEndpoint} {DescribeFailure(ex)}，已回退到本机 AI 服务。");
        }
    }

    public async Task<AiAuthActionEndpointResult> LoginAsync(
        string? serverBaseUrl,
        string? apiToken,
        bool useDeviceAuth = true,
        CancellationToken cancellationToken = default)
    {
        return await PostActionWithFallbackAsync(
            serverBaseUrl,
            apiToken,
            "/api/quickreply/ai-auth/login",
            new LoginRequest { UseDeviceAuth = useDeviceAuth },
            "登录",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiAuthActionEndpointResult> LogoutAsync(
        string? serverBaseUrl,
        string? apiToken,
        CancellationToken cancellationToken = default)
    {
        return await PostActionWithFallbackAsync(
            serverBaseUrl,
            apiToken,
            "/api/quickreply/ai-auth/logout",
            payload: null,
            "退出登录",
            cancellationToken).ConfigureAwait(false);
    }

    public static string ResolveEndpointBaseUrl(string? serverBaseUrl)
    {
        return SettingsInputValidator.ResolveOperationalServerBaseUrl(serverBaseUrl);
    }

    private async Task<AiAuthStatusResponse> GetStatusFromEndpointAsync(
        string endpointBaseUrl,
        string? apiToken,
        bool ensureLocalService,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(endpointBaseUrl, apiToken);
        try
        {
            using var response = await client.GetAsync("/api/quickreply/ai-auth/status", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateHttpFailureException(response.StatusCode);
            }

            var status = await response.Content.ReadFromJsonAsync<AiAuthStatusResponse>(SerializerOptions, cancellationToken).ConfigureAwait(false);
            return status ?? throw new InvalidOperationException("AI 认证服务返回了空结果。");
        }
        catch (Exception ex) when (ensureLocalService && IsRetryableLocalFailure(ex))
        {
            if (!await _ensureLocalServiceRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            using var retryClient = CreateClient(endpointBaseUrl, apiToken);
            using var retryResponse = await retryClient.GetAsync("/api/quickreply/ai-auth/status", cancellationToken).ConfigureAwait(false);
            if (!retryResponse.IsSuccessStatusCode)
            {
                throw CreateHttpFailureException(retryResponse.StatusCode);
            }

            var status = await retryResponse.Content.ReadFromJsonAsync<AiAuthStatusResponse>(SerializerOptions, cancellationToken).ConfigureAwait(false);
            return status ?? throw new InvalidOperationException("AI 认证服务返回了空结果。");
        }
    }

    private async Task<AiAuthActionEndpointResult> PostActionWithFallbackAsync(
        string? serverBaseUrl,
        string? apiToken,
        string relativePath,
        object? payload,
        string actionDisplayName,
        CancellationToken cancellationToken)
    {
        var primaryEndpoint = ResolveEndpointBaseUrl(serverBaseUrl);
        if (string.Equals(primaryEndpoint, LocalCloudServiceBootstrapper.DefaultLocalServiceBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            var action = await PostActionToEndpointAsync(
                primaryEndpoint,
                apiToken,
                relativePath,
                payload,
                ensureLocalService: true,
                cancellationToken).ConfigureAwait(false);
            return BuildActionResult(
                action,
                primaryEndpoint,
                $"AI 认证服务地址：{primaryEndpoint}",
                $"当前未配置独立服务器，已在本机执行“{actionDisplayName}”。");
        }

        try
        {
            var action = await PostActionToEndpointAsync(
                primaryEndpoint,
                apiToken,
                relativePath,
                payload,
                ensureLocalService: false,
                cancellationToken).ConfigureAwait(false);
            return BuildActionResult(
                action,
                primaryEndpoint,
                $"AI 认证服务地址：{primaryEndpoint}",
                $"已在当前服务器 {primaryEndpoint} 执行“{actionDisplayName}”。");
        }
        catch (Exception ex) when (ShouldFallbackToLocal(ex))
        {
            var fallbackAction = await PostActionToEndpointAsync(
                LocalCloudServiceBootstrapper.DefaultLocalServiceBaseUrl,
                apiToken,
                relativePath,
                payload,
                ensureLocalService: true,
                cancellationToken).ConfigureAwait(false);
            return BuildActionResult(
                fallbackAction,
                LocalCloudServiceBootstrapper.DefaultLocalServiceBaseUrl,
                $"AI 认证服务地址：{LocalCloudServiceBootstrapper.DefaultLocalServiceBaseUrl}（已从 {primaryEndpoint} 回退）",
                $"当前服务器 {primaryEndpoint} {DescribeFailure(ex)}，已回退到本机执行“{actionDisplayName}”。");
        }
    }

    private async Task<AiAuthActionResponse> PostActionToEndpointAsync(
        string endpointBaseUrl,
        string? apiToken,
        string relativePath,
        object? payload,
        bool ensureLocalService,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(endpointBaseUrl, apiToken);
        try
        {
            using var response = payload is null
                ? await client.PostAsync(relativePath, content: null, cancellationToken).ConfigureAwait(false)
                : await client.PostAsJsonAsync(relativePath, payload, SerializerOptions, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateHttpFailureException(response.StatusCode);
            }

            var action = await response.Content.ReadFromJsonAsync<AiAuthActionResponse>(SerializerOptions, cancellationToken).ConfigureAwait(false);
            return action ?? throw new InvalidOperationException("AI 认证服务返回了空结果。");
        }
        catch (Exception ex) when (ensureLocalService && IsRetryableLocalFailure(ex))
        {
            if (!await _ensureLocalServiceRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            using var retryClient = CreateClient(endpointBaseUrl, apiToken);
            using var retryResponse = payload is null
                ? await retryClient.PostAsync(relativePath, content: null, cancellationToken).ConfigureAwait(false)
                : await retryClient.PostAsJsonAsync(relativePath, payload, SerializerOptions, cancellationToken).ConfigureAwait(false);
            if (!retryResponse.IsSuccessStatusCode)
            {
                throw CreateHttpFailureException(retryResponse.StatusCode);
            }

            var action = await retryResponse.Content.ReadFromJsonAsync<AiAuthActionResponse>(SerializerOptions, cancellationToken).ConfigureAwait(false);
            return action ?? throw new InvalidOperationException("AI 认证服务返回了空结果。");
        }
    }

    private HttpClient CreateClient(string endpointBaseUrl, string? apiToken)
    {
        var baseUri = new Uri($"{endpointBaseUrl.TrimEnd('/')}/", UriKind.Absolute);
        return _httpClientFactory(baseUri, apiToken);
    }

    private static HttpClient CreateDefaultHttpClient(Uri baseUri, string? apiToken)
    {
        var client = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(apiToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
        }

        return client;
    }

    private static Exception CreateHttpFailureException(HttpStatusCode statusCode)
    {
        return new HttpRequestException($"status code {(int)statusCode}", null, statusCode);
    }

    private static bool ShouldFallbackToLocal(Exception ex)
    {
        if (ex is TaskCanceledException)
        {
            return true;
        }

        if (ex is HttpRequestException httpRequestException)
        {
            return httpRequestException.StatusCode is null or HttpStatusCode.NotFound or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
        }

        return false;
    }

    private static bool IsRetryableLocalFailure(Exception ex)
    {
        return ex is TaskCanceledException || ex is HttpRequestException;
    }

    private static string DescribeFailure(Exception ex)
    {
        if (ex is TaskCanceledException)
        {
            return "探测超时";
        }

        if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode.HasValue)
        {
            return $"返回 {(int)httpRequestException.StatusCode.Value}";
        }

        return $"不可达：{ex.Message}";
    }

    private static AiAuthStatusEndpointResult BuildStatusResult(
        AiAuthStatusResponse response,
        string effectiveEndpointBaseUrl,
        string endpointSummary,
        string probeSummary)
    {
        return new AiAuthStatusEndpointResult
        {
            CliAvailable = response.CliAvailable,
            LoggedIn = response.LoggedIn,
            LoginMode = response.LoginMode ?? string.Empty,
            LoginModeDisplayName = response.LoginModeDisplayName ?? "未登录",
            Message = response.Message ?? string.Empty,
            EffectiveEndpointBaseUrl = effectiveEndpointBaseUrl,
            EndpointSummary = endpointSummary,
            ProbeSummary = probeSummary
        };
    }

    private static AiAuthActionEndpointResult BuildActionResult(
        AiAuthActionResponse response,
        string effectiveEndpointBaseUrl,
        string endpointSummary,
        string probeSummary)
    {
        return new AiAuthActionEndpointResult
        {
            Success = response.Success,
            CliAvailable = response.CliAvailable,
            LoggedIn = response.LoggedIn,
            LoginMode = response.LoginMode ?? string.Empty,
            LoginModeDisplayName = response.LoginModeDisplayName ?? "未登录",
            Message = response.Message ?? string.Empty,
            CommandOutput = response.CommandOutput ?? string.Empty,
            EffectiveEndpointBaseUrl = effectiveEndpointBaseUrl,
            EndpointSummary = endpointSummary,
            ProbeSummary = probeSummary
        };
    }

    private sealed class LoginRequest
    {
        public bool UseDeviceAuth { get; init; }
    }
}

public class AiAuthStatusResponse
{
    public bool CliAvailable { get; init; }

    public bool LoggedIn { get; init; }

    public string? LoginMode { get; init; }

    public string? LoginModeDisplayName { get; init; }

    public string? Message { get; init; }
}

public class AiAuthActionResponse : AiAuthStatusResponse
{
    public bool Success { get; init; }

    public string? CommandOutput { get; init; }
}

public class AiAuthStatusEndpointResult : AiAuthStatusResponse
{
    public string EffectiveEndpointBaseUrl { get; init; } = string.Empty;

    public string EndpointSummary { get; init; } = string.Empty;

    public string ProbeSummary { get; init; } = string.Empty;
}

public class AiAuthActionEndpointResult : AiAuthActionResponse
{
    public string EffectiveEndpointBaseUrl { get; init; } = string.Empty;

    public string EndpointSummary { get; init; } = string.Empty;

    public string ProbeSummary { get; init; } = string.Empty;
}
