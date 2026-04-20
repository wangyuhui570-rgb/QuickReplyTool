using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class RetrievalBackedReplyGenerator : IReplyGenerator
{
    public Task<IReadOnlyList<GeneratedReplyCandidate>> GenerateCandidatesAsync(
        ConversationRecommendationSnapshot snapshot,
        CandidateRetrievalResult retrievalResult,
        RecommendationRequest request)
    {
        var maxResults = Math.Max(1, request.GeneratedReplyCount);
        var generated = retrievalResult.Suggestions
            .Where(item => !string.IsNullOrWhiteSpace(item.Phrase.Content))
            .GroupBy(item => item.Phrase.Content, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(maxResults)
            .Select(item => new GeneratedReplyCandidate
            {
                Text = item.Phrase.Content,
                Source = "retrieval_fallback",
                Reason = item.ReasonText,
                Confidence = item.Confidence,
                BackingPhraseId = item.Phrase.Id,
                BackingPhraseTitle = item.Phrase.Title
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<GeneratedReplyCandidate>>(generated);
    }
}
