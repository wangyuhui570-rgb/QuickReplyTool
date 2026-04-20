using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public interface ICandidateRetriever
{
    Task<CandidateRetrievalResult> RetrieveCandidatesAsync(
        ConversationRecommendationSnapshot snapshot,
        RecommendationRequest request);
}
