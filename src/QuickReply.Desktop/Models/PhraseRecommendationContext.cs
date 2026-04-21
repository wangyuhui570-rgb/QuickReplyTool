using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Models;

public sealed class PhraseRecommendationContext
{
    public ConversationRecommendationSnapshot Snapshot { get; init; } = new();

    public IReadOnlyList<PhraseRecommendationSuggestion> Suggestions { get; init; } = [];

    public IReadOnlyList<KnowledgeCardSuggestion> KnowledgeCards { get; init; } = [];

    public IReadOnlyList<HistoricalReplyGroupSuggestion> SimilarReplyGroups { get; init; } = [];
}
