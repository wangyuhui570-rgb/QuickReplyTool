using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public readonly record struct UpdateCheckResult(
    bool Success,
    bool Skipped,
    string Message,
    bool HasAppUpdate,
    bool HasKnowledgeUpdate,
    CloudUpdateManifest? Manifest);

public sealed class UpdateCheckService : IUpdateCheckStateService
{
    internal const string DefaultUpdateServerBaseUrl = "http://45.207.201.102";
    private const string UpdateManifestPath = "/api/quickreply/update/manifest";

    private readonly SqliteAppDataStore _dataStore;
    private readonly Func<Uri, AppSettings, HttpClient> _httpClientFactory;
    private readonly ConversationLoggingService? _operationLogger;

    public UpdateCheckService(SqliteAppDataStore dataStore, ConversationLoggingService? operationLogger = null)
        : this(dataStore, CreateHttpClient, operationLogger)
    {
    }

    internal UpdateCheckService(
        SqliteAppDataStore dataStore,
        Func<Uri, AppSettings, HttpClient> httpClientFactory,
        ConversationLoggingService? operationLogger = null)
    {
        _dataStore = dataStore;
        _httpClientFactory = httpClientFactory;
        _operationLogger = operationLogger;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        AppSettings settingsSnapshot,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var stage = "validation";

        try
        {
        if (!settingsSnapshot.EnableUpdateChecks)
        {
            return await CompleteResultAsync(
                new UpdateCheckResult(false, true, "更新检查未开启。", false, false, null),
                stage,
                stopwatch,
                new { force }).ConfigureAwait(false);
        }

        if (!TryBuildBaseUri(settingsSnapshot.ServerBaseUrl, out var baseUri))
        {
            return await CompleteResultAsync(
                new UpdateCheckResult(false, true, "未配置可用的更新地址。", false, false, null),
                stage,
                stopwatch,
                new { force }).ConfigureAwait(false);
        }

        if (!force && settingsSnapshot.LastUpdateCheckAt?.LocalDateTime.Date == DateTime.Now.Date)
        {
            return await CompleteResultAsync(
                new UpdateCheckResult(false, true, "今天已检查过更新。", false, false, null),
                stage,
                stopwatch,
                new { force }).ConfigureAwait(false);
        }

        stage = "manifest_request";
        using var httpClient = _httpClientFactory(baseUri, settingsSnapshot);
        var requestUri = $"{UpdateManifestPath}?clientId={Uri.EscapeDataString(settingsSnapshot.SyncClientId)}&appVersion={Uri.EscapeDataString(AppIdentity.Version)}&knowledgeVersion={Uri.EscapeDataString(settingsSnapshot.LastKnownCloudKnowledgeVersion ?? string.Empty)}";
        var manifest = await httpClient.GetFromJsonAsync<CloudUpdateManifest>(requestUri, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return await CompleteResultAsync(
                new UpdateCheckResult(false, false, "服务器没有返回有效的更新清单。", false, false, null),
                "manifest_parse",
                stopwatch,
                new { force }).ConfigureAwait(false);
        }

        _ = ResolveAppUpdatePackage(manifest);

        stage = "persist_state";
        settingsSnapshot.LastUpdateCheckAt = DateTimeOffset.Now;
        await _dataStore.UpdateSettingsAsync(settingsSnapshot).ConfigureAwait(false);

        var hasAppUpdate = IsRemoteVersionNewer(manifest.LatestAppVersion, AppIdentity.Version);
        var hasKnowledgeUpdate = !string.IsNullOrWhiteSpace(manifest.KnowledgeVersion) &&
                                 !string.Equals(manifest.KnowledgeVersion, settingsSnapshot.LastKnownCloudKnowledgeVersion, StringComparison.OrdinalIgnoreCase);

        return await CompleteResultAsync(
            new UpdateCheckResult(
            true,
            false,
            hasAppUpdate || hasKnowledgeUpdate ? "发现可用更新。" : "当前已是最新状态。",
            hasAppUpdate,
            hasKnowledgeUpdate,
            manifest),
            "completed",
            stopwatch,
            new
            {
                force,
                hasAppUpdate,
                hasKnowledgeUpdate,
                packageKind = manifest.LatestAppPackageKind
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TryLogOperationEventAsync(
                    stage,
                    success: false,
                    skipped: false,
                    stopwatch.ElapsedMilliseconds,
                    ex.Message,
                    new { force })
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task MarkKnowledgeVersionSeenAsync(AppSettings settingsSnapshot, string knowledgeVersion)
    {
        settingsSnapshot.LastKnownCloudKnowledgeVersion = knowledgeVersion;
        await _dataStore.UpdateSettingsAsync(settingsSnapshot).ConfigureAwait(false);
    }

    internal static AppUpdatePackageDescriptor ResolveAppUpdatePackage(CloudUpdateManifest manifest)
    {
        return AppUpdatePackageResolver.Resolve(manifest);
    }

    private static HttpClient CreateHttpClient(Uri baseUri, AppSettings settingsSnapshot)
    {
        var client = new HttpClient(SharedHttpMessageHandler.Instance, disposeHandler: false)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Add("X-QuickReply-ClientId", settingsSnapshot.SyncClientId);
        client.DefaultRequestHeaders.Add("X-QuickReply-AppVersion", AppIdentity.Version);

        if (!string.IsNullOrWhiteSpace(settingsSnapshot.SyncApiToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settingsSnapshot.SyncApiToken);
        }

        return client;
    }

    private static bool IsRemoteVersionNewer(string remoteVersion, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(remoteVersion))
        {
            return false;
        }

        if (Version.TryParse(NormalizeVersion(remoteVersion), out var remote) &&
            Version.TryParse(NormalizeVersion(currentVersion), out var current))
        {
            return remote > current;
        }

        return !string.Equals(remoteVersion.Trim(), currentVersion.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersion(string value)
    {
        var normalized = value.Trim();
        return normalized.Count(character => character == '.') >= 2 ? normalized : $"{normalized}.0";
    }

    internal static bool TryBuildBaseUri(string serverBaseUrl, out Uri baseUri)
    {
        var normalizedServerBaseUrl = (serverBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        var shouldUseDefaultUpdateServer =
            string.IsNullOrWhiteSpace(normalizedServerBaseUrl) ||
            string.Equals(
                normalizedServerBaseUrl,
                QuickReplyServiceEndpointDefaults.DefaultRemoteServiceBaseUrl,
                StringComparison.OrdinalIgnoreCase);

        if (!shouldUseDefaultUpdateServer)
        {
            return SettingsInputValidator.TryBuildHttpBaseUri(normalizedServerBaseUrl, out baseUri);
        }

        return SettingsInputValidator.TryBuildHttpBaseUri(DefaultUpdateServerBaseUrl, out baseUri);
    }

    private async Task<UpdateCheckResult> CompleteResultAsync(
        UpdateCheckResult result,
        string stage,
        Stopwatch stopwatch,
        object? details = null)
    {
        await TryLogOperationEventAsync(
                stage,
                result.Success,
                result.Skipped,
                stopwatch.ElapsedMilliseconds,
                result.Message,
                details)
            .ConfigureAwait(false);
        return result;
    }

    private Task TryLogOperationEventAsync(
        string stage,
        bool success,
        bool skipped,
        long durationMs,
        string message,
        object? details = null)
    {
        return _operationLogger?.LogOperationEventAsync(
                   "update_check",
                   stage,
                   success,
                   skipped,
                   durationMs,
                   message,
                   details)
               ?? Task.CompletedTask;
    }
}
