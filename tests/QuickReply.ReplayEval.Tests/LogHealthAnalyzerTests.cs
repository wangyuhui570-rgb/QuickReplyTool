using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuickReply.Desktop.Services;

namespace QuickReply.ReplayEval.Tests;

public sealed class LogHealthAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_FlagsBlockingIssuesForCrashAndCaptureRegression()
    {
        using var scope = new TempLogRootScope();
        await using var connection = await scope.OpenConnectionAsync();

        await scope.CreateSchemaAsync(connection);
        await scope.InsertConversationRowsAsync(connection, totalRows: 60, failedRows: 8, suspiciousCaptureRows: 60, lowConfidenceRows: 60, searchOverlayRows: 60);
        await scope.InsertRecommendationEventsAsync(connection, count: 0);
        await scope.InsertRecognitionCorrectionsAsync(connection, count: 0);
        await scope.WriteExceptionLogAsync(isTerminating: true);
        await scope.WriteComposeOperationLogAsync("qianniu", retryTriggered: true, retryOutcome: "recovered", finalSuccess: true, finalMessage: "首次尝试未稳定完成，已自动补救后成功");

        var analyzer = new LogHealthAnalyzer();
        var report = await analyzer.AnalyzeAsync(scope.LogRootPath);

        Assert.True(report.HasBlockingIssues);
        Assert.Equal(60, report.TotalConversationLogs);
        Assert.Equal(8, report.FailedConversationLogs);
        Assert.Equal(60, report.LowConfidenceConversationLogs);
        Assert.Equal(60, report.SuspiciousCaptureConversationLogs);
        Assert.Equal(1, report.TerminatingExceptionCount);
        Assert.Contains(report.Issues, issue => issue.Code == "terminating_exception");
        Assert.Contains(report.Issues, issue => issue.Code == "high_failure_rate");
        Assert.Contains(report.Issues, issue => issue.Code == "capture_regression");
        Assert.Contains(report.Issues, issue => issue.Code == "missing_recommendation_events");
        var hostHealth = Assert.Single(report.HostSendHealth);
        Assert.Equal("qianniu", hostHealth.HostProfile);
        Assert.Equal(60, hostHealth.AttemptCount);
        Assert.Equal(8, hostHealth.FailureCount);
        Assert.Equal(1, hostHealth.RetryTriggeredCount);
        Assert.Equal(1, hostHealth.RetryRecoveredCount);
    }

    [Fact]
    public async Task AnalyzeAsync_PassesHealthyDataset()
    {
        using var scope = new TempLogRootScope();
        await using var connection = await scope.OpenConnectionAsync();

        await scope.CreateSchemaAsync(connection);
        await scope.InsertConversationRowsAsync(connection, totalRows: 24, failedRows: 1, suspiciousCaptureRows: 0, lowConfidenceRows: 4, searchOverlayRows: 10);
        await scope.InsertRecommendationEventsAsync(connection, count: 5);
        await scope.InsertRecognitionCorrectionsAsync(connection, count: 2);

        var analyzer = new LogHealthAnalyzer();
        var report = await analyzer.AnalyzeAsync(scope.LogRootPath);

        Assert.False(report.HasBlockingIssues);
        Assert.DoesNotContain(report.Issues, issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(24, report.TotalConversationLogs);
        Assert.Equal(1, report.FailedConversationLogs);
        Assert.Equal(5, report.RecommendationEventCount);
        Assert.Equal(2, report.RecognitionCorrectionCount);
    }

    [Fact]
    public async Task AnalyzeAsync_UsesStructuredComposeMetricsFromConversationLogs()
    {
        using var scope = new TempLogRootScope();
        await using var connection = await scope.OpenConnectionAsync();

        await scope.CreateSchemaAsync(connection);
        await scope.InsertConversationRowsAsync(
            connection,
            totalRows: 12,
            failedRows: 2,
            suspiciousCaptureRows: 0,
            lowConfidenceRows: 1,
            searchOverlayRows: 0,
            composeHostProfile: "pdd_workbench",
            retryTriggeredRows: 3,
            retryOutcome: "recovered",
            failureCategory: "focus_lost");
        await scope.InsertRecommendationEventsAsync(connection, count: 3);
        await scope.InsertRecognitionCorrectionsAsync(connection, count: 1);

        var analyzer = new LogHealthAnalyzer();
        var report = await analyzer.AnalyzeAsync(scope.LogRootPath);

        var hostHealth = Assert.Single(report.HostSendHealth);
        Assert.Equal("pdd_workbench", hostHealth.HostProfile);
        Assert.Equal(12, hostHealth.AttemptCount);
        Assert.Equal(2, hostHealth.FailureCount);
        Assert.Equal(2, hostHealth.FocusLossFailureCount);
        Assert.Equal(3, hostHealth.RetryTriggeredCount);
        Assert.Equal(3, hostHealth.RetryRecoveredCount);
    }

    [Fact]
    public async Task AnalyzeAsync_LoadsOperationFunnelsAndFlagsHighFailureOperation()
    {
        using var scope = new TempLogRootScope();
        await using var connection = await scope.OpenConnectionAsync();

        await scope.CreateSchemaAsync(connection);
        await scope.InsertConversationRowsAsync(connection, totalRows: 8, failedRows: 1, suspiciousCaptureRows: 0, lowConfidenceRows: 0, searchOverlayRows: 0);
        await scope.InsertRecommendationEventsAsync(connection, count: 2);
        await scope.InsertRecognitionCorrectionsAsync(connection, count: 1);
        await scope.InsertOperationEventsAsync(connection, "cloud_sync", successCount: 2, failureCount: 2, skippedCount: 0, topFailureStage: "conversation_upload", topFailureMessage: "服务器返回 503");

        var analyzer = new LogHealthAnalyzer();
        var report = await analyzer.AnalyzeAsync(scope.LogRootPath);

        var funnel = Assert.Single(report.OperationFunnels);
        Assert.Equal("cloud_sync", funnel.OperationName);
        Assert.Equal(4, funnel.AttemptCount);
        Assert.Equal(2, funnel.SuccessCount);
        Assert.Equal(2, funnel.FailureCount);
        Assert.Equal("conversation_upload", funnel.TopFailureStage);
        Assert.Contains(report.Issues, issue => issue.Code == "operation_funnel_cloud_sync");
    }

    [Fact]
    public void BuildSummary_IncludesKeyMetricsAndIssues()
    {
        var report = new LogHealthReport
        {
            LogRootPath = @"C:\logs",
            DatabasePath = @"C:\logs\conversation-logs.db",
            TotalConversationLogs = 120,
            FailedConversationLogs = 9,
            LowConfidenceConversationLogs = 120,
            SuspiciousCaptureConversationLogs = 100,
            SearchOverlayActionCount = 80,
            RecommendationEventCount = 0,
            RecognitionCorrectionCount = 0,
            TotalExceptionCount = 1,
            TerminatingExceptionCount = 1,
            TopFailureMessages = [new LogHealthCountEntry("剪贴板失败", 4)],
            HostSendHealth =
            [
                new LogHealthHostSendEntry("pdd_workbench", 18, 6, 4, 2, 3, 2)
            ],
            OperationFunnels =
            [
                new LogHealthOperationEntry("cloud_sync", 4, 2, 2, 0, 128.5, "conversation_upload", "服务器返回 503")
            ]
        };
        report.Issues.Add(new LogHealthIssue("error", "capture_regression", "疑似没有抓到真正的聊天正文。"));

        var summary = LogHealthReportWriter.BuildSummary(
            report,
            new DateTimeOffset(2026, 4, 15, 1, 20, 0, TimeSpan.FromHours(8)));

        Assert.Contains("生成时间：2026-04-15 01:20:00", summary, StringComparison.Ordinal);
        Assert.Contains("发送失败数：9 (7.5%)", summary, StringComparison.Ordinal);
        Assert.Contains("[error] 疑似没有抓到真正的聊天正文。", summary, StringComparison.Ordinal);
        Assert.Contains("剪贴板失败: 4", summary, StringComparison.Ordinal);
        Assert.Contains("pdd_workbench: 18 次，失败 6 (33.3%)", summary, StringComparison.Ordinal);
        Assert.Contains("cloud_sync: 4 次，成功 2 (50.0%)，失败 2，跳过 0，平均耗时 128.5 ms，主要掉点 conversation_upload", summary, StringComparison.Ordinal);
    }

    private sealed class TempLogRootScope : IDisposable
    {
        public TempLogRootScope()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "QuickReplyTool.ReplayEval.Tests", Guid.NewGuid().ToString("N"));
            LogRootPath = Path.Combine(RootPath, "logs");
            DatabasePath = Path.Combine(LogRootPath, "conversation-logs.db");
            Directory.CreateDirectory(LogRootPath);
        }

        public string RootPath { get; }

        public string LogRootPath { get; }

        public string DatabasePath { get; }

        public async Task<SqliteConnection> OpenConnectionAsync()
        {
            var connection = new SqliteConnection($"Data Source={DatabasePath}");
            await connection.OpenAsync();
            return connection;
        }

        public async Task CreateSchemaAsync(SqliteConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE conversation_logs (
                    id TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    trigger_source TEXT NOT NULL DEFAULT '',
                    result_success INTEGER NOT NULL DEFAULT 1,
                    result_message TEXT NOT NULL DEFAULT '',
                    compose_host_profile TEXT NOT NULL DEFAULT '',
                    compose_retry_triggered INTEGER NOT NULL DEFAULT 0,
                    compose_retry_outcome TEXT NOT NULL DEFAULT '',
                    compose_failure_category TEXT NOT NULL DEFAULT '',
                    recognition_status TEXT NOT NULL DEFAULT '',
                    captured_text TEXT NOT NULL DEFAULT '',
                    target_window_title TEXT NOT NULL DEFAULT '',
                    target_window_class_name TEXT NOT NULL DEFAULT '',
                    target_process_name TEXT NOT NULL DEFAULT '',
                    app_version TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE recommendation_events (
                    id TEXT NOT NULL,
                    recommendation_kind TEXT NOT NULL DEFAULT '',
                    event_type TEXT NOT NULL DEFAULT '',
                    latency_ms INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE recognition_corrections (
                    id TEXT NOT NULL
                );
                CREATE TABLE operation_events (
                    id TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    operation_name TEXT NOT NULL DEFAULT '',
                    stage TEXT NOT NULL DEFAULT '',
                    success INTEGER NOT NULL DEFAULT 0,
                    skipped INTEGER NOT NULL DEFAULT 0,
                    duration_ms INTEGER NOT NULL DEFAULT 0,
                    message TEXT NOT NULL DEFAULT ''
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        public async Task InsertConversationRowsAsync(
            SqliteConnection connection,
            int totalRows,
            int failedRows,
            int suspiciousCaptureRows,
            int lowConfidenceRows,
            int searchOverlayRows,
            string composeHostProfile = "",
            int retryTriggeredRows = 0,
            string retryOutcome = "recovered",
            string failureCategory = "clipboard")
        {
            for (var index = 0; index < totalRows; index++)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO conversation_logs (
                        id,
                        occurred_at,
                        trigger_source,
                        result_success,
                        result_message,
                        compose_host_profile,
                        compose_retry_triggered,
                        compose_retry_outcome,
                        compose_failure_category,
                        recognition_status,
                        captured_text,
                        target_window_title,
                        target_window_class_name,
                        target_process_name,
                        app_version)
                    VALUES (
                        $id,
                        $occurredAt,
                        $triggerSource,
                        $resultSuccess,
                        $resultMessage,
                        $composeHostProfile,
                        $composeRetryTriggered,
                        $composeRetryOutcome,
                        $composeFailureCategory,
                        $recognitionStatus,
                        $capturedText,
                        $targetWindowTitle,
                        $targetWindowClassName,
                        $targetProcessName,
                        $appVersion);
                    """;
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.Parse("2026-04-15T01:30:00+08:00").AddMinutes(index).ToString("O"));
                command.Parameters.AddWithValue("$triggerSource", index < searchOverlayRows ? "search_overlay_phrase_send" : "manual_send");
                command.Parameters.AddWithValue("$resultSuccess", index < failedRows ? 0 : 1);
                command.Parameters.AddWithValue("$resultMessage", index < failedRows ? "写入系统剪贴板失败：OpenClipboard 失败" : "已发送");
                command.Parameters.AddWithValue("$composeHostProfile", composeHostProfile);
                command.Parameters.AddWithValue("$composeRetryTriggered", index < retryTriggeredRows ? 1 : 0);
                command.Parameters.AddWithValue("$composeRetryOutcome", index < retryTriggeredRows ? retryOutcome : string.Empty);
                command.Parameters.AddWithValue("$composeFailureCategory", index < failedRows ? failureCategory : string.Empty);
                command.Parameters.AddWithValue("$recognitionStatus", index < lowConfidenceRows ? "low_confidence" : "recognized");
                command.Parameters.AddWithValue("$capturedText", index < suspiciousCaptureRows ? "千牛接待台\r\nChrome Legacy Window" : "客户说：这双锁鞋怎么选尺码？");
                command.Parameters.AddWithValue("$targetWindowTitle", "千牛接待台");
                command.Parameters.AddWithValue("$targetWindowClassName", "Qt5152QWindowIcon");
                command.Parameters.AddWithValue("$targetProcessName", "AliWorkbench");
                command.Parameters.AddWithValue("$appVersion", "2.1.9.0");
                await command.ExecuteNonQueryAsync();
            }
        }

        public async Task InsertRecommendationEventsAsync(SqliteConnection connection, int count)
        {
            for (var index = 0; index < count; index++)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO recommendation_events (id) VALUES ($id);";
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                await command.ExecuteNonQueryAsync();
            }
        }

        public async Task InsertRecognitionCorrectionsAsync(SqliteConnection connection, int count)
        {
            for (var index = 0; index < count; index++)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO recognition_corrections (id) VALUES ($id);";
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                await command.ExecuteNonQueryAsync();
            }
        }

        public async Task InsertOperationEventsAsync(
            SqliteConnection connection,
            string operationName,
            int successCount,
            int failureCount,
            int skippedCount,
            string topFailureStage,
            string topFailureMessage)
        {
            var totalCount = successCount + failureCount + skippedCount;
            for (var index = 0; index < totalCount; index++)
            {
                var success = index < successCount;
                var skipped = !success && index >= successCount + failureCount;
                var stage = success
                    ? "completed"
                    : skipped
                        ? "validation"
                        : topFailureStage;
                var message = success
                    ? "ok"
                    : skipped
                        ? "skipped"
                        : topFailureMessage;

                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO operation_events (
                        id,
                        occurred_at,
                        operation_name,
                        stage,
                        success,
                        skipped,
                        duration_ms,
                        message)
                    VALUES (
                        $id,
                        $occurredAt,
                        $operationName,
                        $stage,
                        $success,
                        $skipped,
                        $durationMs,
                        $message);
                    """;
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.Parse("2026-04-15T01:45:00+08:00").AddSeconds(index).ToString("O"));
                command.Parameters.AddWithValue("$operationName", operationName);
                command.Parameters.AddWithValue("$stage", stage);
                command.Parameters.AddWithValue("$success", success ? 1 : 0);
                command.Parameters.AddWithValue("$skipped", skipped ? 1 : 0);
                command.Parameters.AddWithValue("$durationMs", 120 + index);
                command.Parameters.AddWithValue("$message", message);
                await command.ExecuteNonQueryAsync();
            }
        }

        public async Task WriteExceptionLogAsync(bool isTerminating)
        {
            var exceptionDirectory = Path.Combine(LogRootPath, "exceptions");
            Directory.CreateDirectory(exceptionDirectory);

            var payload = JsonSerializer.Serialize(new
            {
                Id = Guid.NewGuid().ToString("N"),
                OccurredAt = "2026-04-15T01:35:00+08:00",
                Source = "dispatcher_unhandled_exception",
                ExceptionType = "System.Runtime.InteropServices.COMException",
                Message = "E_FAIL",
                IsTerminating = isTerminating
            });

            await File.WriteAllTextAsync(Path.Combine(exceptionDirectory, "20260415.jsonl"), payload + Environment.NewLine);
        }

        public async Task WriteComposeOperationLogAsync(
            string hostProfile,
            bool retryTriggered,
            string retryOutcome,
            bool finalSuccess,
            string finalMessage)
        {
            var diagnosticsDirectory = Path.Combine(LogRootPath, "search-overlay");
            Directory.CreateDirectory(diagnosticsDirectory);

            var payload = JsonSerializer.Serialize(new
            {
                occurredAt = "2026-04-15T01:38:00+08:00",
                source = "message_compose",
                @event = "compose_operation_completed",
                hostProfile,
                retryTriggered,
                retryOutcome,
                finalSuccess,
                finalMessage
            });

            await File.WriteAllTextAsync(Path.Combine(diagnosticsDirectory, "20260415.jsonl"), payload + Environment.NewLine);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
