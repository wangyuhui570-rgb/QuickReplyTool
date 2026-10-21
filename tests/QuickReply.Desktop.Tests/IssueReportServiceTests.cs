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
    public async Task CreateReportAsync_WritesPackageLocallyWithoutUpload()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        await store.UpdateSettingsAsync(new AppSettings
        {
            ServerBaseUrl = "https://issues.example.com/",
            SyncClientId = "client-001",
            SyncApiToken = "sensitive-token-6789",
            AllowAutomaticIssueReportUpload = true,
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
        Assert.False(result.UploadAttempted);
        Assert.False(result.UploadSucceeded);
        Assert.Equal(string.Empty, result.ServerIssueId);
        Assert.Equal("问题反馈文件和问题包已生成，已保存在本地。", result.Message);
        Assert.True(File.Exists(result.FilePath));
        Assert.True(File.Exists(result.PackagePath));
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("sensitive-token-6789", packageJson, StringComparison.Ordinal);
        Assert.Contains("6789", packageJson, StringComparison.Ordinal);
        Assert.Contains("***", packageJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAndRetry_AreNoOpsForLocalOnlyTool()
    {
        using var scope = new TestAppDataScope();
        var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(HttpResponseFactory.Json("{}")));
        var service = new IssueReportService(
            scope.CreateDataStore(),
            (baseUri, _, _) => new HttpClient(handler) { BaseAddress = baseUri });

        var refresh = await service.RefreshIssueStatusesAsync();
        var retry = await service.RetryPendingUploadsAsync();

        Assert.Equal("问题状态刷新已移除：当前为本地工具，不再连接服务器。", refresh.Message);
        Assert.Equal("问题上传已移除：反馈包仅保存在本地目录。", retry.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void BuildUploadPackageForTesting_StripsPathsAndSanitizesDiagnostics()
    {
        var uploadPackage = IssueReportService.BuildUploadPackageForTesting(new IssueReportPackage
        {
            IssueId = "issue-001",
            CreatedAt = DateTimeOffset.Parse("2026-04-21T10:00:00+08:00", null, System.Globalization.DateTimeStyles.RoundtripKind),
            ClientId = "client-001",
            AppVersion = "1.0.0",
            MachineName = "mac-mini",
            ProcessPath = "/Applications/QuickReplyTool/QuickReplyTool.exe",
            ProcessId = 1234,
            UserDescription = "打开 https://example.com/order/123456789 时失败，日志在 C:\\secret\\logs\\latest.log",
            ServerBaseUrl = "https://issues.example.com/",
            SettingsSnapshot = new Dictionary<string, string?>
            {
                ["SyncApiToken"] = "***6789"
            },
            Paths = new IssueReportPathSnapshot
            {
                DataRootPath = "/Users/yuhui/Library/Application Support/QuickReplyTool",
                LogsRootPath = "/Users/yuhui/Library/Application Support/QuickReplyTool/logs",
                FeedbackRootPath = "/Users/yuhui/Library/Application Support/QuickReplyTool/feedback",
                DatabasePath = "/Users/yuhui/Library/Application Support/QuickReplyTool/data.db",
                ConversationLogDatabasePath = "/Users/yuhui/Library/Application Support/QuickReplyTool/conversation-logs.db",
                ScreenshotToolSettingsPath = "/Users/yuhui/Library/Application Support/QuickReplyTool/ScreenshotTool/lite_shot_settings.json"
            },
            Diagnostics = new IssueReportDiagnostics
            {
                StartupLogText = "请求 https://example.com/order/123456789，文件 /tmp/test.log",
                LogDirectorySnapshot = "2026-04-21 10:00:00 | 100 | /Users/yuhui/Library/Application Support/QuickReplyTool/logs/startup/latest.log"
            }
        });

        Assert.Equal("QuickReplyTool.exe", uploadPackage.ProcessPath);
        Assert.Equal(string.Empty, uploadPackage.Paths.DataRootPath);
        Assert.Equal("data.db", uploadPackage.Paths.DatabasePath);
        Assert.Contains("[url]", uploadPackage.UserDescription, StringComparison.Ordinal);
        Assert.Contains("[path]", uploadPackage.Diagnostics.StartupLogText, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/yuhui/Library/Application Support", uploadPackage.Diagnostics.LogDirectorySnapshot, StringComparison.Ordinal);
        Assert.Contains("latest.log", uploadPackage.Diagnostics.LogDirectorySnapshot, StringComparison.Ordinal);
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
