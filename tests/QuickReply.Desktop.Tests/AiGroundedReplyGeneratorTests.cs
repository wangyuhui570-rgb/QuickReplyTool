using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class AiGroundedReplyGeneratorTests
{
    [Fact]
    public async Task GenerateCandidatesAsync_UsesOllamaDrafts_WhenContextAndGroundingAvailable()
    {
        var settings = new AppSettings
        {
            EnableAiDrafts = true,
            OllamaModelName = "qwen2.5:3b"
        };

        var provider = new FakeAiDraftProvider("ollama", (_, _, _) => Task.FromResult(
            AiDraftProviderResponse.Succeeded(
                "ollama",
                "qwen2.5:3b",
                "已生成 1 条 AI 草稿。",
                latencyMs: 420,
                characterCount: 18,
                drafts:
                [
                    new GeneratedReplyCandidate
                    {
                        DisplayRank = 1,
                        Text = "您好，这款今天可以发出。",
                        Reason = "参考了历史高频短语",
                        ProviderType = "ollama",
                        ModelName = "qwen2.5:3b",
                        GroundingPhraseCount = 1,
                        GroundingKnowledgeCount = 1,
                        GroundingKnowledgeIdsJson = "[]",
                        LatencyMs = 420,
                        CharacterCount = 18
                    }
                ])));

        var generator = new AiGroundedReplyGenerator(settings, provider, new FakeAiDraftProvider("api", (_, _, _) => throw new InvalidOperationException()));

        var results = await generator.GenerateCandidatesAsync(
            CreateSnapshot(),
            CreateRetrievalResult(),
            new RecommendationRequest { GeneratedReplyCount = 3 });

        var draft = Assert.Single(results);
        Assert.Equal("您好，这款今天可以发出。", draft.Text);
        Assert.Equal(AiDraftGenerationState.Generated, generator.LastStatus.State);
        Assert.Equal("ollama", generator.LastStatus.ProviderType);
        Assert.Equal("qwen2.5:3b", generator.LastStatus.ModelName);
    }

    [Fact]
    public async Task GenerateCandidatesAsync_UsesPublishedKnowledge_WhenPhraseGroundingIsUnavailable()
    {
        var settings = new AppSettings
        {
            EnableAiDrafts = true,
            OllamaModelName = "qwen2.5:3b"
        };

        var provider = new FakeAiDraftProvider("ollama", (_, request, _) =>
        {
            Assert.Empty(request.GroundingSuggestions);
            Assert.Single(request.GroundingKnowledgeCards);
            return Task.FromResult(
                AiDraftProviderResponse.Succeeded(
                    "ollama",
                    "qwen2.5:3b",
                    "已生成 1 条 AI 草稿。",
                    latencyMs: 210,
                    characterCount: 14,
                    drafts:
                    [
                        new GeneratedReplyCandidate
                        {
                            DisplayRank = 1,
                            Text = "您好，这款库存充足，今天可以安排发出。",
                            Reason = "参考了已发布知识卡",
                            ProviderType = "ollama",
                            ModelName = "qwen2.5:3b",
                            GroundingPhraseCount = 0,
                            GroundingKnowledgeCount = 1,
                            GroundingKnowledgeIdsJson = "[\"00000000-0000-0000-0000-000000000001\"]",
                            LatencyMs = 210,
                            CharacterCount = 14
                        }
                    ]));
        });

        var generator = new AiGroundedReplyGenerator(settings, provider, new FakeAiDraftProvider("api", (_, _, _) => throw new InvalidOperationException()));

        var results = await generator.GenerateCandidatesAsync(
            CreateSnapshot(),
            new CandidateRetrievalResult
            {
                KnowledgeCards =
                [
                    new KnowledgeCardSuggestion
                    {
                        Card = new PublishedKnowledgeCard
                        {
                            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                            Platform = "wecom",
                            ProductKey = "SKU-1",
                            Question = "这款今天能发吗",
                            Answer = "您好，这款库存充足，今天可以安排发出。",
                            UpdatedAt = DateTimeOffset.Now
                        },
                        DisplayRank = 1,
                        MatchedKeyword = "发货",
                        MatchCount = 2,
                        Confidence = 0.86d
                    }
                ]
            },
            new RecommendationRequest { GeneratedReplyCount = 3 });

        var draft = Assert.Single(results);
        Assert.Equal(1, draft.GroundingKnowledgeCount);
        Assert.Equal(AiDraftGenerationState.Generated, generator.LastStatus.State);
        Assert.Equal(1, generator.LastStatus.GroundingKnowledgeCount);
    }

    [Fact]
    public async Task GenerateCandidatesAsync_SkipsWhenContextIsInsufficient()
    {
        var settings = new AppSettings
        {
            EnableAiDrafts = true,
            OllamaModelName = "qwen2.5:3b"
        };

        var generator = new AiGroundedReplyGenerator(
            settings,
            new FakeAiDraftProvider("ollama", (_, _, _) => throw new InvalidOperationException()),
            new FakeAiDraftProvider("api", (_, _, _) => throw new InvalidOperationException()));

        var results = await generator.GenerateCandidatesAsync(
            new ConversationRecommendationSnapshot
            {
                RecognitionConfidence = 0.20,
                RecognizedCustomerText = "企业微信"
            },
            CreateRetrievalResult(),
            new RecommendationRequest());

        var draft = Assert.Single(results);
        Assert.Equal("grounding_fallback", draft.ProviderType);
        Assert.Equal(AiDraftGenerationState.Generated, generator.LastStatus.State);
        Assert.Contains("保守草稿", generator.LastStatus.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateCandidatesAsync_FallsBackToGroundedDraftsWhenProviderFails()
    {
        var settings = new AppSettings
        {
            EnableAiDrafts = true,
            OllamaModelName = "qwen2.5:3b"
        };

        var generator = new AiGroundedReplyGenerator(
            settings,
            new FakeAiDraftProvider("ollama", (_, _, _) => Task.FromResult(
                AiDraftProviderResponse.Failed(
                    "ollama",
                    "qwen2.5:3b",
                    "Ollama 不可用",
                    "ollama_unavailable"))),
            new FakeAiDraftProvider("api", (_, _, _) => throw new InvalidOperationException()));

        var results = await generator.GenerateCandidatesAsync(
            CreateSnapshot(),
            CreateRetrievalResult(),
            new RecommendationRequest());

        var draft = Assert.Single(results);
        Assert.Equal("grounding_fallback", draft.ProviderType);
        Assert.Equal("ollama_unavailable", draft.FallbackReason);
        Assert.Equal(AiDraftGenerationState.Generated, generator.LastStatus.State);
        Assert.Equal("grounding_fallback", generator.LastStatus.ProviderType);
    }

    private static ConversationRecommendationSnapshot CreateSnapshot()
    {
        return new ConversationRecommendationSnapshot
        {
            PlatformHint = "wecom",
            RecognitionConfidence = 0.92,
            RecognizedCustomerText = "这双鞋今天能发吗？",
            RecognizedContextText = "客户想确认发货时效。",
            Keywords = ["发货", "今天"]
        };
    }

    private static CandidateRetrievalResult CreateRetrievalResult()
    {
        return new CandidateRetrievalResult
        {
            Suggestions =
            [
                new PhraseRecommendationSuggestion
                {
                    Phrase = new ReplyPhrase
                    {
                        Id = Guid.NewGuid(),
                        Title = "发货答复",
                        Content = "您好，这款今天可以发出。"
                    },
                    DisplayRank = 1,
                    MatchedKeyword = "发货",
                    MatchCount = 4,
                    SampleCount = 5,
                    Confidence = 0.8,
                    RecognizedCustomerText = "这双鞋今天能发吗？"
                }
            ],
            KnowledgeCards =
            [
                new KnowledgeCardSuggestion
                {
                    Card = new PublishedKnowledgeCard
                    {
                        Id = Guid.NewGuid(),
                        Platform = "wecom",
                        ProductKey = "SPU-1",
                        Question = "这双鞋今天能发吗？",
                        Answer = "您好，这款今天可以发出。",
                        UpdatedAt = DateTimeOffset.Now
                    },
                    DisplayRank = 1,
                    MatchedKeyword = "发货",
                    MatchCount = 2,
                    Confidence = 0.8d
                }
            ]
        };
    }

    private sealed class FakeAiDraftProvider(string providerType, Func<AppSettings, AiDraftProviderRequest, CancellationToken, Task<AiDraftProviderResponse>> handler)
        : IAiDraftProvider
    {
        public string ProviderType => providerType;

        public Task<AiDraftProviderResponse> GenerateAsync(
            AppSettings settings,
            AiDraftProviderRequest request,
            CancellationToken cancellationToken)
        {
            return handler(settings, request, cancellationToken);
        }
    }
}
