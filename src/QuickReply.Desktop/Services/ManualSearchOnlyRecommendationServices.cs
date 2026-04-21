using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal sealed class ManualSearchOnlyRecommendationServices :
    IRecommendationOrchestrator,
    IRecommendationTrackingService
{
    private static readonly PhraseRecommendationContext EmptyContext = new();

    public Task<PhraseRecommendationContext> GetRecommendationContextAsync(RecommendationRequest request)
    {
        return Task.FromResult(EmptyContext);
    }

    public Guid LogRecommendationsShown(
        ConversationRecommendationSnapshot snapshot,
        IReadOnlyList<PhraseRecommendationSuggestion> suggestions,
        SearchOverlayMode mode,
        string? searchKeyword)
    {
        return Guid.Empty;
    }

    public void LogRecommendationAccepted(
        PhraseRecommendationSuggestion suggestion,
        Guid sessionId,
        SearchOverlayMode mode,
        string? searchKeyword,
        ComposeActionMode actionMode,
        bool resultSuccess)
    {
    }

    public Guid LogKnowledgeCardsShown(
        ConversationRecommendationSnapshot snapshot,
        IReadOnlyList<KnowledgeCardSuggestion> knowledgeCards,
        SearchOverlayMode mode,
        string? searchKeyword)
    {
        return Guid.Empty;
    }

    public void LogKnowledgeCardAccepted(
        KnowledgeCardSuggestion knowledgeCard,
        Guid sessionId,
        SearchOverlayMode mode,
        string? searchKeyword,
        ComposeActionMode actionMode,
        bool resultSuccess)
    {
    }

    public Task SaveRecognitionCorrectionAsync(
        Guid sessionId,
        ConversationRecommendationSnapshot snapshot,
        ConversationRecognitionCorrection correction)
    {
        return Task.CompletedTask;
    }
}
