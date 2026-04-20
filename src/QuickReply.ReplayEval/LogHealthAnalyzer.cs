using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QuickReply.Desktop.Services;

internal sealed class LogHealthAnalyzer
{
    private const int MinimumStrictSampleCount = 50;
    private const double FailureRateThreshold = 0.05;
    private const double LowConfidenceRateThreshold = 0.95;
    private const double SuspiciousCaptureRateThreshold = 0.60;
    private const int SearchOverlayInstrumentationThreshold = 20;

    public async Task<LogHealthReport> AnalyzeAsync(string logRootPath)
    {
        if (string.IsNullOrWhiteSpace(logRootPath))
        {
            throw new ArgumentException("日志目录不能为空。", nameof(logRootPath));
        }

        var normalizedRootPath = Path.GetFullPath(logRootPath);
        if (!Directory.Exists(normalizedRootPath))
        {
            throw new DirectoryNotFoundException($"未找到日志目录：{normalizedRootPath}");
        }

        var databasePath = Path.Combine(normalizedRootPath, "conversation-logs.db");
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException($"未找到日志数据库：{databasePath}", databasePath);
        }

        var report = new LogHealthReport
        {
            LogRootPath = normalizedRootPath,
            DatabasePath = databasePath
        };

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);

        report.TotalConversationLogs = await ExecuteCountAsync(connection, "conversation_logs").ConfigureAwait(false);
        report.FailedConversationLogs = await ExecuteScalarAsync(
            connection,
            "SELECT COUNT(*) FROM conversation_logs WHERE ifnull(result_success, 0) = 0;").ConfigureAwait(false);
        report.SearchOverlayActionCount = await ExecuteScalarAsync(
            connection,
            "SELECT COUNT(*) FROM conversation_logs WHERE trigger_source LIKE 'search_overlay_%';").ConfigureAwait(false);
        report.RecommendationEventCount = await ExecuteCountAsync(connection, "recommendation_events").ConfigureAwait(false);
        report.RecognitionCorrectionCount = await ExecuteCountAsync(connection, "recognition_corrections").ConfigureAwait(false);
        report.OperationFunnels = await LoadOperationFunnelsAsync(connection).ConfigureAwait(false);
        report.AiDraftHealth = await LoadAiDraftHealthAsync(connection).ConfigureAwait(false);
        report.LowConfidenceConversationLogs = await ExecuteScalarAsync(
            connection,
            "SELECT COUNT(*) FROM conversation_logs WHERE lower(ifnull(recognition_status, '')) = 'low_confidence';").ConfigureAwait(false);
        report.TopFailureMessages = await LoadTopFailureMessagesAsync(connection).ConfigureAwait(false);
        report.AppVersionCounts = await LoadTopCountsAsync(
            connection,
            """
            SELECT coalesce(app_version, '') AS key_text, COUNT(*) AS item_count
            FROM conversation_logs
            GROUP BY coalesce(app_version, '')
            ORDER BY item_count DESC, key_text ASC
            LIMIT 8;
            """).ConfigureAwait(false);
        report.HostSendHealth = await LoadHostSendHealthAsync(connection, normalizedRootPath).ConfigureAwait(false);

        report.SuspiciousCaptureConversationLogs = await CountSuspiciousCapturesAsync(connection).ConfigureAwait(false);
        var exceptionSummary = await LoadExceptionSummaryAsync(normalizedRootPath).ConfigureAwait(false);
        report.TotalExceptionCount = exceptionSummary.TotalExceptionCount;
        report.TerminatingExceptionCount = exceptionSummary.TerminatingExceptionCount;
        report.RecentExceptions = exceptionSummary.RecentExceptions;

        EvaluateIssues(report);
        return report;
    }

    private static void EvaluateIssues(LogHealthReport report)
    {
        if (report.TerminatingExceptionCount > 0)
        {
            report.Issues.Add(new LogHealthIssue(
                "error",
                "terminating_exception",
                $"发现 {report.TerminatingExceptionCount} 条导致进程终止的异常日志。"));
        }

        if (report.TotalConversationLogs >= MinimumStrictSampleCount &&
            report.FailureRate > FailureRateThreshold)
        {
            report.Issues.Add(new LogHealthIssue(
                "error",
                "high_failure_rate",
                $"发送失败率达到 {report.FailureRate:P1}，超过阈值 {FailureRateThreshold:P0}。"));
        }

        if (report.TotalConversationLogs >= MinimumStrictSampleCount &&
            report.LowConfidenceRate >= LowConfidenceRateThreshold &&
            report.SuspiciousCaptureRate >= SuspiciousCaptureRateThreshold)
        {
            report.Issues.Add(new LogHealthIssue(
                "error",
                "capture_regression",
                $"识别低置信度占比 {report.LowConfidenceRate:P1}，且可疑抓取内容占比 {report.SuspiciousCaptureRate:P1}，疑似没有抓到真正的聊天正文。"));
        }

        if (report.SearchOverlayActionCount >= SearchOverlayInstrumentationThreshold &&
            report.RecommendationEventCount == 0)
        {
            report.Issues.Add(new LogHealthIssue(
                "error",
                "missing_recommendation_events",
                $"已记录 {report.SearchOverlayActionCount} 条搜索面板动作，但 recommendation_events 仍为 0，推荐闭环打点可能缺失。"));
        }

        if (report.LowConfidenceConversationLogs == report.TotalConversationLogs &&
            report.TotalConversationLogs >= MinimumStrictSampleCount &&
            report.RecognitionCorrectionCount == 0)
        {
            report.Issues.Add(new LogHealthIssue(
                "warning",
                "missing_recognition_feedback",
                "全部历史样本都是 low_confidence，且没有任何识别纠正记录，后续离线回放和规则优化会缺少有效反馈样本。"));
        }

        foreach (var operation in report.OperationFunnels.Where(entry => entry.AttemptCount >= 3 && entry.FailureRate >= 0.30))
        {
            report.Issues.Add(new LogHealthIssue(
                "warning",
                $"operation_funnel_{operation.OperationName}",
                $"{operation.OperationName} 失败率达到 {operation.FailureRate:P1}，主要掉在 {operation.TopFailureStage}。"));
        }

        foreach (var aiDraft in report.AiDraftHealth.Where(entry => entry.ProviderFailedCount >= 3 && entry.ProviderFailedRate >= 0.50))
        {
            report.Issues.Add(new LogHealthIssue(
                "warning",
                $"ai_draft_provider_{aiDraft.ProviderType}",
                $"AI 草稿 provider {aiDraft.DisplayName} 失败率达到 {aiDraft.ProviderFailedRate:P1}，平均耗时 {aiDraft.AverageLatencyMs.ToString("0.0", CultureInfo.InvariantCulture)} ms。"));
        }
    }

    private static async Task<long> ExecuteCountAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        var exists = Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
        if (!exists)
        {
            return 0;
        }

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt64(await countCommand.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<long> ExecuteScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<LogHealthCountEntry>> LoadTopFailureMessagesAsync(SqliteConnection connection)
    {
        return await LoadTopCountsAsync(
            connection,
            """
            SELECT coalesce(result_message, '') AS key_text, COUNT(*) AS item_count
            FROM conversation_logs
            WHERE ifnull(result_success, 0) = 0
            GROUP BY coalesce(result_message, '')
            ORDER BY item_count DESC, key_text ASC
            LIMIT 8;
            """).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<LogHealthCountEntry>> LoadTopCountsAsync(SqliteConnection connection, string sql)
    {
        var results = new List<LogHealthCountEntry>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var value = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            results.Add(new LogHealthCountEntry(value, reader.GetInt64(1)));
        }

        return results;
    }

    private static async Task<IReadOnlyList<LogHealthHostSendEntry>> LoadHostSendHealthAsync(
        SqliteConnection connection,
        string logRootPath)
    {
        var buckets = new Dictionary<string, MutableHostSendHealth>(StringComparer.OrdinalIgnoreCase);
        var hasStructuredRetryMetrics = false;
        var conversationLogColumns = await LoadTableColumnsAsync(connection, "conversation_logs").ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            var composeHostProfileSql = conversationLogColumns.Contains("compose_host_profile")
                ? "ifnull(compose_host_profile, '')"
                : "''";
            var composeRetryTriggeredSql = conversationLogColumns.Contains("compose_retry_triggered")
                ? "ifnull(compose_retry_triggered, 0)"
                : "0";
            var composeRetryOutcomeSql = conversationLogColumns.Contains("compose_retry_outcome")
                ? "ifnull(compose_retry_outcome, '')"
                : "''";
            var composeFailureCategorySql = conversationLogColumns.Contains("compose_failure_category")
                ? "ifnull(compose_failure_category, '')"
                : "''";
            command.CommandText = $"""
                SELECT ifnull(target_window_title, ''),
                       ifnull(target_window_class_name, ''),
                       ifnull(target_process_name, ''),
                       ifnull(result_success, 0),
                       ifnull(result_message, ''),
                       {composeHostProfileSql},
                       {composeRetryTriggeredSql},
                       {composeRetryOutcomeSql},
                       {composeFailureCategorySql}
                FROM conversation_logs;
                """;

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var profile = SendHostProfileResolver.Resolve(new WindowDockTargetInfo
                {
                    Handle = IntPtr.Zero,
                    Title = reader.GetString(0),
                    WindowClassName = reader.GetString(1),
                    ProcessName = reader.GetString(2),
                    ProcessPath = string.Empty,
                    Left = 0,
                    Top = 0,
                    Width = 0,
                    Height = 0
                });

                var persistedHostProfile = reader.GetString(5);
                var bucket = GetOrAddBucket(
                    buckets,
                    string.IsNullOrWhiteSpace(persistedHostProfile)
                        ? profile.Name
                        : persistedHostProfile);
                bucket.AttemptCount++;

                var success = reader.GetInt64(3) != 0;
                var resultMessage = reader.GetString(4);
                var failureCategory = reader.GetString(8);
                var retryTriggered = reader.GetInt64(6) != 0;
                var retryOutcome = reader.GetString(7);
                if (retryTriggered)
                {
                    hasStructuredRetryMetrics = true;
                    bucket.RetryTriggeredCount++;
                    if (string.Equals(retryOutcome, "recovered", StringComparison.OrdinalIgnoreCase))
                    {
                        bucket.RetryRecoveredCount++;
                    }
                }

                if (!success)
                {
                    bucket.FailureCount++;
                    failureCategory = string.IsNullOrWhiteSpace(failureCategory)
                        ? ClassifyFailureCategory(resultMessage)
                        : failureCategory;
                    if (string.Equals(failureCategory, "focus_lost", StringComparison.Ordinal))
                    {
                        bucket.FocusLossFailureCount++;
                    }

                    if (string.Equals(failureCategory, "clipboard", StringComparison.Ordinal))
                    {
                        bucket.ClipboardFailureCount++;
                    }
                }
            }
        }

        if (!hasStructuredRetryMetrics)
        {
            await MergeRetryMetricsAsync(logRootPath, buckets).ConfigureAwait(false);
        }

        return buckets.Values
            .OrderByDescending(entry => entry.AttemptCount)
            .ThenBy(entry => entry.HostProfile, StringComparer.Ordinal)
            .Select(entry => entry.ToImmutable())
            .ToArray();
    }

    private static async Task<IReadOnlyList<LogHealthOperationEntry>> LoadOperationFunnelsAsync(SqliteConnection connection)
    {
        var tables = await LoadTableColumnsAsync(connection, "operation_events").ConfigureAwait(false);
        if (tables.Count == 0)
        {
            return [];
        }

        var entries = new List<LogHealthOperationEntry>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ifnull(operation_name, '') AS operation_name,
                   COUNT(*) AS attempt_count,
                   SUM(CASE WHEN ifnull(success, 0) <> 0 THEN 1 ELSE 0 END) AS success_count,
                   SUM(CASE WHEN ifnull(success, 0) = 0 AND ifnull(skipped, 0) = 0 THEN 1 ELSE 0 END) AS failure_count,
                   SUM(CASE WHEN ifnull(skipped, 0) <> 0 THEN 1 ELSE 0 END) AS skipped_count,
                   AVG(CASE WHEN ifnull(duration_ms, 0) < 0 THEN 0 ELSE ifnull(duration_ms, 0) END) AS avg_duration_ms
            FROM operation_events
            GROUP BY ifnull(operation_name, '')
            ORDER BY attempt_count DESC, operation_name ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var operationName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var topFailure = await LoadTopOperationFailureAsync(connection, operationName).ConfigureAwait(false);
            entries.Add(new LogHealthOperationEntry(
                operationName,
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                topFailure.Stage,
                topFailure.Message));
        }

        return entries;
    }

    private static async Task<IReadOnlyList<LogHealthAiDraftEntry>> LoadAiDraftHealthAsync(SqliteConnection connection)
    {
        var columns = await LoadTableColumnsAsync(connection, "recommendation_events").ConfigureAwait(false);
        if (!columns.Contains("recommendation_kind"))
        {
            return [];
        }

        var entries = new List<LogHealthAiDraftEntry>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ifnull(provider_type, '') AS provider_type,
                   ifnull(model_name, '') AS model_name,
                   SUM(CASE WHEN ifnull(event_type, '') = 'shown' THEN 1 ELSE 0 END) AS shown_count,
                   SUM(CASE WHEN ifnull(event_type, '') = 'input' THEN 1 ELSE 0 END) AS input_count,
                   SUM(CASE WHEN ifnull(event_type, '') = 'sent' THEN 1 ELSE 0 END) AS sent_count,
                   SUM(CASE WHEN ifnull(event_type, '') = 'dismissed' THEN 1 ELSE 0 END) AS dismissed_count,
                   SUM(CASE WHEN ifnull(event_type, '') = 'provider_failed' THEN 1 ELSE 0 END) AS provider_failed_count,
                   SUM(CASE WHEN ifnull(event_type, '') = 'context_skipped' THEN 1 ELSE 0 END) AS context_skipped_count,
                   AVG(CASE WHEN ifnull(latency_ms, 0) > 0 THEN CAST(latency_ms AS REAL) END) AS average_latency_ms
            FROM recommendation_events
            WHERE ifnull(recommendation_kind, '') = 'ai_draft'
            GROUP BY ifnull(provider_type, ''), ifnull(model_name, '')
            ORDER BY shown_count DESC, provider_type ASC, model_name ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            entries.Add(new LogHealthAiDraftEntry(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.IsDBNull(8) ? 0d : reader.GetDouble(8)));
        }

        return entries;
    }

    private static async Task<(string Stage, string Message)> LoadTopOperationFailureAsync(SqliteConnection connection, string operationName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ifnull(stage, '') AS stage_name,
                   ifnull(message, '') AS message_text,
                   COUNT(*) AS item_count
            FROM operation_events
            WHERE ifnull(operation_name, '') = $operationName
              AND ifnull(success, 0) = 0
              AND ifnull(skipped, 0) = 0
            GROUP BY ifnull(stage, ''), ifnull(message, '')
            ORDER BY item_count DESC, stage_name ASC, message_text ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$operationName", operationName);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            return (reader.IsDBNull(0) ? string.Empty : reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
        }

        return ("-", string.Empty);
    }

    private static async Task<HashSet<string>> LoadTableColumnsAsync(SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<long> CountSuspiciousCapturesAsync(SqliteConnection connection)
    {
        var suspiciousCount = 0L;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ifnull(captured_text, ''),
                   ifnull(target_window_title, ''),
                   ifnull(target_window_class_name, ''),
                   ifnull(target_process_name, '')
            FROM conversation_logs
            WHERE ifnull(captured_text, '') <> '';
            """;

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var capturedText = reader.GetString(0);
            var windowTitle = reader.GetString(1);
            var windowClass = reader.GetString(2);
            var processName = reader.GetString(3);
            if (IsSuspiciousCapture(capturedText, windowTitle, windowClass, processName))
            {
                suspiciousCount++;
            }
        }

        return suspiciousCount;
    }

    private static bool IsSuspiciousCapture(string capturedText, string windowTitle, string windowClass, string processName)
    {
        if (string.IsNullOrWhiteSpace(capturedText))
        {
            return false;
        }

        var normalizedText = capturedText
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (normalizedText.Length == 0)
        {
            return false;
        }

        if (normalizedText.Contains("Chrome Legacy Window", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(windowClass) &&
            normalizedText.Contains(windowClass, StringComparison.OrdinalIgnoreCase) &&
            normalizedText.Length <= Math.Max(windowClass.Length + 40, 80))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(processName) &&
            normalizedText.Contains(processName, StringComparison.OrdinalIgnoreCase) &&
            normalizedText.Length <= Math.Max(processName.Length + 40, 80))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(windowTitle) &&
               normalizedText.StartsWith(windowTitle, StringComparison.OrdinalIgnoreCase) &&
               normalizedText.Length <= Math.Max(windowTitle.Length + 48, 96);
    }

    private static async Task<ExceptionSummary> LoadExceptionSummaryAsync(string logRootPath)
    {
        var exceptionDirectory = Path.Combine(logRootPath, "exceptions");
        if (!Directory.Exists(exceptionDirectory))
        {
            return new ExceptionSummary(0, 0, []);
        }

        var totalCount = 0;
        var terminatingCount = 0;
        var recentExceptions = new List<RecentExceptionEntry>();

        foreach (var filePath in Directory.GetFiles(exceptionDirectory, "*.jsonl", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var line in await File.ReadAllLinesAsync(filePath).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                totalCount++;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var isTerminating = root.TryGetProperty("IsTerminating", out var isTerminatingElement) && isTerminatingElement.ValueKind == JsonValueKind.True;
                    if (isTerminating)
                    {
                        terminatingCount++;
                    }

                    if (recentExceptions.Count < 8)
                    {
                        recentExceptions.Add(new RecentExceptionEntry(
                            root.TryGetProperty("OccurredAt", out var occurredAtElement) ? occurredAtElement.GetString() ?? string.Empty : string.Empty,
                            root.TryGetProperty("Source", out var sourceElement) ? sourceElement.GetString() ?? string.Empty : string.Empty,
                            root.TryGetProperty("ExceptionType", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty,
                            root.TryGetProperty("Message", out var messageElement) ? messageElement.GetString() ?? string.Empty : string.Empty,
                            isTerminating));
                    }
                }
                catch
                {
                    if (recentExceptions.Count < 8)
                    {
                        recentExceptions.Add(new RecentExceptionEntry(string.Empty, "exception_log_parse_failed", "JsonException", "异常日志文件里存在无法解析的 JSON 行。", false));
                    }
                }
            }
        }

        return new ExceptionSummary(totalCount, terminatingCount, recentExceptions);
    }

    private static async Task MergeRetryMetricsAsync(
        string logRootPath,
        IDictionary<string, MutableHostSendHealth> buckets)
    {
        var diagnosticsDirectory = Path.Combine(logRootPath, "search-overlay");
        if (!Directory.Exists(diagnosticsDirectory))
        {
            return;
        }

        foreach (var filePath in Directory.GetFiles(diagnosticsDirectory, "*.jsonl", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var line in await File.ReadAllLinesAsync(filePath).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var source = root.TryGetProperty("source", out var sourceElement)
                        ? sourceElement.GetString() ?? string.Empty
                        : string.Empty;
                    var eventName = root.TryGetProperty("event", out var eventElement)
                        ? eventElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (!string.Equals(source, "message_compose", StringComparison.Ordinal) ||
                        !string.Equals(eventName, "compose_operation_completed", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var hostProfile = root.TryGetProperty("hostProfile", out var hostProfileElement)
                        ? hostProfileElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(hostProfile))
                    {
                        hostProfile = SendHostProfile.GenericDesktopInput.Name;
                    }

                    var bucket = GetOrAddBucket(buckets, hostProfile);
                    var retryTriggered = root.TryGetProperty("retryTriggered", out var retryTriggeredElement) &&
                                         retryTriggeredElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                         retryTriggeredElement.GetBoolean();
                    if (!retryTriggered)
                    {
                        continue;
                    }

                    bucket.RetryTriggeredCount++;
                    var retryOutcome = root.TryGetProperty("retryOutcome", out var retryOutcomeElement)
                        ? retryOutcomeElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (string.Equals(retryOutcome, "recovered", StringComparison.OrdinalIgnoreCase))
                    {
                        bucket.RetryRecoveredCount++;
                    }
                }
                catch
                {
                }
            }
        }
    }

    private static MutableHostSendHealth GetOrAddBucket(
        IDictionary<string, MutableHostSendHealth> buckets,
        string hostProfile)
    {
        if (buckets.TryGetValue(hostProfile, out var existing))
        {
            return existing;
        }

        var created = new MutableHostSendHealth(hostProfile);
        buckets[hostProfile] = created;
        return created;
    }

    private static string ClassifyFailureCategory(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "unknown";
        }

        if (message.Contains("未能稳定切回目标输入窗口", StringComparison.Ordinal))
        {
            return "focus_lost";
        }

        if (message.Contains("焦点已丢失", StringComparison.Ordinal) ||
            message.Contains("先点一下聊天输入框", StringComparison.Ordinal))
        {
            return "focus_lost";
        }

        if (message.Contains("剪贴板", StringComparison.Ordinal) ||
            message.Contains("OpenClipboard", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("CLIPBRD", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("超时", StringComparison.Ordinal))
        {
            return "clipboard";
        }

        return "other";
    }

    private sealed record ExceptionSummary(
        int TotalExceptionCount,
        int TerminatingExceptionCount,
        IReadOnlyList<RecentExceptionEntry> RecentExceptions);

    private sealed class MutableHostSendHealth
    {
        public MutableHostSendHealth(string hostProfile)
        {
            HostProfile = hostProfile;
        }

        public string HostProfile { get; }

        public long AttemptCount { get; set; }

        public long FailureCount { get; set; }

        public long FocusLossFailureCount { get; set; }

        public long ClipboardFailureCount { get; set; }

        public long RetryTriggeredCount { get; set; }

        public long RetryRecoveredCount { get; set; }

        public LogHealthHostSendEntry ToImmutable()
        {
            return new LogHealthHostSendEntry(
                HostProfile,
                AttemptCount,
                FailureCount,
                FocusLossFailureCount,
                ClipboardFailureCount,
                RetryTriggeredCount,
                RetryRecoveredCount);
        }
    }
}

internal sealed class LogHealthReport
{
    public string LogRootPath { get; init; } = string.Empty;

    public string DatabasePath { get; init; } = string.Empty;

    public long TotalConversationLogs { get; set; }

    public long FailedConversationLogs { get; set; }

    public long LowConfidenceConversationLogs { get; set; }

    public long SuspiciousCaptureConversationLogs { get; set; }

    public long SearchOverlayActionCount { get; set; }

    public long RecommendationEventCount { get; set; }

    public long RecognitionCorrectionCount { get; set; }

    public int TotalExceptionCount { get; set; }

    public int TerminatingExceptionCount { get; set; }

    public IReadOnlyList<LogHealthCountEntry> TopFailureMessages { get; set; } = [];

    public IReadOnlyList<LogHealthCountEntry> AppVersionCounts { get; set; } = [];

    public IReadOnlyList<LogHealthHostSendEntry> HostSendHealth { get; set; } = [];

    public IReadOnlyList<LogHealthOperationEntry> OperationFunnels { get; set; } = [];

    public IReadOnlyList<LogHealthAiDraftEntry> AiDraftHealth { get; set; } = [];

    public IReadOnlyList<RecentExceptionEntry> RecentExceptions { get; set; } = [];

    public List<LogHealthIssue> Issues { get; } = [];

    public double FailureRate => TotalConversationLogs == 0 ? 0 : (double)FailedConversationLogs / TotalConversationLogs;

    public double LowConfidenceRate => TotalConversationLogs == 0 ? 0 : (double)LowConfidenceConversationLogs / TotalConversationLogs;

    public double SuspiciousCaptureRate => TotalConversationLogs == 0 ? 0 : (double)SuspiciousCaptureConversationLogs / TotalConversationLogs;

    public bool HasBlockingIssues => Issues.Any(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
}

internal sealed record LogHealthCountEntry(string Value, long Count);

internal sealed record LogHealthHostSendEntry(
    string HostProfile,
    long AttemptCount,
    long FailureCount,
    long FocusLossFailureCount,
    long ClipboardFailureCount,
    long RetryTriggeredCount,
    long RetryRecoveredCount)
{
    public double FailureRate => AttemptCount == 0 ? 0 : (double)FailureCount / AttemptCount;
}

internal sealed record LogHealthOperationEntry(
    string OperationName,
    long AttemptCount,
    long SuccessCount,
    long FailureCount,
    long SkippedCount,
    double AverageDurationMs,
    string TopFailureStage,
    string TopFailureMessage)
{
    public double FailureRate => AttemptCount == 0 ? 0 : (double)FailureCount / AttemptCount;

    public double SuccessRate => AttemptCount == 0 ? 0 : (double)SuccessCount / AttemptCount;
}

internal sealed record LogHealthAiDraftEntry(
    string ProviderType,
    string ModelName,
    long ShownCount,
    long InputCount,
    long SentCount,
    long DismissedCount,
    long ProviderFailedCount,
    long ContextSkippedCount,
    double AverageLatencyMs)
{
    public string DisplayName => string.IsNullOrWhiteSpace(ModelName)
        ? (string.IsNullOrWhiteSpace(ProviderType) ? "unknown" : ProviderType)
        : $"{ProviderType}/{ModelName}";

    public double ProviderFailedRate => (ShownCount + ProviderFailedCount) == 0 ? 0 : (double)ProviderFailedCount / (ShownCount + ProviderFailedCount);
}

internal sealed record RecentExceptionEntry(
    string OccurredAt,
    string Source,
    string ExceptionType,
    string Message,
    bool IsTerminating);

internal sealed record LogHealthIssue(string Severity, string Code, string Message);
