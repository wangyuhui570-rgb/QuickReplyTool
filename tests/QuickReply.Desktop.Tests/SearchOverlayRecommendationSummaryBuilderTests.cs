using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class SearchOverlayRecommendationSummaryBuilderTests
{
    [Fact]
    public void BuildRecommendationHintText_ExplainsKnowledgeOnlyFallback()
    {
        IReadOnlyList<KnowledgeCardSuggestion> knowledgeCards =
        [
            new KnowledgeCardSuggestion
            {
                Card = new PublishedKnowledgeCard
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    CandidateKind = "resource",
                    Platform = "wecom",
                    ProductKey = "SKU-1",
                    Question = "安装视频在哪",
                    Answer = "安装视频：https://example.com/install",
                    UpdatedAt = DateTimeOffset.Now
                },
                DisplayRank = 1,
                MatchedKeyword = "安装",
                MatchCount = 1,
                Confidence = 0.8d
            }
        ];

        var text = SearchOverlayRecommendationSummaryBuilder.BuildRecommendationHintText([], knowledgeCards);

        Assert.Equal("已命中 资料卡 1 条。", text);
    }

    [Fact]
    public void BuildRecommendationHintText_SplitsHistoricalCatalogAndKnowledgeSources()
    {
        IReadOnlyList<PhraseRecommendationSuggestion> suggestions =
        [
            new PhraseRecommendationSuggestion
            {
                Phrase = new ReplyPhrase { Id = Guid.NewGuid(), Title = "发货答复", Content = "今天可以发出。" },
                RecommendationSource = PhraseRecommendationSuggestion.HistoricalSource
            },
            new PhraseRecommendationSuggestion
            {
                Phrase = new ReplyPhrase { Id = Guid.NewGuid(), Title = "安装视频", Content = "安装视频链接如下。" },
                RecommendationSource = PhraseRecommendationSuggestion.CatalogFallbackSource
            }
        ];
        IReadOnlyList<KnowledgeCardSuggestion> knowledgeCards =
        [
            new KnowledgeCardSuggestion
            {
                Card = new PublishedKnowledgeCard
                {
                    Id = Guid.NewGuid(),
                    CandidateKind = "qa",
                    Platform = "wecom",
                    Question = "这款今天能发吗",
                    Answer = "今天可以发出。",
                    UpdatedAt = DateTimeOffset.Now
                }
            }
        ];

        var text = SearchOverlayRecommendationSummaryBuilder.BuildRecommendationHintText(suggestions, knowledgeCards);

        Assert.Equal("已命中 历史高频 1 条｜短语库匹配 1 条｜问答卡 1 条。", text);
    }

    [Fact]
    public void BuildGroundingSummaryText_IncludesQaAndResourceBreakdown()
    {
        IReadOnlyList<PhraseRecommendationSuggestion> suggestions =
        [
            new PhraseRecommendationSuggestion
            {
                Phrase = new ReplyPhrase
                {
                    Id = Guid.NewGuid(),
                    Title = "发货答复",
                    Content = "今天可以发出。"
                }
            }
        ];
        IReadOnlyList<KnowledgeCardSuggestion> knowledgeCards =
        [
            new KnowledgeCardSuggestion
            {
                Card = new PublishedKnowledgeCard
                {
                    Id = Guid.NewGuid(),
                    CandidateKind = "qa",
                    Platform = "wecom",
                    ProductKey = "SKU-1",
                    Question = "能发吗",
                    Answer = "今天可以发出。",
                    UpdatedAt = DateTimeOffset.Now
                },
                DisplayRank = 1
            },
            new KnowledgeCardSuggestion
            {
                Card = new PublishedKnowledgeCard
                {
                    Id = Guid.NewGuid(),
                    CandidateKind = "resource",
                    Platform = "wecom",
                    ProductKey = "SKU-1",
                    Question = "安装视频",
                    Answer = "视频地址",
                    UpdatedAt = DateTimeOffset.Now
                },
                DisplayRank = 2
            }
        ];

        var text = SearchOverlayRecommendationSummaryBuilder.BuildGroundingSummaryText(suggestions, knowledgeCards);

        Assert.Equal("参考依据：历史高频 1 条｜知识 2 条（问答卡 1 / 资料卡 1）", text);
    }

    [Fact]
    public void BuildAiDraftStatusMessage_AppendsProviderAndGroundingSummary()
    {
        var text = SearchOverlayRecommendationSummaryBuilder.BuildAiDraftStatusMessage(
            AiDraftGenerationStatus.ProviderFailed(
                "生成失败",
                "ollama",
                "qwen2.5:3b",
                latencyMs: 200,
                groundingPhraseCount: 2,
                groundingKnowledgeCount: 1,
                fallbackReason: "timeout"));

        Assert.Contains("生成失败", text, StringComparison.Ordinal);
        Assert.Contains("provider Ollama/qwen2.5:3b", text, StringComparison.Ordinal);
        Assert.Contains("参考短语 2 条", text, StringComparison.Ordinal);
        Assert.Contains("知识 1 条", text, StringComparison.Ordinal);
        Assert.Contains("fallback timeout", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAiDraftStatusMessage_ShowsGeneratedSummary()
    {
        var text = SearchOverlayRecommendationSummaryBuilder.BuildAiDraftStatusMessage(
            AiDraftGenerationStatus.Generated(
                "生成完成",
                "codex_cli",
                "gpt-5.4",
                latencyMs: 320,
                groundingPhraseCount: 3,
                groundingKnowledgeCount: 2,
                fallbackReason: string.Empty,
                characterCount: 120,
                generatedCount: 2));

        Assert.Contains("生成完成", text, StringComparison.Ordinal);
        Assert.Contains("已生成 2 条 AI 草稿", text, StringComparison.Ordinal);
        Assert.Contains("provider ChatGPT/Codex/gpt-5.4", text, StringComparison.Ordinal);
        Assert.Contains("参考短语 3 条", text, StringComparison.Ordinal);
        Assert.Contains("知识 2 条", text, StringComparison.Ordinal);
    }
}
