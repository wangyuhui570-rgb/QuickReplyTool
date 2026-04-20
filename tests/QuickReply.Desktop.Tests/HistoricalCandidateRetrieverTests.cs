using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class HistoricalCandidateRetrieverTests
{
    [Fact]
    public async Task RetrieveCandidatesAsync_ReturnsCatalogFallbackSuggestions_WhenHistoricalSamplesAreUnavailable()
    {
        var phrase = new ReplyPhrase
        {
            Id = Guid.NewGuid(),
            Title = "发货说明",
            Tags = "发货,物流",
            Content = "您好，这边今天可以安排发货。"
        };

        var retriever = new HistoricalCandidateRetriever(
            new FakeRecommendationQueryService(),
            new FakeRecommendationPhraseCatalog([phrase]),
            new FakePublishedKnowledgeCardCatalog([]));

        var result = await retriever.RetrieveCandidatesAsync(
            new ConversationRecommendationSnapshot
            {
                PlatformHint = "wecom",
                RecognitionConfidence = 0.86d,
                RecognizedCustomerText = "这款今天能发货吗",
                Keywords = ["发货", "今天"]
            },
            new RecommendationRequest
            {
                DisplayCount = 3,
                MinimumSampleCount = 3
            });

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(phrase.Id, suggestion.Phrase.Id);
        Assert.Equal(PhraseRecommendationSuggestion.CatalogFallbackSource, suggestion.RecommendationSource);
        Assert.Equal("发货", suggestion.MatchedKeyword);
        Assert.Equal(0, suggestion.SampleCount);
    }

    [Fact]
    public async Task RetrieveCandidatesAsync_ReturnsPublishedKnowledgeSuggestions_WhenKnowledgeMatches()
    {
        var retriever = new HistoricalCandidateRetriever(
            new FakeRecommendationQueryService(),
            new FakeRecommendationPhraseCatalog([]),
            new FakePublishedKnowledgeCardCatalog(
            [
                new PublishedKnowledgeCard
                {
                    Id = Guid.NewGuid(),
                    Platform = "wecom",
                    ProductKey = "SKU-1",
                    Question = "这款今天能发吗",
                    Answer = "您好，这款今天可以安排发出。",
                    Synonyms = ["发货时效", "今天发货"],
                    Tags = ["发货", "时效"],
                    UpdatedAt = DateTimeOffset.Now
                }
            ]));

        var result = await retriever.RetrieveCandidatesAsync(
            new ConversationRecommendationSnapshot
            {
                PlatformHint = "wecom",
                RecognitionConfidence = 0.86d,
                RecognizedCustomerText = "这款今天能发货吗",
                Product = new ConversationProductContext
                {
                    ProductId = "SKU-1"
                },
                Keywords = ["发货", "今天"]
            },
            new RecommendationRequest
            {
                DisplayCount = 3,
                MinimumSampleCount = 3
            });

        var suggestion = Assert.Single(result.KnowledgeCards);
        Assert.Equal("发货", suggestion.MatchedKeyword);
        Assert.Equal("这款今天能发吗", suggestion.Card.Question);
    }

    private sealed class FakeRecommendationQueryService : IRecommendationQueryService
    {
        public Task<IReadOnlyList<HistoricalReplyGroupSuggestion>> GetSimilarReplyGroupsAsync(
            ConversationRecommendationSnapshot snapshot,
            int maxGroups = 3,
            RecommendationQueryScope? queryScope = null)
        {
            return Task.FromResult<IReadOnlyList<HistoricalReplyGroupSuggestion>>([]);
        }

        public Task<IReadOnlyList<PhraseRecommendationCandidate>> GetPhraseRecommendationsAsync(
            ConversationRecommendationSnapshot snapshot,
            int maxResults = 5,
            RecommendationQueryScope? queryScope = null)
        {
            return Task.FromResult<IReadOnlyList<PhraseRecommendationCandidate>>([]);
        }
    }

    private sealed class FakeRecommendationPhraseCatalog(IReadOnlyList<ReplyPhrase> phrases) : IRecommendationPhraseCatalog
    {
        public IReadOnlyList<ReplyPhrase> GetAllPhrases() => phrases;

        public int GetPhraseSendCount(Guid phraseId) => 0;

        public DateTimeOffset? GetPhraseLastSentAt(Guid phraseId) => null;
    }

    private sealed class FakePublishedKnowledgeCardCatalog(IReadOnlyList<PublishedKnowledgeCard> cards) : IPublishedKnowledgeCardCatalog
    {
        public IReadOnlyList<PublishedKnowledgeCard> GetAllPublishedKnowledgeCards() => cards;
    }
}
