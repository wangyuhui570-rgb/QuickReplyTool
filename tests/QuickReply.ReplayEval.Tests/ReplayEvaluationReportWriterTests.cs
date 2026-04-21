using System.Text.Json;

namespace QuickReply.ReplayEval.Tests;

public sealed class ReplayEvaluationReportWriterTests
{
    [Fact]
    public void BuildSummary_FormatsMetricsAndPlatformBreakdown()
    {
        var rows = new[]
        {
            new ReplayEvaluationRow
            {
                SampleId = "sample-1",
                SampleSource = ReplaySampleSources.SuccessfulSend,
                PlatformHint = "wechat",
                PhraseStillExists = true,
                ActualRank = 1,
                CandidateCount = 3
            },
            new ReplayEvaluationRow
            {
                SampleId = "sample-2",
                SampleSource = ReplaySampleSources.FailedSend,
                PlatformHint = "wechat",
                PhraseStillExists = true,
                ActualRank = 4,
                CandidateCount = 4,
                CorrectedApplied = true
            },
            new ReplayEvaluationRow
            {
                SampleId = "sample-3",
                SampleSource = ReplaySampleSources.CorrectionBackfill,
                PlatformHint = "",
                PhraseStillExists = false,
                CandidateCount = 0
            }
        };

        var summary = ReplayEvaluationReportWriter.BuildSummary(
            rows,
            new ReplayEvalOptions
            {
                MinimumSampleCount = 6
            },
            new DateTimeOffset(2026, 4, 14, 9, 30, 0, TimeSpan.FromHours(8)));

        Assert.Contains("离线回放评估时间：2026-04-14 09:30:00", summary, StringComparison.Ordinal);
        Assert.Contains("样本总数：3", summary, StringComparison.Ordinal);
        Assert.Contains("纳入评估样本：2", summary, StringComparison.Ordinal);
        Assert.Contains("Recall@1：1/2 (50.0%)", summary, StringComparison.Ordinal);
        Assert.Contains("Recall@3：1/2 (50.0%)", summary, StringComparison.Ordinal);
        Assert.Contains("成功发送样本：1", summary, StringComparison.Ordinal);
        Assert.Contains("失败发送样本：1", summary, StringComparison.Ordinal);
        Assert.Contains("纠错回灌样本：1", summary, StringComparison.Ordinal);
        Assert.Contains("平台 wechat：样本 2，Recall@3 1/2 (50.0%)", summary, StringComparison.Ordinal);
        Assert.Contains("平台 unknown：样本 1，Recall@3 0/0 (0.0%)", summary, StringComparison.Ordinal);
        Assert.Contains("来源 successful_send：样本 1，纳入评估 1，无候选 0，miss 0，Recall@3 1/1 (100.0%)", summary, StringComparison.Ordinal);
        Assert.Contains("来源 failed_send：样本 1，纳入评估 1，无候选 0，miss 0，Recall@3 0/1 (0.0%)", summary, StringComparison.Ordinal);
        Assert.Contains("来源 correction_backfill：样本 1，纳入评估 0，无候选 0，miss 0，Recall@3 0/0 (0.0%)", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectMissRows_FiltersBySource()
    {
        var rows = new[]
        {
            new ReplayEvaluationRow
            {
                SampleId = "success-miss",
                SampleSource = ReplaySampleSources.SuccessfulSend,
                PhraseStillExists = true,
                ActualRank = null
            },
            new ReplayEvaluationRow
            {
                SampleId = "failed-miss",
                SampleSource = ReplaySampleSources.FailedSend,
                PhraseStillExists = true,
                ActualRank = 8
            },
            new ReplayEvaluationRow
            {
                SampleId = "failed-hit",
                SampleSource = ReplaySampleSources.FailedSend,
                PhraseStillExists = true,
                ActualRank = 2
            }
        };

        var failedMisses = ReplayEvaluationReportWriter.SelectMissRows(rows, ReplaySampleSources.FailedSend);

        var miss = Assert.Single(failedMisses);
        Assert.Equal("failed-miss", miss.SampleId);
    }

    [Fact]
    public async Task WriteJsonLinesAsync_WritesOneJsonObjectPerLine()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "QuickReplyTool.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var outputPath = Path.Combine(tempRoot, "rows.jsonl");
            var rows = new[]
            {
                new ReplayEvaluationRow { SampleId = "sample-1", PlatformHint = "wechat", PhraseStillExists = true },
                new ReplayEvaluationRow { SampleId = "sample-2", PlatformHint = "qq", PhraseStillExists = true }
            };

            await ReplayEvaluationReportWriter.WriteJsonLinesAsync(outputPath, rows);

            var lines = await File.ReadAllLinesAsync(outputPath);

            Assert.Equal(2, lines.Length);
            Assert.Equal("sample-1", JsonDocument.Parse(lines[0]).RootElement.GetProperty("SampleId").GetString());
            Assert.Equal("sample-2", JsonDocument.Parse(lines[1]).RootElement.GetProperty("SampleId").GetString());
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }
}
