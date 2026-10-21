using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public readonly record struct KnowledgeSyncRunResult(
    bool Success,
    bool Skipped,
    string Message,
    int CategoryCount,
    int PhraseCount,
    int QuickTagCount,
    int FileItemCount,
    int ConversationLogCount,
    int RecommendationEventCount,
    int RecognitionCorrectionCount);

public sealed class KnowledgeSyncService
{
    private const string KnowledgeSyncPath = "/api/quickreply/sync/knowledge-delta";
    private const string ConversationSyncPath = "/api/quickreply/sync/conversation-delta";
    private const string PublishedKnowledgeSyncPath = "/api/quickreply/knowledge/published";

    private readonly SqliteAppDataStore _dataStore;
    private readonly ConversationLoggingService _conversationLoggingService;
    private readonly PublishedKnowledgeCatalogService? _publishedKnowledgeCatalog;
    private readonly Func<Uri, AppSettings, HttpClient> _httpClientFactory;

    public KnowledgeSyncService(
        SqliteAppDataStore dataStore,
        ConversationLoggingService conversationLoggingService,
        PublishedKnowledgeCatalogService? publishedKnowledgeCatalog = null)
        : this(dataStore, conversationLoggingService, publishedKnowledgeCatalog, CreateHttpClient)
    {
    }

    internal KnowledgeSyncService(
        SqliteAppDataStore dataStore,
        ConversationLoggingService conversationLoggingService,
        PublishedKnowledgeCatalogService? publishedKnowledgeCatalog,
        Func<Uri, AppSettings, HttpClient> httpClientFactory)
    {
        _dataStore = dataStore;
        _conversationLoggingService = conversationLoggingService;
        _publishedKnowledgeCatalog = publishedKnowledgeCatalog;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<KnowledgeSyncRunResult> RunScheduledSyncAsync(
        AppSettings settingsSnapshot,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var stage = "validation";

        try
        {
            if (!settingsSnapshot.EnableCloudSync)
            {
                return await CompleteResultAsync(
                    new KnowledgeSyncRunResult(false, true, "云同步未开启。", 0, 0, 0, 0, 0, 0, 0),
                    stage,
                    stopwatch,
                    new { force }).ConfigureAwait(false);
            }

            if (!TryBuildBaseUri(settingsSnapshot.ServerBaseUrl, out var baseUri))
            {
                return await CompleteResultAsync(
                    new KnowledgeSyncRunResult(false, true, "未配置可用的服务器地址。", 0, 0, 0, 0, 0, 0, 0),
                    stage,
                    stopwatch,
                    new { force }).ConfigureAwait(false);
            }

            if (!force &&
                settingsSnapshot.LastKnowledgeSyncAt?.LocalDateTime.Date == DateTime.Now.Date &&
                settingsSnapshot.LastConversationSyncAt?.LocalDateTime.Date == DateTime.Now.Date &&
                settingsSnapshot.LastPublishedKnowledgeSyncAt?.LocalDateTime.Date == DateTime.Now.Date)
            {
                return await CompleteResultAsync(
                    new KnowledgeSyncRunResult(false, true, "今天已完成同步。", 0, 0, 0, 0, 0, 0, 0),
                    stage,
                    stopwatch,
                    new { force }).ConfigureAwait(false);
            }

            stage = "build_payload";
            var clientId = settingsSnapshot.SyncClientId;
            var knowledgePayload = await BuildKnowledgePayloadAsync(settingsSnapshot, clientId).ConfigureAwait(false);
            var conversationPayload = await _conversationLoggingService
                .ExportSyncPayloadAsync(
                    clientId,
                    settingsSnapshot.LastConversationSyncAt,
                    settingsSnapshot.AllowRawConversationCloudSync)
                .ConfigureAwait(false);

            using var httpClient = _httpClientFactory(baseUri, settingsSnapshot);

            if (knowledgePayload.Categories.Count > 0 ||
                knowledgePayload.Phrases.Count > 0 ||
                knowledgePayload.QuickTags.Count > 0 ||
                knowledgePayload.FileItems.Count > 0)
            {
                stage = "knowledge_upload";
                using var knowledgeResponse = await httpClient
                    .PostAsJsonAsync(KnowledgeSyncPath, knowledgePayload, cancellationToken)
                    .ConfigureAwait(false);
                knowledgeResponse.EnsureSuccessStatusCode();
            }
            settingsSnapshot.LastKnowledgeSyncAt = knowledgePayload.GeneratedAt;

            if (conversationPayload.ConversationLogs.Count > 0 ||
                conversationPayload.RecommendationEvents.Count > 0 ||
                conversationPayload.RecognitionCorrections.Count > 0)
            {
                stage = "conversation_upload";
                using var conversationResponse = await httpClient
                    .PostAsJsonAsync(ConversationSyncPath, conversationPayload, cancellationToken)
                    .ConfigureAwait(false);
                conversationResponse.EnsureSuccessStatusCode();
            }
            settingsSnapshot.LastConversationSyncAt = conversationPayload.GeneratedAt;

            stage = "published_knowledge_download";
            var publishedKnowledgeResult = await TrySyncPublishedKnowledgeAsync(
                httpClient,
                settingsSnapshot,
                cancellationToken).ConfigureAwait(false);
            settingsSnapshot.LastPublishedKnowledgeSyncAt = publishedKnowledgeResult.SyncedAt;
            settingsSnapshot.LastPublishedKnowledgeRevision = publishedKnowledgeResult.LatestRevision;
            if (!string.IsNullOrWhiteSpace(publishedKnowledgeResult.KnowledgeVersion))
            {
                settingsSnapshot.LastPublishedKnowledgeVersion = publishedKnowledgeResult.KnowledgeVersion;
            }

            stage = "persist_state";
            await _dataStore.UpdateSettingsAsync(settingsSnapshot).ConfigureAwait(false);
            if (publishedKnowledgeResult.ReloadCatalog && _publishedKnowledgeCatalog is not null)
            {
                await _publishedKnowledgeCatalog.ReloadAsync().ConfigureAwait(false);
            }

            return await CompleteResultAsync(
                new KnowledgeSyncRunResult(
                    true,
                    false,
                    BuildSyncCompletionMessage(publishedKnowledgeResult),
                    knowledgePayload.Categories.Count,
                    knowledgePayload.Phrases.Count,
                    knowledgePayload.QuickTags.Count,
                    knowledgePayload.FileItems.Count,
                    conversationPayload.ConversationLogs.Count,
                    conversationPayload.RecommendationEvents.Count,
                    conversationPayload.RecognitionCorrections.Count),
                "completed",
                stopwatch,
                new
                {
                    force,
                    knowledgeCategories = knowledgePayload.Categories.Count,
                    knowledgePhrases = knowledgePayload.Phrases.Count,
                    quickTags = knowledgePayload.QuickTags.Count,
                    fileItems = knowledgePayload.FileItems.Count,
                    conversationLogs = conversationPayload.ConversationLogs.Count,
                    recommendationEvents = conversationPayload.RecommendationEvents.Count,
                    recognitionCorrections = conversationPayload.RecognitionCorrections.Count,
                    publishedKnowledgeCards = publishedKnowledgeResult.CardCount,
                    removedPublishedKnowledgeCards = publishedKnowledgeResult.RemovedCardCount,
                    publishedKnowledgeRevision = publishedKnowledgeResult.LatestRevision,
                    publishedKnowledgeVersion = publishedKnowledgeResult.KnowledgeVersion,
                    publishedKnowledgeSkipped = publishedKnowledgeResult.Skipped,
                    publishedKnowledgeSkipReason = publishedKnowledgeResult.SkipReason
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

    private async Task<KnowledgeSyncPayload> BuildKnowledgePayloadAsync(AppSettings settingsSnapshot, string clientId)
    {
        var appData = await _dataStore.LoadAsync().ConfigureAwait(false);
        var since = settingsSnapshot.LastKnowledgeSyncAt;
        var fileLibraryRoot = settingsSnapshot.FileLibraryFolderPath;

        return new KnowledgeSyncPayload
        {
            ClientId = clientId,
            AppVersion = AppIdentity.Version,
            GeneratedAt = DateTimeOffset.Now,
            Since = since,
            IsFullSync = since is null,
            Categories = appData.Categories
                .Where(category => since is null || category.UpdatedAt > since.Value)
                .Select(category => new KnowledgeCategorySyncItem
                {
                    Id = category.Id,
                    Name = category.Name,
                    SortOrder = category.SortOrder,
                    CreatedAt = category.CreatedAt,
                    UpdatedAt = category.UpdatedAt
                })
                .ToArray(),
            Phrases = appData.Categories
                .SelectMany(category => category.Phrases)
                .Where(phrase => since is null || phrase.UpdatedAt > since.Value)
                .Select(phrase => new KnowledgePhraseSyncItem
                {
                    Id = phrase.Id,
                    CategoryId = phrase.CategoryId,
                    Title = phrase.Title,
                    Tags = phrase.Tags,
                    Content = phrase.Content,
                    SortOrder = phrase.SortOrder,
                    CreatedAt = phrase.CreatedAt,
                    UpdatedAt = phrase.UpdatedAt
                })
                .ToArray(),
            QuickTags = appData.QuickTags
                .Where(tag => since is null || tag.CreatedAt > since.Value)
                .Select(tag => new KnowledgeQuickTagSyncItem
                {
                    Id = tag.Id,
                    PhraseId = tag.PhraseId,
                    SortOrder = tag.SortOrder,
                    CreatedAt = tag.CreatedAt
                })
                .ToArray(),
            FileItems = appData.FileItems
                .Where(fileItem => since is null || fileItem.UpdatedAt > since.Value)
                .Select(fileItem => new KnowledgeFileSyncItem
                {
                    Id = fileItem.Id,
                    DisplayName = fileItem.DisplayName,
                    RelativePath = BuildRelativePath(fileLibraryRoot, fileItem.FullPath),
                    FullPath = string.Empty,
                    Tags = string.Empty,
                    CreatedAt = fileItem.CreatedAt,
                    UpdatedAt = fileItem.UpdatedAt
                })
                .ToArray()
        };
    }

    private async Task<PublishedKnowledgeSyncResult> TrySyncPublishedKnowledgeAsync(
        HttpClient httpClient,
        AppSettings settingsSnapshot,
        CancellationToken cancellationToken)
    {
        var initialRevision = Math.Max(0, settingsSnapshot.LastPublishedKnowledgeRevision);
        var cursor = string.Empty;
        var totalCardCount = 0;
        var totalRemovedCount = 0;
        var latestRevision = initialRevision;
        var latestGeneratedAt = settingsSnapshot.LastPublishedKnowledgeSyncAt ?? DateTimeOffset.Now;
        var knowledgeVersion = settingsSnapshot.LastPublishedKnowledgeVersion;
        var reloadCatalog = false;

        while (true)
        {
            var sinceQuery = settingsSnapshot.LastPublishedKnowledgeSyncAt?.ToString("O") ?? string.Empty;
            var requestUri =
                $"{PublishedKnowledgeSyncPath}?since={Uri.EscapeDataString(sinceQuery)}" +
                $"&sinceRevision={Uri.EscapeDataString(initialRevision.ToString())}" +
                $"&cursor={Uri.EscapeDataString(cursor)}" +
                "&limit=200";
            using var response = await httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return PublishedKnowledgeSyncResult.SkippedResult(DateTimeOffset.Now, "published_endpoint_not_available");
            }

            response.EnsureSuccessStatusCode();
            var payload = await response.Content
                .ReadFromJsonAsync<PublishedKnowledgeSyncResponse>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (payload is null)
            {
                return PublishedKnowledgeSyncResult.Completed(DateTimeOffset.Now, string.Empty, initialRevision, 0, 0, reloadCatalog: false);
            }

            var cards = payload.Cards
                .Select(item => new PublishedKnowledgeCard
                {
                    Id = item.Id,
                    CandidateKind = item.CandidateKind,
                    Platform = item.Platform,
                    ProductKey = item.ProductKey,
                    Question = item.Question,
                    Answer = item.Answer,
                    Synonyms = item.Synonyms,
                    Tags = item.Tags,
                    UsageNotes = item.UsageNotes,
                    Version = item.Version,
                    UpdatedAt = item.UpdatedAt
                })
                .ToArray();

            if (cards.Length > 0)
            {
                await _dataStore.UpsertPublishedKnowledgeCardsAsync(cards).ConfigureAwait(false);
                totalCardCount += cards.Length;
                reloadCatalog = true;
            }

            if (payload.RemovedCardIds.Count > 0)
            {
                await _dataStore.DeletePublishedKnowledgeCardsAsync(payload.RemovedCardIds).ConfigureAwait(false);
                totalRemovedCount += payload.RemovedCardIds.Count;
                reloadCatalog = true;
            }

            latestGeneratedAt = payload.GeneratedAt == default ? latestGeneratedAt : payload.GeneratedAt;
            if (!string.IsNullOrWhiteSpace(payload.KnowledgeVersion))
            {
                knowledgeVersion = payload.KnowledgeVersion;
            }

            latestRevision = Math.Max(latestRevision, payload.LatestRevision);
            if (!payload.HasMore || string.IsNullOrWhiteSpace(payload.NextCursor))
            {
                break;
            }

            cursor = payload.NextCursor;
        }

        return PublishedKnowledgeSyncResult.Completed(
            latestGeneratedAt,
            knowledgeVersion,
            latestRevision,
            totalCardCount,
            totalRemovedCount,
            reloadCatalog);
    }

    private static HttpClient CreateHttpClient(Uri baseUri, AppSettings settingsSnapshot)
    {
        var client = new HttpClient(SharedHttpMessageHandler.Instance, disposeHandler: false)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(25)
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

    private static string BuildRelativePath(string rootPath, string fullPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(fullPath))
            {
                return string.Empty;
            }

            var normalizedRoot = Path.GetFullPath(rootPath);
            var normalizedFullPath = Path.GetFullPath(fullPath);
            return normalizedFullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(normalizedRoot, normalizedFullPath)
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryBuildBaseUri(string serverBaseUrl, out Uri baseUri)
    {
        return SettingsInputValidator.TryBuildConfiguredServerBaseUri(serverBaseUrl, out baseUri);
    }

    private static string BuildSyncCompletionMessage(PublishedKnowledgeSyncResult publishedKnowledgeResult)
    {
        if (publishedKnowledgeResult.CardCount == 0 && publishedKnowledgeResult.RemovedCardCount == 0)
        {
            return "云同步完成。";
        }

        var messageParts = new List<string>();
        if (publishedKnowledgeResult.CardCount > 0)
        {
            messageParts.Add($"更新 {publishedKnowledgeResult.CardCount} 条已发布知识");
        }

        if (publishedKnowledgeResult.RemovedCardCount > 0)
        {
            messageParts.Add($"移除 {publishedKnowledgeResult.RemovedCardCount} 条已下线知识");
        }

        return $"云同步完成，已{string.Join("，", messageParts)}。";
    }

    private async Task<KnowledgeSyncRunResult> CompleteResultAsync(
        KnowledgeSyncRunResult result,
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
        return _conversationLoggingService.LogOperationEventAsync(
            "cloud_sync",
            stage,
            success,
            skipped,
            durationMs,
            message,
            details);
    }

    private readonly record struct PublishedKnowledgeSyncResult(
        DateTimeOffset SyncedAt,
        string KnowledgeVersion,
        long LatestRevision,
        int CardCount,
        int RemovedCardCount,
        bool ReloadCatalog,
        bool Skipped,
        string SkipReason)
    {
        public static PublishedKnowledgeSyncResult Completed(
            DateTimeOffset syncedAt,
            string knowledgeVersion,
            long latestRevision,
            int cardCount,
            int removedCardCount,
            bool reloadCatalog)
        {
            return new PublishedKnowledgeSyncResult(
                syncedAt,
                knowledgeVersion,
                latestRevision,
                cardCount,
                removedCardCount,
                reloadCatalog,
                Skipped: false,
                SkipReason: string.Empty);
        }

        public static PublishedKnowledgeSyncResult SkippedResult(DateTimeOffset syncedAt, string skipReason)
        {
            return new PublishedKnowledgeSyncResult(
                syncedAt,
                string.Empty,
                0,
                0,
                0,
                ReloadCatalog: false,
                Skipped: true,
                SkipReason: skipReason);
        }
    }
}
