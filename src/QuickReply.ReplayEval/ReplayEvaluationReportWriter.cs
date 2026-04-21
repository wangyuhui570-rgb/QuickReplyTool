using System.Text.Json;

internal static class ReplayEvaluationReportWriter
{
    public static IReadOnlyList<ReplayEvaluationRow> SelectMissRows(
        IReadOnlyList<ReplayEvaluationRow> rows,
        string? sampleSource = null,
        int take = 80)
    {
        var filteredRows = rows.Where(row => row.PhraseStillExists && (row.ActualRank is null || row.ActualRank > 5));
        if (!string.IsNullOrWhiteSpace(sampleSource))
        {
            filteredRows = filteredRows.Where(row => string.Equals(row.SampleSource, sampleSource, StringComparison.Ordinal));
        }

        return filteredRows.Take(Math.Max(0, take)).ToArray();
    }

    public static async Task WriteJsonLinesAsync<T>(string path, IEnumerable<T> rows)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        await using var writer = new StreamWriter(path, append: false);
        foreach (var row in rows)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, jsonOptions));
        }
    }

    public static string BuildSummary(
        IReadOnlyList<ReplayEvaluationRow> rows,
        ReplayEvalOptions options,
        DateTimeOffset? now = null)
    {
        var effectiveNow = now ?? DateTimeOffset.Now;
        var eligibleRows = rows.Where(row => row.PhraseStillExists).ToList();
        var totalRows = rows.Count;
        var correctedCount = rows.Count(row => row.CorrectedApplied);
        var deletedPhraseCount = rows.Count(row => !row.PhraseStillExists);
        var noCandidateCount = eligibleRows.Count(row => row.CandidateCount == 0);
        var successfulSendCount = rows.Count(row => string.Equals(row.SampleSource, ReplaySampleSources.SuccessfulSend, StringComparison.Ordinal));
        var failedSendCount = rows.Count(row => string.Equals(row.SampleSource, ReplaySampleSources.FailedSend, StringComparison.Ordinal));
        var correctionBackfillCount = rows.Count(row => string.Equals(row.SampleSource, ReplaySampleSources.CorrectionBackfill, StringComparison.Ordinal));
        var hitAt1 = eligibleRows.Count(row => row.ActualRank == 1);
        var hitAt3 = eligibleRows.Count(row => row.ActualRank is <= 3);
        var hitAt5 = eligibleRows.Count(row => row.ActualRank is <= 5);

        var lines = new List<string>
        {
            $"离线回放评估时间：{effectiveNow:yyyy-MM-dd HH:mm:ss}",
            $"样本总数：{totalRows}",
            $"纳入评估样本：{eligibleRows.Count}",
            $"缺少历史短语快照的样本：{deletedPhraseCount}",
            $"应用纠错样本：{correctedCount}",
            $"成功发送样本：{successfulSendCount}",
            $"失败发送样本：{failedSendCount}",
            $"纠错回灌样本：{correctionBackfillCount}",
            $"无候选样本：{noCandidateCount}",
            $"Recall@1：{FormatRate(hitAt1, eligibleRows.Count)}",
            $"Recall@3：{FormatRate(hitAt3, eligibleRows.Count)}",
            $"Recall@5：{FormatRate(hitAt5, eligibleRows.Count)}",
            $"最小样本阈值：{options.MinimumSampleCount}",
            "说明：本次回放会按样本发生时间截断历史日志，排除样本自身，并用日志重建历史短语快照目录。结果比之前更接近真实回测，但仍不是完整线上环境快照。"
        };

        foreach (var platformGroup in rows.GroupBy(row => string.IsNullOrWhiteSpace(row.PlatformHint) ? "unknown" : row.PlatformHint))
        {
            var platformEligible = platformGroup.Where(row => row.PhraseStillExists).ToList();
            lines.Add(
                $"平台 {platformGroup.Key}：样本 {platformGroup.Count()}，Recall@3 {FormatRate(platformEligible.Count(row => row.ActualRank is <= 3), platformEligible.Count)}");
        }

        foreach (var sourceGroup in rows.GroupBy(row => row.SampleSource))
        {
            var sourceEligible = sourceGroup.Where(row => row.PhraseStillExists).ToList();
            var sourceMisses = sourceEligible.Count(row => row.ActualRank is null || row.ActualRank > 5);
            var sourceNoCandidates = sourceEligible.Count(row => row.CandidateCount == 0);
            lines.Add(
                $"来源 {sourceGroup.Key}：样本 {sourceGroup.Count()}，纳入评估 {sourceEligible.Count}，无候选 {sourceNoCandidates}，miss {sourceMisses}，Recall@3 {FormatRate(sourceEligible.Count(row => row.ActualRank is <= 3), sourceEligible.Count)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatRate(int value, int total)
    {
        if (total <= 0)
        {
            return "0/0 (0.0%)";
        }

        var rate = (double)value / total * 100d;
        return $"{value}/{total} ({rate:0.0}%)";
    }
}
