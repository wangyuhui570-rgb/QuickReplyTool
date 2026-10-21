namespace QuickReply.Desktop.Models;

public sealed class CandidateRetrievalResult
{
    public IReadOnlyList<PhraseRecommendationSuggestion> Suggestions { get; init; } = [];

    public IReadOnlyList<HistoricalReplyGroupSuggestion> SimilarReplyGroups { get; init; } = [];
}
