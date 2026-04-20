using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;

namespace QuickReply.Desktop.Tests;

public sealed class UpdateCheckServiceTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsManifestAndPersistsTimestamp()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();

        var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            HttpResponseFactory.Json("""{"latestAppVersion":"99.99.99","latestAppPackageKind":"zip_ota","latestAppDownloadUrl":"https://updates.example.com/quickreply.zip","latestAppSha256":"abc123","latestAppSizeBytes":2048,"latestAppReleaseChannel":"stable","knowledgeVersion":"knowledge-v2"}""")));

        var service = new UpdateCheckService(
            store,
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var settings = new AppSettings
        {
            EnableUpdateChecks = true,
            ServerBaseUrl = "https://updates.example.com/",
            SyncClientId = "client-001",
            LastKnownCloudKnowledgeVersion = "knowledge-v1"
        };

        var result = await service.CheckForUpdatesAsync(settings, force: true);
        var persisted = await store.LoadSettingsOnlyAsync();

        Assert.True(result.Success);
        Assert.False(result.Skipped);
        Assert.True(result.HasAppUpdate);
        Assert.True(result.HasKnowledgeUpdate);
        Assert.NotNull(result.Manifest);
        var packageDescriptor = UpdateCheckService.ResolveAppUpdatePackage(result.Manifest!);
        Assert.Equal(AppUpdatePackageKind.ZipOta, packageDescriptor.Kind);
        Assert.Equal("ABC123", packageDescriptor.ExpectedSha256);
        Assert.Equal(2048, packageDescriptor.ExpectedSizeBytes);
        Assert.Single(handler.Requests);
        Assert.Equal("/api/quickreply/update/manifest", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.NotNull(persisted.LastUpdateCheckAt);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_SkipsWhenDisabled()
    {
        using var scope = new TestAppDataScope();
        var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(HttpResponseFactory.Json("{}")));
        var service = new UpdateCheckService(
            scope.CreateDataStore(),
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var result = await service.CheckForUpdatesAsync(new AppSettings
        {
            EnableUpdateChecks = false,
            ServerBaseUrl = "https://updates.example.com/"
        });

        Assert.True(result.Skipped);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_SkipsSameDayChecksUnlessForced()
    {
        using var scope = new TestAppDataScope();
        var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(HttpResponseFactory.Json("{}")));
        var service = new UpdateCheckService(
            scope.CreateDataStore(),
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var result = await service.CheckForUpdatesAsync(new AppSettings
        {
            EnableUpdateChecks = true,
            ServerBaseUrl = "https://updates.example.com/",
            LastUpdateCheckAt = DateTimeOffset.Now
        });

        Assert.True(result.Skipped);
        Assert.Equal("今天已检查过更新。", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_UsesDefaultUpdateServerWhenCustomServerIsEmpty()
    {
        using var scope = new TestAppDataScope();
        var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            HttpResponseFactory.Json("""{"latestAppVersion":"99.99.99"}""")));
        var service = new UpdateCheckService(
            scope.CreateDataStore(),
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var result = await service.CheckForUpdatesAsync(new AppSettings
        {
            EnableUpdateChecks = true,
            ServerBaseUrl = string.Empty
        }, force: true);

        Assert.True(result.Success);
        Assert.Single(handler.Requests);
        Assert.Equal("45.207.201.102", handler.Requests[0].RequestUri!.Host);
        Assert.Equal("/api/quickreply/update/manifest", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_SkipsUnsupportedServerScheme()
    {
        using var scope = new TestAppDataScope();
        var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(HttpResponseFactory.Json("{}")));
        var service = new UpdateCheckService(
            scope.CreateDataStore(),
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var result = await service.CheckForUpdatesAsync(new AppSettings
        {
            EnableUpdateChecks = true,
            ServerBaseUrl = "ftp://updates.example.com/"
        }, force: true);

        Assert.True(result.Skipped);
        Assert.Equal("未配置可用的更新地址。", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MarkKnowledgeVersionSeenAsync_PersistsLatestVersion()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();

        var service = new UpdateCheckService(
            store,
            (baseUri, _) => new HttpClient(new RecordingHttpMessageHandler((_, _) =>
                Task.FromResult(HttpResponseFactory.Json("{}"))))
            {
                BaseAddress = baseUri
            });

        var settings = new AppSettings
        {
            LastKnownCloudKnowledgeVersion = "knowledge-v1"
        };

        await service.MarkKnowledgeVersionSeenAsync(settings, "knowledge-v2");
        var persisted = await store.LoadSettingsOnlyAsync();

        Assert.Equal("knowledge-v2", persisted.LastKnownCloudKnowledgeVersion);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WritesOperationEvent()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        await using var loggingService = scope.CreateConversationLoggingService();
        var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            HttpResponseFactory.Json("""{"latestAppVersion":"99.99.99","latestAppPackageKind":"zip_ota","latestAppDownloadUrl":"https://updates.example.com/quickreply.zip"}""")));
        var service = new UpdateCheckService(
            store,
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            },
            loggingService);

        await service.CheckForUpdatesAsync(new AppSettings
        {
            EnableUpdateChecks = true,
            ServerBaseUrl = "https://updates.example.com/",
            SyncClientId = "client-001"
        }, force: true);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(scope.LogsRootPath, "conversation-logs.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM operation_events WHERE operation_name = 'update_check';";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }
}
