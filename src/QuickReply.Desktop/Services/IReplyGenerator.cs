using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public interface IReplyGenerator
{
    Task<IReadOnlyList<GeneratedReplyCandidate>> GenerateCandidatesAsync(
        ConversationRecommendationSnapshot snapshot,
        CandidateRetrievalResult retrievalResult,
        RecommendationRequest request);
}
