using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public interface IRecommendationOrchestrator
{
    Task<PhraseRecommendationContext> GetRecommendationContextAsync(RecommendationRequest request);
}
