using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public interface IRecommendationTrackingService
{
    Guid LogRecommendationsShown(
        ConversationRecommendationSnapshot snapshot,
        IReadOnlyList<PhraseRecommendationSuggestion> suggestions,
        SearchOverlayMode mode,
        string? searchKeyword);

    void LogRecommendationAccepted(
        PhraseRecommendationSuggestion suggestion,
        Guid sessionId,
        SearchOverlayMode mode,
        string? searchKeyword,
        ComposeActionMode actionMode,
        bool resultSuccess);

    Guid LogKnowledgeCardsShown(
        ConversationRecommendationSnapshot snapshot,
        IReadOnlyList<KnowledgeCardSuggestion> knowledgeCards,
        SearchOverlayMode mode,
        string? searchKeyword);

    void LogKnowledgeCardAccepted(
        KnowledgeCardSuggestion knowledgeCard,
        Guid sessionId,
        SearchOverlayMode mode,
        string? searchKeyword,
        ComposeActionMode actionMode,
        bool resultSuccess);

    Task SaveRecognitionCorrectionAsync(
        Guid sessionId,
        ConversationRecommendationSnapshot snapshot,
        ConversationRecognitionCorrection correction);
}
