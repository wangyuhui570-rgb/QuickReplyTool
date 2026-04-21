using System.Globalization;
using System.Text;

namespace QuickReply.Desktop.Services;

internal static class LogHealthReportWriter
{
    public static string BuildSummary(LogHealthReport report, DateTimeOffset? generatedAt = null)
    {
        var builder = new StringBuilder();
        var timestamp = generatedAt ?? DateTimeOffset.Now;

        builder.AppendLine("# 日志健康检查");
        builder.AppendLine();
        builder.AppendLine($"生成时间：{timestamp:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"日志目录：{report.LogRootPath}");
        builder.AppendLine($"数据库：{report.DatabasePath}");
        builder.AppendLine();
        builder.AppendLine("## 核心指标");
        builder.AppendLine();
        builder.AppendLine($"- 会话日志总数：{report.TotalConversationLogs}");
        builder.AppendLine($"- 发送失败数：{report.FailedConversationLogs} ({report.FailureRate:P1})");
        builder.AppendLine($"- low_confidence 数：{report.LowConfidenceConversationLogs} ({report.LowConfidenceRate:P1})");
        builder.AppendLine($"- 可疑抓取数：{report.SuspiciousCaptureConversationLogs} ({report.SuspiciousCaptureRate:P1})");
        builder.AppendLine($"- 搜索面板动作数：{report.SearchOverlayActionCount}");
        builder.AppendLine($"- recommendation_events：{report.RecommendationEventCount}");
        builder.AppendLine($"- recognition_corrections：{report.RecognitionCorrectionCount}");
        builder.AppendLine($"- 异常日志：{report.TotalExceptionCount}");
        builder.AppendLine($"- 终止型异常：{report.TerminatingExceptionCount}");
        builder.AppendLine();
        builder.AppendLine("## 结论");
        builder.AppendLine();

        if (report.Issues.Count == 0)
        {
            builder.AppendLine("- 本次日志健康检查通过，没有发现阻断级问题。");
        }
        else
        {
            foreach (var issue in report.Issues)
            {
                builder.AppendLine($"- [{issue.Severity}] {issue.Message}");
            }
        }

        AppendCountSection(builder, "高频失败原因", report.TopFailureMessages);
        AppendCountSection(builder, "版本分布", report.AppVersionCounts);
        AppendHostHealthSection(builder, report.HostSendHealth);
        AppendOperationFunnelSection(builder, report.OperationFunnels);

        if (report.RecentExceptions.Count > 0)
        {
            builder.AppendLine("## 最近异常");
            builder.AppendLine();
            foreach (var exception in report.RecentExceptions)
            {
                var terminatingFlag = exception.IsTerminating ? " [terminating]" : string.Empty;
                builder.AppendLine($"- {exception.OccurredAt} {exception.Source} {exception.ExceptionType}{terminatingFlag}");
                builder.AppendLine($"  {exception.Message}");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendCountSection(StringBuilder builder, string title, IReadOnlyList<LogHealthCountEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {title}");
        builder.AppendLine();
        foreach (var entry in entries)
        {
            var label = string.IsNullOrWhiteSpace(entry.Value) ? "<empty>" : entry.Value.ReplaceLineEndings(" ").Trim();
            builder.AppendLine($"- {label}: {entry.Count.ToString(CultureInfo.InvariantCulture)}");
        }

        builder.AppendLine();
    }

    private static void AppendHostHealthSection(StringBuilder builder, IReadOnlyList<LogHealthHostSendEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        builder.AppendLine("## 按宿主发送健康度");
        builder.AppendLine();
        foreach (var entry in entries)
        {
            builder.AppendLine(
                $"- {entry.HostProfile}: {entry.AttemptCount.ToString(CultureInfo.InvariantCulture)} 次，失败 {entry.FailureCount.ToString(CultureInfo.InvariantCulture)} ({entry.FailureRate:P1})，焦点丢失 {entry.FocusLossFailureCount.ToString(CultureInfo.InvariantCulture)}，剪贴板 {entry.ClipboardFailureCount.ToString(CultureInfo.InvariantCulture)}，自动补救 {entry.RetryRecoveredCount.ToString(CultureInfo.InvariantCulture)}/{entry.RetryTriggeredCount.ToString(CultureInfo.InvariantCulture)}");
        }

        builder.AppendLine();
    }

    private static void AppendOperationFunnelSection(StringBuilder builder, IReadOnlyList<LogHealthOperationEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        builder.AppendLine("## 关键流程漏斗");
        builder.AppendLine();
        foreach (var entry in entries)
        {
            var failureStage = string.IsNullOrWhiteSpace(entry.TopFailureStage) ? "-" : entry.TopFailureStage;
            builder.AppendLine(
                $"- {entry.OperationName}: {entry.AttemptCount.ToString(CultureInfo.InvariantCulture)} 次，成功 {entry.SuccessCount.ToString(CultureInfo.InvariantCulture)} ({entry.SuccessRate:P1})，失败 {entry.FailureCount.ToString(CultureInfo.InvariantCulture)}，跳过 {entry.SkippedCount.ToString(CultureInfo.InvariantCulture)}，平均耗时 {entry.AverageDurationMs.ToString("0.0", CultureInfo.InvariantCulture)} ms，主要掉点 {failureStage}");
        }

        builder.AppendLine();
    }

}
