using System.Text.Json;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;

namespace QuickReply.Desktop.Tests;

public sealed class KnowledgeSyncServiceTests
{
    [Fact]
    public async Task RunScheduledSyncAsync_UploadsKnowledgePayloadAndPersistsSyncTime()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();

        var filePath = scope.CreateLibraryFile("kb\\guide.txt", "guide");
        await store.UpsertFileItemAsync(new LibraryFileItem
        {
            DisplayName = "Guide",
            FullPath = filePath
        });

        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/api/quickreply/knowledge/published" => HttpResponseFactory.Json("""{"knowledgeVersion":"knowledge-r12","generatedAt":"2026-04-16T12:00:00+08:00","latestRevision":12,"nextCursor":"","hasMore":false,"cards":[{"id":"11111111-1111-1111-1111-111111111111","candidateKind":"resource","platform":"wecom","productKey":"SKU-1","question":"这款今天能发吗","answer":"您好，这款今天可以安排发出。","synonyms":["发货时效"],"tags":["发货"],"usageNotes":"仅限现货","version":"v1","revision":12,"updatedAt":"2026-04-16T12:00:00+08:00"}],"removedCardIds":[]}"""),
                _ => HttpResponseFactory.Json("{}")
            });
        });
        await using var loggingService = scope.CreateConversationLoggingService();
        var knowledgeCatalog = new PublishedKnowledgeCatalogService(store);
        var service = new KnowledgeSyncService(
            store,
            loggingService,
            knowledgeCatalog,
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var settings = new AppSettings
        {
            EnableCloudSync = true,
            ServerBaseUrl = "https://sync.example.com/",
            SyncClientId = "client-001",
            SyncApiToken = "token-001",
            FileLibraryFolderPath = scope.LibraryRootPath
        };

        var result = await service.RunScheduledSyncAsync(settings, force: true);
        var persisted = await store.LoadSettingsOnlyAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.FileItemCount);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/quickreply/sync/knowledge-delta", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/api/quickreply/knowledge/published", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Contains("sinceRevision=0", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(handler.Requests[0].Body);
        var relativePath = json.RootElement
            .GetProperty("fileItems")[0]
            .GetProperty("relativePath")
            .GetString();
        var fullPath = json.RootElement
            .GetProperty("fileItems")[0]
            .GetProperty("fullPath")
            .GetString();

        Assert.Equal(Path.GetRelativePath(scope.LibraryRootPath, filePath), relativePath);
        Assert.Equal(string.Empty, fullPath);
        Assert.NotNull(persisted.LastKnowledgeSyncAt);
        Assert.NotNull(persisted.LastConversationSyncAt);
        Assert.Equal("knowledge-r12", persisted.LastPublishedKnowledgeVersion);
        Assert.Equal(12, persisted.LastPublishedKnowledgeRevision);
        Assert.NotNull(persisted.LastPublishedKnowledgeSyncAt);
        await knowledgeCatalog.ReloadAsync();
        var card = Assert.Single(knowledgeCatalog.GetAllPublishedKnowledgeCards());
        Assert.Equal("resource", card.CandidateKind);
    }

    [Fact]
    public async Task RunScheduledSyncAsync_SkipsWhenDisabled()
    {
        using var scope = new TestAppDataScope();
        var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(HttpResponseFactory.Json("{}")));
        await using var loggingService = scope.CreateConversationLoggingService();
        var service = new KnowledgeSyncService(
            scope.CreateDataStore(),
            loggingService,
            publishedKnowledgeCatalog: null,
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var result = await service.RunScheduledSyncAsync(new AppSettings
        {
            EnableCloudSync = false,
            ServerBaseUrl = "https://sync.example.com/"
        });

        Assert.True(result.Skipped);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunScheduledSyncAsync_SkipsWhenAlreadySyncedToday()
    {
        using var scope = new TestAppDataScope();
        var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(HttpResponseFactory.Json("{}")));
        await using var loggingService = scope.CreateConversationLoggingService();
        var service = new KnowledgeSyncService(
            scope.CreateDataStore(),
            loggingService,
            publishedKnowledgeCatalog: null,
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var result = await service.RunScheduledSyncAsync(new AppSettings
        {
            EnableCloudSync = true,
            ServerBaseUrl = "https://sync.example.com/",
            LastKnowledgeSyncAt = DateTimeOffset.Now,
            LastConversationSyncAt = DateTimeOffset.Now,
            LastPublishedKnowledgeSyncAt = DateTimeOffset.Now
        });

        Assert.True(result.Skipped);
        Assert.Equal("今天已完成同步。", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunScheduledSyncAsync_FallsBackToBuiltInServerWhenConfiguredServerSchemeIsUnsupported()
    {
        using var scope = new TestAppDataScope();
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            Assert.StartsWith($"{QuickReplyServiceEndpointDefaults.DefaultRemoteServiceBaseUrl}/api/quickreply/", request.RequestUri?.ToString(), StringComparison.Ordinal);
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/api/quickreply/knowledge/published" => HttpResponseFactory.Json("""{"knowledgeVersion":"knowledge-r0","generatedAt":"2026-04-18T12:00:00+08:00","latestRevision":0,"nextCursor":"","hasMore":false,"cards":[],"removedCardIds":[]}"""),
                _ => HttpResponseFactory.Json("{}")
            });
        });
        await using var loggingService = scope.CreateConversationLoggingService();
        var service = new KnowledgeSyncService(
            scope.CreateDataStore(),
            loggingService,
            publishedKnowledgeCatalog: null,
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var result = await service.RunScheduledSyncAsync(new AppSettings
        {
            EnableCloudSync = true,
            ServerBaseUrl = "ftp://sync.example.com/"
        }, force: true);

        Assert.True(result.Success);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RunScheduledSyncAsync_WritesOperationEvent()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(HttpResponseFactory.Json("{}")));
        await using var loggingService = scope.CreateConversationLoggingService();
        var service = new KnowledgeSyncService(
            store,
            loggingService,
            publishedKnowledgeCatalog: null,
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        await service.RunScheduledSyncAsync(new AppSettings
        {
            EnableCloudSync = true,
            ServerBaseUrl = "https://sync.example.com/",
            SyncClientId = "client-001"
        }, force: true);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(scope.LogsRootPath, "conversation-logs.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM operation_events
            WHERE operation_name = 'cloud_sync' AND stage = 'completed';
            """;

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RunScheduledSyncAsync_SkipsPublishedKnowledgeEndpoint_WhenServerDoesNotSupportIt()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/quickreply/knowledge/published")
            {
                return Task.FromResult(HttpResponseFactory.Json("{}", System.Net.HttpStatusCode.NotFound));
            }

            return Task.FromResult(HttpResponseFactory.Json("{}"));
        });
        await using var loggingService = scope.CreateConversationLoggingService();
        var service = new KnowledgeSyncService(
            store,
            loggingService,
            publishedKnowledgeCatalog: null,
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var settings = new AppSettings
        {
            EnableCloudSync = true,
            ServerBaseUrl = "https://sync.example.com/",
            SyncClientId = "client-001"
        };

        var result = await service.RunScheduledSyncAsync(settings, force: true);

        Assert.True(result.Success);
        Assert.Equal(1, handler.Requests.Count(request => request.RequestUri!.AbsolutePath == "/api/quickreply/knowledge/published"));
    }

    [Fact]
    public async Task RunScheduledSyncAsync_RemovesUnpublishedKnowledgeFromLocalCatalog()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.UpsertPublishedKnowledgeCardsAsync(
        [
            new PublishedKnowledgeCard
            {
                Id = Guid.Parse("99999999-9999-9999-9999-999999999999"),
                CandidateKind = "qa",
                Platform = "wecom",
                ProductKey = "SKU-OLD",
                Question = "旧知识",
                Answer = "旧答案",
                Synonyms = ["旧问法"],
                Tags = ["旧标签"],
                UsageNotes = string.Empty,
                Version = "v1",
                UpdatedAt = DateTimeOffset.Parse("2026-04-16T10:00:00+08:00")
            }
        ]);

        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/api/quickreply/knowledge/published" => HttpResponseFactory.Json("""{"knowledgeVersion":"knowledge-r20","generatedAt":"2026-04-17T12:00:00+08:00","latestRevision":20,"nextCursor":"","hasMore":false,"cards":[],"removedCardIds":["99999999-9999-9999-9999-999999999999"]}"""),
                _ => HttpResponseFactory.Json("{}")
            });
        });
        await using var loggingService = scope.CreateConversationLoggingService();
        var knowledgeCatalog = new PublishedKnowledgeCatalogService(store);
        await knowledgeCatalog.ReloadAsync();
        var service = new KnowledgeSyncService(
            store,
            loggingService,
            knowledgeCatalog,
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var settings = new AppSettings
        {
            EnableCloudSync = true,
            ServerBaseUrl = "https://sync.example.com/",
            SyncClientId = "client-001"
        };

        var result = await service.RunScheduledSyncAsync(settings, force: true);
        var persisted = await store.LoadSettingsOnlyAsync();
        await knowledgeCatalog.ReloadAsync();

        Assert.True(result.Success);
        Assert.Equal("云同步完成，已移除 1 条已下线知识。", result.Message);
        Assert.Equal("knowledge-r20", persisted.LastPublishedKnowledgeVersion);
        Assert.Equal(20, persisted.LastPublishedKnowledgeRevision);
        Assert.Empty(knowledgeCatalog.GetAllPublishedKnowledgeCards());
    }
}
