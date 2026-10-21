using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class HistoricalCandidateRetriever : ICandidateRetriever
{
    private readonly IRecommendationQueryService _recommendationQueryService;
    private readonly IRecommendationPhraseCatalog _phraseCatalog;

    public HistoricalCandidateRetriever(
        IRecommendationQueryService recommendationQueryService,
        IRecommendationPhraseCatalog phraseCatalog)
    {
        _recommendationQueryService = recommendationQueryService;
        _phraseCatalog = phraseCatalog;
    }

    public async Task<CandidateRetrievalResult> RetrieveCandidatesAsync(
        ConversationRecommendationSnapshot snapshot,
        RecommendationRequest request)
    {
        var displayCount = Math.Max(1, request.DisplayCount);
        var minimumSampleCount = Math.Max(0, request.MinimumSampleCount);
        var rankingReferenceTime = request.QueryScope?.MaxOccurredAt ?? DateTimeOffset.Now;

        if (snapshot is null ||
            snapshot.RecognitionConfidence < 0.20d ||
            snapshot.Keywords.Count == 0 ||
            (string.IsNullOrWhiteSpace(snapshot.RecognizedCustomerText) &&
             string.IsNullOrWhiteSpace(snapshot.RecognizedContextText) &&
             !snapshot.Product.HasValue))
        {
            return new CandidateRetrievalResult();
        }

        var similarReplyGroups = await _recommendationQueryService
            .GetSimilarReplyGroupsAsync(snapshot, 3, request.QueryScope)
            .ConfigureAwait(false);
        var fetchCount = Math.Max((Math.Max(displayCount + 2, 5)) * 2, 8);
        var candidates = await _recommendationQueryService
            .GetPhraseRecommendationsAsync(snapshot, fetchCount, request.QueryScope)
            .ConfigureAwait(false);

        var phrasesById = _phraseCatalog
            .GetAllPhrases()
            .GroupBy(phrase => phrase.Id)
            .ToDictionary(group => group.Key, group => group.First());

        var suggestions = candidates.Count == 0
            ? []
            : candidates
                .Where(candidate => phrasesById.TryGetValue(candidate.PhraseId, out _))
                .Select(candidate =>
                {
                    var phrase = phrasesById[candidate.PhraseId];
                    var usageBoost = Math.Log2(_phraseCatalog.GetPhraseSendCount(phrase.Id) + 1) * 0.03d;
                    var recentBoost = ComputePhraseRecommendationRecentBoost(
                        _phraseCatalog.GetPhraseLastSentAt(phrase.Id),
                        rankingReferenceTime);
                    return new
                    {
                        Phrase = phrase,
                        candidate.MatchedKeyword,
                        candidate.MatchCount,
                        candidate.SampleCount,
                        candidate.Confidence,
                        candidate.RecognizedCustomerText,
                        Score = candidate.Confidence + usageBoost + recentBoost
                    };
                })
                .OrderByDescending(entry => entry.Score)
                .ThenByDescending(entry => entry.MatchCount)
                .ThenByDescending(entry => entry.SampleCount)
                .Where(entry => entry.SampleCount >= minimumSampleCount)
                .Take(displayCount)
                .Select((entry, index) => new PhraseRecommendationSuggestion
                {
                    Phrase = entry.Phrase,
                    DisplayRank = index + 1,
                    MatchedKeyword = entry.MatchedKeyword,
                    MatchCount = entry.MatchCount,
                    SampleCount = entry.SampleCount,
                    Confidence = entry.Confidence,
                    RecognizedCustomerText = entry.RecognizedCustomerText,
                    RecommendationSource = PhraseRecommendationSuggestion.HistoricalSource
                })
                .ToList();

        if (suggestions.Count == 0)
        {
            suggestions = BuildCatalogFallbackSuggestions(snapshot, displayCount, rankingReferenceTime);
        }

        return new CandidateRetrievalResult
        {
            Suggestions = suggestions,
            SimilarReplyGroups = similarReplyGroups
        };
    }

    private static double ComputePhraseRecommendationRecentBoost(
        DateTimeOffset? lastSentAt,
        DateTimeOffset referenceTime)
    {
        if (lastSentAt is null)
        {
            return 0d;
        }

        var days = Math.Max(0d, (referenceTime - lastSentAt.Value).TotalDays);
        return Math.Max(0d, (12d - days) * 0.01d);
    }

    private List<PhraseRecommendationSuggestion> BuildCatalogFallbackSuggestions(
        ConversationRecommendationSnapshot snapshot,
        int displayCount,
        DateTimeOffset rankingReferenceTime)
    {
        var keywords = snapshot.Keywords
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Take(12)
            .ToArray();
        if (keywords.Length == 0)
        {
            return [];
        }

        var fallbackSuggestions = _phraseCatalog.GetAllPhrases()
            .Where(phrase => phrase is not null && (!string.IsNullOrWhiteSpace(phrase.Content) || !string.IsNullOrWhiteSpace(phrase.Title)))
            .Select(phrase => BuildCatalogFallbackEntry(phrase, keywords, rankingReferenceTime))
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .OrderByDescending(entry => entry.Score)
            .ThenByDescending(entry => entry.KeywordHitCount)
            .ThenByDescending(entry => entry.SendCount)
            .ThenByDescending(entry => entry.Phrase.UpdatedAt)
            .Take(displayCount)
            .Select((entry, index) => new PhraseRecommendationSuggestion
            {
                Phrase = entry.Phrase,
                DisplayRank = index + 1,
                MatchedKeyword = entry.PrimaryKeyword,
                MatchCount = entry.KeywordHitCount,
                SampleCount = 0,
                Confidence = entry.Confidence,
                RecognizedCustomerText = snapshot.RecognizedCustomerText,
                RecommendationSource = PhraseRecommendationSuggestion.CatalogFallbackSource
            })
            .ToList();

        return fallbackSuggestions;
    }

    private CatalogFallbackEntry? BuildCatalogFallbackEntry(
        ReplyPhrase phrase,
        IReadOnlyList<string> keywords,
        DateTimeOffset rankingReferenceTime)
    {
        var title = NormalizeCatalogText(phrase.Title);
        var tags = NormalizeCatalogText(phrase.Tags);
        var content = NormalizeCatalogText(phrase.Content);

        var score = 0d;
        var keywordHitCount = 0;
        var primaryKeyword = string.Empty;

        foreach (var keyword in keywords)
        {
            var keywordScore = 0d;
            if (ContainsCatalogKeyword(title, keyword))
            {
                keywordScore += 1.8d;
            }

            if (ContainsCatalogKeyword(tags, keyword))
            {
                keywordScore += 1.5d;
            }

            if (ContainsCatalogKeyword(content, keyword))
            {
                keywordScore += 1.2d;
            }

            if (keywordScore <= 0d)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(primaryKeyword))
            {
                primaryKeyword = keyword;
            }

            keywordHitCount++;
            score += keywordScore;
        }

        if (keywordHitCount == 0)
        {
            return null;
        }

        var sendCount = _phraseCatalog.GetPhraseSendCount(phrase.Id);
        var usageBoost = Math.Log2(sendCount + 1) * 0.09d;
        var recentBoost = ComputePhraseRecommendationRecentBoost(
            _phraseCatalog.GetPhraseLastSentAt(phrase.Id),
            rankingReferenceTime) * 1.8d;
        score += usageBoost + recentBoost;

        var confidence = Math.Min(0.88d, 0.28d + score * 0.11d);
        if (confidence < 0.33d)
        {
            return null;
        }

        return new CatalogFallbackEntry(
            phrase,
            primaryKeyword,
            keywordHitCount,
            sendCount,
            confidence,
            score);
    }

    private static bool ContainsCatalogKeyword(string text, string keyword)
    {
        return !string.IsNullOrWhiteSpace(text) &&
               !string.IsNullOrWhiteSpace(keyword) &&
               text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCatalogText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim();
    }

    private sealed record CatalogFallbackEntry(
        ReplyPhrase Phrase,
        string PrimaryKeyword,
        int KeywordHitCount,
        int SendCount,
        double Confidence,
        double Score);

}
