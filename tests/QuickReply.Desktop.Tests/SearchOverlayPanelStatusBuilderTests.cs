using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class SearchOverlayPanelStatusBuilderTests
{
    [Fact]
    public void BuildRecommendationStatusText_ShowsRefreshingState()
    {
        var text = SearchOverlayPanelStatusBuilder.BuildRecommendationStatusText(
            new ConversationRecommendationSnapshot(),
            [],
            [],
            isRefreshing: true);

        Assert.Equal("正在识别当前聊天上下文，并整理推荐短语...", text);
    }

    [Fact]
    public void BuildRecommendationStatusText_ExplainsCapturedButUnreliableContext()
    {
        var text = SearchOverlayPanelStatusBuilder.BuildRecommendationStatusText(
            new ConversationRecommendationSnapshot
            {
                CaptureStatus = "uia+ocr",
                CapturedText = "客户说安装视频在哪里"
            },
            [],
            [],
            isRefreshing: false);

        Assert.Contains("已抓到窗口文本", text, StringComparison.Ordinal);
        Assert.Contains("修正识别", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRecommendationStatusText_ExplainsRecognizedContextAndMatches()
    {
        var text = SearchOverlayPanelStatusBuilder.BuildRecommendationStatusText(
            new ConversationRecommendationSnapshot
            {
                RecognitionMethod = "ocr",
                RecognitionConfidence = 0.82d,
                RecognizedCustomerText = "支持哪些骑行软件"
            },
            [
                new PhraseRecommendationSuggestion
                {
                    Phrase = new ReplyPhrase
                    {
                        Id = Guid.NewGuid(),
                        Title = "软件兼容",
                        Content = "支持 Zwift 等常见骑行软件。"
                    }
                }
            ],
            [
                new KnowledgeCardSuggestion
                {
                    Card = new PublishedKnowledgeCard
                    {
                        Id = Guid.NewGuid(),
                        CandidateKind = "qa",
                        Platform = "wecom",
                        ProductKey = "SKU-1",
                        Question = "支持哪些骑行软件",
                        Answer = "支持 Zwift 等常见骑行软件。",
                        UpdatedAt = DateTimeOffset.Now
                    }
                }
            ],
            isRefreshing: false);

        Assert.Contains("已识别客户消息", text, StringComparison.Ordinal);
        Assert.Contains("OCR", text, StringComparison.Ordinal);
        Assert.Contains("置信度 0.82", text, StringComparison.Ordinal);
        Assert.Contains("历史高频 1 条", text, StringComparison.Ordinal);
        Assert.Contains("问答卡 1 条", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRecommendationStatusText_HidesInternalRecognitionMethodVersion()
    {
        var text = SearchOverlayPanelStatusBuilder.BuildRecommendationStatusText(
            new ConversationRecommendationSnapshot
            {
                RecognitionMethod = "automation_ocr_hybrid_v2",
                RecognitionConfidence = 0.34d,
                RecognizedCustomerText = "客户想问安装视频在哪里"
            },
            [],
            [],
            isRefreshing: false);

        Assert.Contains("自动 OCR 混合识别", text, StringComparison.Ordinal);
        Assert.DoesNotContain("automation_ocr_hybrid_v2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildResultsSectionSummary_ExplainsFallbackWhenSearchHasNoHits()
    {
        var text = SearchOverlayPanelStatusBuilder.BuildResultsSectionSummary(
            SearchOverlayMode.Phrase,
            "安装视频",
            resultCount: 0,
            hasRecommendationPanelContent: true);

        Assert.Equal("当前搜索没有直接命中，但上方仍有推荐短语或知识卡可供参考。", text);
    }

    [Fact]
    public void BuildEmptyStateHintText_ExplainsEmptyFileMode()
    {
        var text = SearchOverlayPanelStatusBuilder.BuildEmptyStateHintText(
            SearchOverlayMode.File,
            "安装包",
            hasRecommendationPanelContent: false);

        Assert.Contains("换一个文件名关键词", text, StringComparison.Ordinal);
    }
}
