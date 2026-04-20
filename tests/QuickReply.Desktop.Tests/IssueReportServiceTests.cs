using System.Text.Json;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;

namespace QuickReply.Desktop.Tests;

public sealed class IssueReportServiceTests
{
    [Fact]
    public async Task CreateReportAsync_ReturnsValidationFailureWhenDescriptionIsBlank()
    {
        using var scope = new TestAppDataScope();
        var service = new IssueReportService(
            scope.CreateDataStore(),
            (baseUri, _, _) => new HttpClient(new RecordingHttpMessageHandler((_, _) =>
                Task.FromResult(HttpResponseFactory.Json("{}"))))
            {
                BaseAddress = baseUri
            });

        var result = await service.CreateReportAsync("   ");

        Assert.False(result.Success);
        Assert.Equal("请先填写遇到的问题。", result.Message);
    }

    [Fact]
    public async Task CreateReportAsync_WritesPackageMasksTokenAndUploadsWhenConfigured()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        await store.UpdateSettingsAsync(new AppSettings
        {
            ServerBaseUrl = "https://issues.example.com/",
            SyncClientId = "client-001",
            SyncApiToken = "sensitive-token-6789",
            AiApiKey = "openai-secret-1234",
            IssueReportDirectory = scope.FeedbackRootPath
        });

        var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            HttpResponseFactory.Json("""{"success":true,"issueId":"srv-123","message":"ok"}""")));

        var service = new IssueReportService(
            store,
            (baseUri, _, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var result = await service.CreateReportAsync("发送时焦点丢失");
        var packageJson = await File.ReadAllTextAsync(result.PackagePath);

        Assert.True(result.Success);
        Assert.True(result.UploadAttempted);
        Assert.True(result.UploadSucceeded);
        Assert.Equal("srv-123", result.ServerIssueId);
        Assert.True(File.Exists(result.FilePath));
        Assert.True(File.Exists(result.PackagePath));
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("sensitive-token-6789", packageJson, StringComparison.Ordinal);
        Assert.DoesNotContain("openai-secret-1234", packageJson, StringComparison.Ordinal);
        Assert.Contains("6789", packageJson, StringComparison.Ordinal);
        Assert.Contains("1234", packageJson, StringComparison.Ordinal);
        Assert.Contains("***", packageJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateReportAsync_FallsBackToBuiltInServerWhenConfiguredServerIsUnsupported()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        await store.UpdateSettingsAsync(new AppSettings
        {
            ServerBaseUrl = "ftp://invalid-service",
            IssueReportDirectory = scope.FeedbackRootPath
        });

        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            Assert.StartsWith($"{QuickReplyServiceEndpointDefaults.DefaultRemoteServiceBaseUrl}/api/quickreply/issues/report", request.RequestUri?.ToString(), StringComparison.Ordinal);
            return Task.FromResult(HttpResponseFactory.Json("""{"success":true,"issueId":"srv-ftp-fallback","message":"ok"}"""));
        });

        var service = new IssueReportService(
            store,
            (baseUri, _, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var result = await service.CreateReportAsync("服务地址无效时也要保留本地问题包");

        Assert.True(result.Success);
        Assert.True(result.UploadAttempted);
        Assert.True(result.UploadSucceeded);
        Assert.True(File.Exists(result.FilePath));
        Assert.True(File.Exists(result.PackagePath));
        Assert.Equal("srv-ftp-fallback", result.ServerIssueId);
    }

    [Fact]
    public async Task CreateReportAsync_SkipsUploadWhenBuiltInServiceDoesNotProvideIssueEndpoint()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        await store.UpdateSettingsAsync(new AppSettings
        {
            ServerBaseUrl = string.Empty,
            IssueReportDirectory = scope.FeedbackRootPath
        });

        var service = new IssueReportService(
            store,
            (baseUri, _, _) => new HttpClient(new RecordingHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))))
            {
                BaseAddress = baseUri
            });

        var result = await service.CreateReportAsync("内置服务没有问题上报接口时也要保留本地问题包");

        Assert.True(result.Success);
        Assert.False(result.UploadAttempted);
        Assert.False(result.UploadSucceeded);
        Assert.True(File.Exists(result.FilePath));
        Assert.True(File.Exists(result.PackagePath));
        Assert.Contains("当前内置服务未启用问题上报接口", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateReportAsync_WritesOperationEvent()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        await store.UpdateSettingsAsync(new AppSettings
        {
            IssueReportDirectory = scope.FeedbackRootPath
        });
        await using var loggingService = scope.CreateConversationLoggingService();

        var service = new IssueReportService(
            store,
            (baseUri, _, _) => new HttpClient(new RecordingHttpMessageHandler((_, _) =>
                Task.FromResult(HttpResponseFactory.Json("{}"))))
            {
                BaseAddress = baseUri
            },
            loggingService);

        var result = await service.CreateReportAsync("记录一条本地问题反馈");

        Assert.True(result.Success);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(scope.LogsRootPath, "conversation-logs.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM operation_events
            WHERE operation_name = 'issue_report_create' AND stage = 'completed';
            """;

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RefreshIssueStatusesAsync_UpdatesPatchFieldsFromServerResponse()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        await store.UpdateSettingsAsync(new AppSettings
        {
            ServerBaseUrl = "https://issues.example.com/",
            SyncClientId = "client-001",
            IssueReportDirectory = scope.FeedbackRootPath
        });

        var indexPath = Path.Combine(scope.FeedbackRootPath, "issue-report-index.json");
        await File.WriteAllTextAsync(
            indexPath,
            JsonSerializer.Serialize(
                new[]
                {
                    new IssueReportIndexEntry
                    {
                        IssueId = "local-001",
                        ServerIssueId = "srv-001",
                        CreatedAt = DateTimeOffset.Now.AddMinutes(-5),
                        UserDescription = "sample"
                    }
                }));

        var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            HttpResponseFactory.Json(
                """
                {
                  "success": true,
                  "items": [
                    {
                      "issueId": "srv-001",
                      "serverIssueId": "srv-001",
                      "remoteStatus": "resolved",
                      "analysisStatus": "ready",
                      "patchVersion": "2.1.10",
                      "downloadUrl": "https://issues.example.com/patch.zip",
                      "releaseNotes": "fix",
                      "message": "done"
                    }
                  ],
                  "message": "ok"
                }
                """)));

        var service = new IssueReportService(
            store,
            (baseUri, _, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var refreshResult = await service.RefreshIssueStatusesAsync();
        var entries = await service.LoadRecentIssueEntriesAsync();

        Assert.True(refreshResult.Success);
        Assert.Equal(1, refreshResult.UpdatedCount);
        Assert.Single(entries);
        Assert.Equal("resolved", entries[0].RemoteStatus);
        Assert.Equal("2.1.10", entries[0].PatchVersion);
        Assert.Equal("https://issues.example.com/patch.zip", entries[0].PatchDownloadUrl);
    }

    [Fact]
    public async Task RefreshIssueStatusesAsync_SkipsWhenBuiltInServiceDoesNotProvideIssueStatusEndpoint()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        await store.UpdateSettingsAsync(new AppSettings
        {
            ServerBaseUrl = string.Empty,
            IssueReportDirectory = scope.FeedbackRootPath
        });

        var createdAt = DateTimeOffset.Parse("2026-04-18T12:00:00+08:00", null, System.Globalization.DateTimeStyles.RoundtripKind);
        var feedbackRootPath = scope.FeedbackRootPath;
        Directory.CreateDirectory(feedbackRootPath);
        var indexPath = Path.Combine(feedbackRootPath, "issue-report-index.json");
        await File.WriteAllTextAsync(
            indexPath,
            JsonSerializer.Serialize<IReadOnlyList<IssueReportIndexEntry>>(
            [
                new IssueReportIndexEntry
                {
                    IssueId = "issue-001",
                    CreatedAt = createdAt,
                    FilePath = Path.Combine(feedbackRootPath, "issue-001.md"),
                    PackagePath = Path.Combine(feedbackRootPath, "issue-001.json"),
                    UploadAttempted = true,
                    UploadSucceeded = true,
                    ServerIssueId = "srv-issue-001"
                }
            ]));

        var service = new IssueReportService(
            store,
            (baseUri, _, _) => new HttpClient(new RecordingHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))))
            {
                BaseAddress = baseUri
            });

        var result = await service.RefreshIssueStatusesAsync();

        Assert.True(result.Success);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Contains("已跳过本次自动刷新", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryPendingUploadsAsync_RetriesSavedPackageAndUpdatesIndex()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        await store.UpdateSettingsAsync(new AppSettings
        {
            ServerBaseUrl = "https://issues.example.com/",
            SyncClientId = "client-001",
            IssueReportDirectory = scope.FeedbackRootPath
        });

        var packagePath = Path.Combine(scope.FeedbackRootPath, "issue-package.json");
        await File.WriteAllTextAsync(
            packagePath,
            JsonSerializer.Serialize(new IssueReportPackage
            {
                IssueId = "local-002",
                CreatedAt = DateTimeOffset.Now,
                ClientId = "client-001",
                UserDescription = "发送失败"
            }));

        var indexPath = Path.Combine(scope.FeedbackRootPath, "issue-report-index.json");
        await File.WriteAllTextAsync(
            indexPath,
            JsonSerializer.Serialize(
                new[]
                {
                    new IssueReportIndexEntry
                    {
                        IssueId = "local-002",
                        CreatedAt = DateTimeOffset.Now,
                        UserDescription = "发送失败",
                        PackagePath = packagePath,
                        UploadSucceeded = false
                    }
                }));

        var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            HttpResponseFactory.Json("""{"success":true,"issueId":"srv-222","message":"ok"}""")));

        var service = new IssueReportService(
            store,
            (baseUri, _, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            });

        var retryResult = await service.RetryPendingUploadsAsync();
        var entries = await service.LoadRecentIssueEntriesAsync();

        Assert.True(retryResult.Success);
        Assert.Equal(1, retryResult.RetriedCount);
        Assert.Equal(1, retryResult.SucceededCount);
        Assert.Single(entries);
        Assert.True(entries[0].UploadSucceeded);
        Assert.Equal("srv-222", entries[0].ServerIssueId);
    }

    [Fact]
    public async Task MarkPatchVersionInstalledAsync_UpdatesMatchingIssueEntries()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        await store.UpdateSettingsAsync(new AppSettings
        {
            IssueReportDirectory = scope.FeedbackRootPath
        });

        var indexPath = Path.Combine(scope.FeedbackRootPath, "issue-report-index.json");
        await File.WriteAllTextAsync(
            indexPath,
            JsonSerializer.Serialize(
                new[]
                {
                    new IssueReportIndexEntry
                    {
                        IssueId = "local-003",
                        CreatedAt = DateTimeOffset.Now,
                        UserDescription = "补丁待安装",
                        PatchVersion = "2.1.10"
                    }
                }));

        var service = new IssueReportService(
            store,
            (baseUri, _, _) => new HttpClient(new RecordingHttpMessageHandler((_, _) =>
                Task.FromResult(HttpResponseFactory.Json("{}"))))
            {
                BaseAddress = baseUri
            });

        await service.MarkPatchVersionInstalledAsync("2.1.10");
        var entries = await service.LoadRecentIssueEntriesAsync();

        Assert.Single(entries);
        Assert.Equal("2.1.10", entries[0].InstalledPatchVersion);
    }
}
