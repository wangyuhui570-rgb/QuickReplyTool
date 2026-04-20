using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public interface IRecommendationQueryService
{
    Task<IReadOnlyList<PhraseRecommendationCandidate>> GetPhraseRecommendationsAsync(
        ConversationRecommendationSnapshot snapshot,
        int maxResults = 5,
        RecommendationQueryScope? queryScope = null);

    Task<IReadOnlyList<HistoricalReplyGroupSuggestion>> GetSimilarReplyGroupsAsync(
        ConversationRecommendationSnapshot snapshot,
        int maxResults = 3,
        RecommendationQueryScope? queryScope = null);
}
