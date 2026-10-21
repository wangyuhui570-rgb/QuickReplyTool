using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public readonly record struct IssueReportResult(
    bool Success,
    string FilePath,
    string PackagePath,
    string Message,
    bool UploadAttempted,
    bool UploadSucceeded,
    string UploadMessage,
    string ServerIssueId);

public readonly record struct IssueReportRetryResult(
    bool Success,
    int RetriedCount,
    int SucceededCount,
    string Message);

public readonly record struct IssueReportStatusRefreshResult(
    bool Success,
    int RequestedCount,
    int UpdatedCount,
    string Message);

internal readonly record struct IssueReportUploadAttemptResult(
    bool Attempted,
    bool Success,
    string Message,
    string ServerIssueId);

internal sealed class IssueReportUploadResponse
{
    public bool Success { get; init; }

    public string IssueId { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

public sealed class IssueReportService
{
    private const string IssueReportUploadPath = "/api/quickreply/issues/report";
    private const string IssueReportStatusBatchPath = "/api/quickreply/issues/status/batch";
    private const string IssueReportIndexFileName = "issue-report-index.json";

    private readonly SqliteAppDataStore _dataStore;
    private readonly string _appRootPath;
    private readonly string _logsRootPath;
    private readonly string _conversationLogDatabasePath;
    private readonly Func<Uri, AppSettings, string, HttpClient> _httpClientFactory;
    private readonly ConversationLoggingService? _operationLogger;

    public IssueReportService()
        : this(new SqliteAppDataStore(), CreateHttpClient, null)
    {
    }

    internal IssueReportService(
        SqliteAppDataStore dataStore,
        ConversationLoggingService? operationLogger)
        : this(dataStore, CreateHttpClient, operationLogger)
    {
    }

    internal IssueReportService(
        SqliteAppDataStore dataStore,
        Func<Uri, AppSettings, string, HttpClient> httpClientFactory,
        ConversationLoggingService? operationLogger = null)
    {
        _dataStore = dataStore;
        _appRootPath = _dataStore.DataRootPath;
        _logsRootPath = Path.Combine(_appRootPath, "logs");
        _conversationLogDatabasePath = Path.Combine(_logsRootPath, "conversation-logs.db");
        _httpClientFactory = httpClientFactory;
        _operationLogger = operationLogger;
    }

    public async Task<IssueReportResult> CreateReportAsync(string issueDescription)
    {
        var normalizedDescription = (issueDescription ?? string.Empty).Trim();
        var stopwatch = Stopwatch.StartNew();
        var stage = "validation";
        if (string.IsNullOrWhiteSpace(normalizedDescription))
        {
            return await CompleteCreateReportAsync(
                new IssueReportResult(false, string.Empty, string.Empty, "请先填写遇到的问题。", false, false, string.Empty, string.Empty),
                stage,
                stopwatch).ConfigureAwait(false);
        }

        try
        {
            stage = "load_settings";
            var settings = await _dataStore.LoadSettingsOnlyAsync().ConfigureAwait(false);
            var feedbackRootPath = GetFeedbackRootPath(settings);
            Directory.CreateDirectory(feedbackRootPath);

            var createdAt = DateTimeOffset.Now;
            var issueId = Guid.NewGuid().ToString("N");
            var fileNamePrefix = $"issue-feedback-{createdAt:yyyyMMdd-HHmmss}-{issueId[..8]}";
            var filePath = Path.Combine(feedbackRootPath, $"{fileNamePrefix}.md");
            var packagePath = Path.Combine(feedbackRootPath, $"{fileNamePrefix}.json");

            stage = "build_package";
            var reportPackage = await BuildIssueReportPackageAsync(
                    settings,
                    normalizedDescription,
                    feedbackRootPath,
                    issueId,
                    createdAt)
                .ConfigureAwait(false);

            var markdown = BuildMarkdownReport(reportPackage);
            var json = JsonSerializer.Serialize(reportPackage, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            stage = "write_local_artifacts";
            await File.WriteAllTextAsync(filePath, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)).ConfigureAwait(false);
            await File.WriteAllTextAsync(packagePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)).ConfigureAwait(false);

            stage = "upload";
            var uploadResult = await TryUploadReportAsync(
                    settings,
                    reportPackage,
                    settings.AllowAutomaticIssueReportUpload)
                .ConfigureAwait(false);
            stage = "persist_index";
            await UpsertIssueIndexEntryAsync(
                    feedbackRootPath,
                    new IssueReportIndexEntry
                    {
                        IssueId = reportPackage.IssueId,
                        CreatedAt = reportPackage.CreatedAt,
                        UserDescription = reportPackage.UserDescription,
                        FilePath = filePath,
                        PackagePath = packagePath,
                        UploadAttempted = uploadResult.Attempted,
                        UploadSucceeded = uploadResult.Success,
                        LastUploadAttemptAt = uploadResult.Attempted ? DateTimeOffset.Now : null,
                        UploadMessage = uploadResult.Message,
                        ServerIssueId = uploadResult.ServerIssueId,
                        RemoteStatus = uploadResult.Success ? "reported" : string.Empty
                    })
                .ConfigureAwait(false);
            var message = BuildResultMessage(uploadResult);

            return await CompleteCreateReportAsync(
                new IssueReportResult(
                true,
                filePath,
                packagePath,
                message,
                uploadResult.Attempted,
                uploadResult.Success,
                uploadResult.Message,
                uploadResult.ServerIssueId),
                uploadResult.Attempted && !uploadResult.Success ? "upload" : "completed",
                stopwatch,
                new
                {
                    uploadAttempted = uploadResult.Attempted,
                    uploadSucceeded = uploadResult.Success,
                    serverIssueId = uploadResult.ServerIssueId
                }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await CompleteCreateReportAsync(
                new IssueReportResult(false, string.Empty, string.Empty, $"生成问题反馈文件失败：{ex.Message}", false, false, string.Empty, string.Empty),
                stage,
                stopwatch).ConfigureAwait(false);
        }
    }

    public async Task<string> GetIssueReportDirectoryPathAsync()
    {
        var settings = await _dataStore.LoadSettingsOnlyAsync().ConfigureAwait(false);
        return GetFeedbackRootPath(settings);
    }

    public async Task<IReadOnlyList<IssueReportIndexEntry>> LoadRecentIssueEntriesAsync()
    {
        var settings = await _dataStore.LoadSettingsOnlyAsync().ConfigureAwait(false);
        var feedbackRootPath = GetFeedbackRootPath(settings);
        Directory.CreateDirectory(feedbackRootPath);
        return await LoadIssueIndexAsync(feedbackRootPath).ConfigureAwait(false);
    }

    public async Task<IssueReportStatusRefreshResult> RefreshIssueStatusesAsync()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return new IssueReportStatusRefreshResult(true, 0, 0, "问题状态刷新已移除：当前为本地工具，不再连接服务器。");
    }

    public async Task<IssueReportRetryResult> RetryPendingUploadsAsync()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return new IssueReportRetryResult(true, 0, 0, "问题上传已移除：反馈包仅保存在本地目录。");
    }

    public async Task MarkPatchVersionInstalledAsync(string patchVersion)
    {
        var normalizedPatchVersion = patchVersion?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedPatchVersion))
        {
            return;
        }

        var settings = await _dataStore.LoadSettingsOnlyAsync().ConfigureAwait(false);
        var feedbackRootPath = GetFeedbackRootPath(settings);
        Directory.CreateDirectory(feedbackRootPath);

        var entries = await LoadIssueIndexAsync(feedbackRootPath).ConfigureAwait(false);
        var updatedEntries = entries
            .Select(entry =>
                string.Equals(entry.PatchVersion, normalizedPatchVersion, StringComparison.OrdinalIgnoreCase)
                    ? entry with { InstalledPatchVersion = normalizedPatchVersion }
                    : entry)
            .OrderByDescending(entry => entry.CreatedAt)
            .ToArray();

        await SaveIssueIndexAsync(feedbackRootPath, updatedEntries).ConfigureAwait(false);
    }

    private async Task<IssueReportResult> CompleteCreateReportAsync(
        IssueReportResult result,
        string stage,
        Stopwatch stopwatch,
        object? details = null)
    {
        await TryLogOperationEventAsync(
                "issue_report_create",
                stage,
                result.Success,
                skipped: false,
                stopwatch.ElapsedMilliseconds,
                result.Message,
                details)
            .ConfigureAwait(false);
        return result;
    }

    private async Task<IssueReportStatusRefreshResult> CompleteStatusRefreshAsync(
        IssueReportStatusRefreshResult result,
        string stage,
        Stopwatch stopwatch,
        object? details = null)
    {
        await TryLogOperationEventAsync(
                "issue_report_refresh",
                stage,
                result.Success,
                skipped: false,
                stopwatch.ElapsedMilliseconds,
                result.Message,
                details)
            .ConfigureAwait(false);
        return result;
    }

    private async Task<IssueReportRetryResult> CompleteRetryResultAsync(
        IssueReportRetryResult result,
        string stage,
        Stopwatch stopwatch,
        object? details = null)
    {
        await TryLogOperationEventAsync(
                "issue_report_retry_upload",
                stage,
                result.Success,
                skipped: false,
                stopwatch.ElapsedMilliseconds,
                result.Message,
                details)
            .ConfigureAwait(false);
        return result;
    }

    private Task TryLogOperationEventAsync(
        string operationName,
        string stage,
        bool success,
        bool skipped,
        long durationMs,
        string message,
        object? details = null)
    {
        return _operationLogger?.LogOperationEventAsync(
                   operationName,
                   stage,
                   success,
                   skipped,
                   durationMs,
                   message,
                   details)
               ?? Task.CompletedTask;
    }

    private async Task<IssueReportPackage> BuildIssueReportPackageAsync(
        AppSettings settings,
        string issueDescription,
        string feedbackRootPath,
        string issueId,
        DateTimeOffset createdAt)
    {
        var settingsSnapshot = CaptureSettingsSnapshot(settings);
        var recentConversationEntries = await ReadConversationEntriesAsync(30).ConfigureAwait(false);

        return new IssueReportPackage
        {
            IssueId = issueId,
            CreatedAt = createdAt,
            ClientId = string.IsNullOrWhiteSpace(settings.SyncClientId) ? Environment.MachineName : settings.SyncClientId,
            AppVersion = GetAppVersion(),
            MachineName = Environment.MachineName,
            ProcessPath = Environment.ProcessPath ?? string.Empty,
            ProcessId = Environment.ProcessId,
            UserDescription = issueDescription,
            ServerBaseUrl = settings.ServerBaseUrl,
            SettingsSnapshot = settingsSnapshot,
            Paths = new IssueReportPathSnapshot
            {
                DataRootPath = _appRootPath,
                LogsRootPath = _logsRootPath,
                FeedbackRootPath = feedbackRootPath,
                DatabasePath = _dataStore.DatabasePath,
                ConversationLogDatabasePath = _conversationLogDatabasePath,
                ScreenshotToolSettingsPath = Path.Combine(_appRootPath, "ScreenshotTool", "lite_shot_settings.json")
            },
            Diagnostics = new IssueReportDiagnostics
            {
                ScreenshotToolSettingsText = await ReadTextFileOrNoteAsync(Path.Combine(_appRootPath, "ScreenshotTool", "lite_shot_settings.json")).ConfigureAwait(false),
                StartupLogText = await ReadTextFileOrNoteAsync(Path.Combine(_logsRootPath, "startup", "latest.log")).ConfigureAwait(false),
                ExceptionLogTail = await ReadLatestLinesFromPatternAsync(Path.Combine(_logsRootPath, "exceptions"), "*.jsonl", 120).ConfigureAwait(false),
                SearchOverlayLogTail = await ReadLatestLinesFromPatternAsync(Path.Combine(_logsRootPath, "search-overlay"), "*.jsonl", 160).ConfigureAwait(false),
                LogDirectorySnapshot = BuildDirectorySnapshot(_logsRootPath),
                RecentConversationEntries = recentConversationEntries
            }
        };
    }

    private async Task<IssueReportUploadAttemptResult> TryUploadReportAsync(
        AppSettings settings,
        IssueReportPackage reportPackage,
        bool uploadAllowed)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return new IssueReportUploadAttemptResult(false, false, "已生成本地问题包（云端上传已移除）。", string.Empty);
    }

    private static HttpClient CreateHttpClient(Uri baseUri, AppSettings settings, string clientId)
    {
        var client = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Add("X-QuickReply-ClientId", clientId);
        client.DefaultRequestHeaders.Add("X-QuickReply-AppVersion", AppIdentity.Version);

        if (!string.IsNullOrWhiteSpace(settings.SyncApiToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.SyncApiToken);
        }

        return client;
    }

    private static bool TryBuildBaseUri(string serverBaseUrl, out Uri baseUri)
    {
        return SettingsInputValidator.TryBuildConfiguredServerBaseUri(serverBaseUrl, out baseUri);
    }

    private static bool IsUnsupportedBuiltInIssueEndpoint(AppSettings settings, HttpStatusCode statusCode)
    {
        if (statusCode is not (HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed))
        {
            return false;
        }

        return string.Equals(
            SettingsInputValidator.ResolveOperationalServerBaseUrl(settings.ServerBaseUrl),
            QuickReplyServiceEndpointDefaults.DefaultRemoteServiceBaseUrl,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildResultMessage(IssueReportUploadAttemptResult uploadResult)
    {
        if (!uploadResult.Attempted)
        {
            if (string.Equals(uploadResult.Message, "自动上传未开启，已保留本地问题包。", StringComparison.Ordinal))
            {
                return "问题反馈文件和问题包已生成。本次未自动上传到服务器，后续可手动重试。";
            }

            return string.Equals(uploadResult.Message, "当前内置服务未启用问题上报接口。", StringComparison.Ordinal)
                ? "问题反馈文件和问题包已生成。当前内置服务未启用问题上报接口，已保留本地问题包。"
                : "问题反馈文件和问题包已生成，已保存在本地。";
        }

        return uploadResult.Success
            ? "问题反馈文件和问题包已生成，并已上传到服务器。"
            : $"问题反馈文件和问题包已生成，但上传失败：{uploadResult.Message}。后续可手动重试。";
    }

    internal static IssueReportPackage BuildUploadPackageForTesting(IssueReportPackage reportPackage)
    {
        return BuildUploadPackage(reportPackage);
    }

    private static IssueReportPackage BuildUploadPackage(IssueReportPackage reportPackage)
    {
        return new IssueReportPackage
        {
            IssueId = reportPackage.IssueId,
            CreatedAt = reportPackage.CreatedAt,
            ClientId = reportPackage.ClientId,
            AppVersion = reportPackage.AppVersion,
            MachineName = reportPackage.MachineName,
            ProcessPath = Path.GetFileName(reportPackage.ProcessPath),
            ProcessId = reportPackage.ProcessId,
            UserDescription = SanitizeUploadText(reportPackage.UserDescription, 800),
            ServerBaseUrl = reportPackage.ServerBaseUrl,
            SettingsSnapshot = reportPackage.SettingsSnapshot
                .ToDictionary(
                    pair => pair.Key,
                    pair => SanitizeUploadSettingValue(pair.Value),
                    StringComparer.Ordinal),
            Paths = new IssueReportPathSnapshot
            {
                DataRootPath = string.Empty,
                LogsRootPath = string.Empty,
                FeedbackRootPath = string.Empty,
                DatabasePath = Path.GetFileName(reportPackage.Paths.DatabasePath),
                ConversationLogDatabasePath = Path.GetFileName(reportPackage.Paths.ConversationLogDatabasePath),
                ScreenshotToolSettingsPath = Path.GetFileName(reportPackage.Paths.ScreenshotToolSettingsPath)
            },
            Diagnostics = new IssueReportDiagnostics
            {
                ScreenshotToolSettingsText = SanitizeUploadText(reportPackage.Diagnostics.ScreenshotToolSettingsText, 800),
                StartupLogText = SanitizeUploadText(reportPackage.Diagnostics.StartupLogText, 1000),
                ExceptionLogTail = SanitizeUploadText(reportPackage.Diagnostics.ExceptionLogTail, 1000),
                SearchOverlayLogTail = SanitizeUploadText(reportPackage.Diagnostics.SearchOverlayLogTail, 1000),
                LogDirectorySnapshot = SanitizeDirectorySnapshot(reportPackage.Diagnostics.LogDirectorySnapshot),
                RecentConversationEntries = reportPackage.Diagnostics.RecentConversationEntries
                    .Select(entry => new IssueReportConversationEntry
                    {
                        OccurredAt = entry.OccurredAt,
                        TriggerSource = entry.TriggerSource,
                        ActionMode = entry.ActionMode,
                        PayloadType = entry.PayloadType,
                        ResultSuccess = entry.ResultSuccess,
                        ResultMessage = SanitizeUploadText(entry.ResultMessage, 120),
                        TargetProcessName = entry.TargetProcessName,
                        PhraseTitle = SanitizeUploadText(entry.PhraseTitle, 80),
                        FileDisplayName = Path.GetFileName(entry.FileDisplayName)
                    })
                    .ToArray()
            }
        };
    }

    private static string? SanitizeUploadSettingValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? value
            : SanitizeUploadText(value, 200);
    }

    private static string SanitizeDirectorySnapshot(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lines = value
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var parts = line.Split('|', StringSplitOptions.TrimEntries);
                if (parts.Length < 3)
                {
                    return SanitizeUploadText(line, 180);
                }

                parts[^1] = Path.GetFileName(parts[^1]);
                return string.Join(" | ", parts);
            });
        return string.Join(Environment.NewLine, lines);
    }

    private static string SanitizeUploadText(string value, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = Regex.Replace(normalized, @"https?://\S+", "[url]", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"(?:[A-Za-z]:\\|/)[^\s]+", "[path]");
        normalized = Regex.Replace(normalized, @"\b\d{6,}\b", "[num]");
        normalized = Regex.Replace(normalized, @"\b[A-Za-z0-9_-]{12,}\b", "[id]");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return $"{normalized[..Math.Max(0, maxLength - 3)]}...";
    }

    private static string BuildMarkdownReport(IssueReportPackage reportPackage)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 问题反馈");
        builder.AppendLine();
        builder.AppendLine($"问题编号：{reportPackage.IssueId}");
        builder.AppendLine($"生成时间：{reportPackage.CreatedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"程序版本：{reportPackage.AppVersion}");
        builder.AppendLine($"机器名：{reportPackage.MachineName}");
        builder.AppendLine($"客户端标识：{reportPackage.ClientId}");
        builder.AppendLine($"进程 ID：{reportPackage.ProcessId}");
        builder.AppendLine($"程序路径：{reportPackage.ProcessPath}");
        builder.AppendLine($"服务器地址：{reportPackage.ServerBaseUrl}");
        builder.AppendLine();
        builder.AppendLine("## 用户描述");
        builder.AppendLine();
        builder.AppendLine(reportPackage.UserDescription);
        builder.AppendLine();

        AppendSection(builder, "关键路径", string.Join(Environment.NewLine, new[]
        {
            $"数据目录：{reportPackage.Paths.DataRootPath}",
            $"日志目录：{reportPackage.Paths.LogsRootPath}",
            $"反馈目录：{reportPackage.Paths.FeedbackRootPath}",
            $"数据库：{reportPackage.Paths.DatabasePath}",
            $"使用日志数据库：{reportPackage.Paths.ConversationLogDatabasePath}",
            $"截图设置：{reportPackage.Paths.ScreenshotToolSettingsPath}"
        }));

        AppendSection(builder, "当前设置快照", JsonSerializer.Serialize(reportPackage.SettingsSnapshot, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        AppendSection(builder, "截图工具设置", reportPackage.Diagnostics.ScreenshotToolSettingsText);
        AppendSection(builder, "启动日志（latest.log）", reportPackage.Diagnostics.StartupLogText);
        AppendSection(builder, "最近异常日志", reportPackage.Diagnostics.ExceptionLogTail);
        AppendSection(builder, "最近搜索/发送诊断日志", reportPackage.Diagnostics.SearchOverlayLogTail);
        AppendSection(builder, "最近使用记录摘要", FormatConversationEntries(reportPackage.Diagnostics.RecentConversationEntries));
        AppendSection(builder, "日志目录概览", reportPackage.Diagnostics.LogDirectorySnapshot);
        AppendSection(builder, "结构化问题包文件", "同目录下已同时生成 .json，可直接给服务器或自动分析任务使用。");
        return builder.ToString();
    }

    private static string GetAppVersion()
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? string.Empty).ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? "未知" : version;
        }
        catch
        {
            return "未知";
        }
    }

    private string GetFeedbackRootPath(AppSettings settings)
    {
        var configuredPath = settings.IssueReportDirectory?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(_appRootPath, "feedback")
            : configuredPath;
    }

    private static void AppendSection(StringBuilder builder, string title, string content)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(content) ? "无可用数据。" : content.TrimEnd());
        builder.AppendLine();
    }

    private static Dictionary<string, string?> CaptureSettingsSnapshot(AppSettings settings)
    {
        return typeof(AppSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(
                property => property.Name,
                property => FormatSettingValue(property.Name, ReadSettingValue(property, settings)),
                StringComparer.Ordinal);
    }

    private static object? ReadSettingValue(PropertyInfo property, AppSettings settings)
    {
        try
        {
            return property.GetValue(settings);
        }
        catch
        {
            return null;
        }
    }

    private static string? FormatSettingValue(string propertyName, object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (propertyName.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("Key", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("Secret", StringComparison.OrdinalIgnoreCase))
        {
            var raw = value.ToString()?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(raw)
                ? string.Empty
                : $"{new string('*', Math.Max(0, raw.Length - 4))}{raw[^Math.Min(4, raw.Length)..]}";
        }

        return value switch
        {
            DateTimeOffset offset => offset.ToString("O"),
            _ => value.ToString()
        };
    }

    private static async Task<string> ReadTextFileOrNoteAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return $"文件不存在：{filePath}";
        }

        try
        {
            return await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"读取失败：{filePath}{Environment.NewLine}{ex.Message}";
        }
    }

    private static async Task<string> ReadLatestLinesFromPatternAsync(string directoryPath, string pattern, int maxLines)
    {
        if (!Directory.Exists(directoryPath))
        {
            return $"目录不存在：{directoryPath}";
        }

        var files = new DirectoryInfo(directoryPath)
            .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(5)
            .ToArray();

        if (files.Length == 0)
        {
            return "没有找到日志文件。";
        }

        var collected = new List<string>(maxLines + 8);
        foreach (var file in files)
        {
            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file.FullName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                collected.Add($"### {file.Name}");
                collected.Add($"读取失败：{ex.Message}");
                continue;
            }

            collected.Add($"### {file.Name}");
            foreach (var line in lines.TakeLast(Math.Max(1, maxLines / Math.Max(1, files.Length))))
            {
                collected.Add(line);
            }
        }

        return string.Join(Environment.NewLine, collected);
    }

    private async Task<IReadOnlyList<IssueReportConversationEntry>> ReadConversationEntriesAsync(int maxRows)
    {
        if (!File.Exists(_conversationLogDatabasePath))
        {
            return [];
        }

        try
        {
            await using var connection = new SqliteConnection($"Data Source={_conversationLogDatabasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT occurred_at,
                       trigger_source,
                       action_mode,
                       payload_type,
                       result_success,
                       result_message,
                       target_process_name,
                       phrase_title,
                       file_display_name
                FROM conversation_logs
                ORDER BY occurred_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", maxRows);

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            var items = new List<IssueReportConversationEntry>();
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                items.Add(new IssueReportConversationEntry
                {
                    OccurredAt = reader.GetString(0),
                    TriggerSource = reader.GetString(1),
                    ActionMode = reader.GetString(2),
                    PayloadType = reader.GetString(3),
                    ResultSuccess = reader.GetInt64(4) == 1,
                    ResultMessage = reader.GetString(5),
                    TargetProcessName = reader.GetString(6),
                    PhraseTitle = reader.GetString(7),
                    FileDisplayName = reader.GetString(8)
                });
            }

            return items;
        }
        catch
        {
            return [];
        }
    }

    private static string FormatConversationEntries(IReadOnlyList<IssueReportConversationEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "没有最近使用记录。";
        }

        return string.Join(
            Environment.NewLine,
            entries.Select(entry =>
            {
                var payloadDisplay = !string.IsNullOrWhiteSpace(entry.PhraseTitle)
                    ? entry.PhraseTitle
                    : !string.IsNullOrWhiteSpace(entry.FileDisplayName)
                        ? entry.FileDisplayName
                        : "-";

                var resultText = entry.ResultSuccess ? "success" : "failed";
                return $"{entry.OccurredAt} | {resultText} | {entry.TriggerSource} | {entry.ActionMode} | {entry.PayloadType} | {entry.TargetProcessName} | {payloadDisplay} | {entry.ResultMessage}";
            }));
    }

    private static string BuildDirectorySnapshot(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return $"目录不存在：{directoryPath}";
        }

        var lines = new List<string>();
        foreach (var file in new DirectoryInfo(directoryPath)
                     .EnumerateFiles("*", SearchOption.AllDirectories)
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Take(30))
        {
            lines.Add($"{file.LastWriteTime:yyyy-MM-dd HH:mm:ss} | {file.Length,10} | {file.FullName}");
        }

        return lines.Count == 0 ? "目录里没有文件。" : string.Join(Environment.NewLine, lines);
    }

    private static async Task UpsertIssueIndexEntryAsync(string feedbackRootPath, IssueReportIndexEntry entry)
    {
        var entries = await LoadIssueIndexAsync(feedbackRootPath).ConfigureAwait(false);
        var updatedEntries = entries
            .Where(existing => !string.Equals(existing.IssueId, entry.IssueId, StringComparison.Ordinal))
            .Prepend(entry)
            .OrderByDescending(existing => existing.CreatedAt)
            .Take(200)
            .ToArray();

        await SaveIssueIndexAsync(feedbackRootPath, updatedEntries).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<IssueReportIndexEntry>> LoadIssueIndexAsync(string feedbackRootPath)
    {
        var indexPath = Path.Combine(feedbackRootPath, IssueReportIndexFileName);
        if (!File.Exists(indexPath))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(indexPath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<IReadOnlyList<IssueReportIndexEntry>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static async Task SaveIssueIndexAsync(string feedbackRootPath, IReadOnlyList<IssueReportIndexEntry> entries)
    {
        Directory.CreateDirectory(feedbackRootPath);
        var indexPath = Path.Combine(feedbackRootPath, IssueReportIndexFileName);
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(indexPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)).ConfigureAwait(false);
    }
}
