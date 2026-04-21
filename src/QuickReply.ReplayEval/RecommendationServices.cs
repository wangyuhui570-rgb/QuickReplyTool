using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal interface IRecommendationPhraseCatalog
{
    IReadOnlyList<ReplyPhrase> GetAllPhrases();

    int GetPhraseSendCount(Guid phraseId);

    DateTimeOffset? GetPhraseLastSentAt(Guid phraseId);
}

internal interface IPublishedKnowledgeCardCatalog
{
    IReadOnlyList<PublishedKnowledgeCard> GetAllPublishedKnowledgeCards();
}

internal interface ICandidateRetriever
{
    Task<CandidateRetrievalResult> RetrieveCandidatesAsync(
        ConversationRecommendationSnapshot snapshot,
        RecommendationRequest request);
}

internal interface IReplyGenerator
{
    Task<IReadOnlyList<GeneratedReplyCandidate>> GenerateCandidatesAsync(
        ConversationRecommendationSnapshot snapshot,
        CandidateRetrievalResult retrievalResult,
        RecommendationRequest request);
}

internal interface IRecommendationQueryService
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

internal sealed class InMemoryRecommendationPhraseCatalog : IRecommendationPhraseCatalog
{
    private readonly IReadOnlyList<ReplyPhrase> _phrases;
    private readonly IReadOnlyDictionary<Guid, PhraseUsageStat> _usageStats;

    public InMemoryRecommendationPhraseCatalog(
        IReadOnlyList<ReplyPhrase> phrases,
        IReadOnlyDictionary<Guid, PhraseUsageStat> usageStats)
    {
        _phrases = phrases;
        _usageStats = usageStats;
    }

    public IReadOnlyList<ReplyPhrase> GetAllPhrases() => _phrases;

    public int GetPhraseSendCount(Guid phraseId) =>
        _usageStats.TryGetValue(phraseId, out var stat) ? stat.SendCount : 0;

    public DateTimeOffset? GetPhraseLastSentAt(Guid phraseId) =>
        _usageStats.TryGetValue(phraseId, out var stat) ? stat.LastSentAt : null;
}

internal sealed class InMemoryPublishedKnowledgeCardCatalog : IPublishedKnowledgeCardCatalog
{
    private readonly IReadOnlyList<PublishedKnowledgeCard> _cards;

    public InMemoryPublishedKnowledgeCardCatalog(IReadOnlyList<PublishedKnowledgeCard> cards)
    {
        _cards = cards;
    }

    public IReadOnlyList<PublishedKnowledgeCard> GetAllPublishedKnowledgeCards() => _cards;
}

internal sealed class RetrievalBackedReplyGenerator : IReplyGenerator
{
    public Task<IReadOnlyList<GeneratedReplyCandidate>> GenerateCandidatesAsync(
        ConversationRecommendationSnapshot snapshot,
        CandidateRetrievalResult retrievalResult,
        RecommendationRequest request)
    {
        var generated = retrievalResult.Suggestions
            .Where(item => !string.IsNullOrWhiteSpace(item.Phrase.Content))
            .GroupBy(item => item.Phrase.Content, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(Math.Max(1, request.GeneratedReplyCount))
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

internal sealed class HistoricalCandidateRetriever : ICandidateRetriever
{
    private readonly IRecommendationQueryService _recommendationQueryService;
    private readonly IRecommendationPhraseCatalog _phraseCatalog;
    private readonly IPublishedKnowledgeCardCatalog _knowledgeCardCatalog;

    public HistoricalCandidateRetriever(
        IRecommendationQueryService recommendationQueryService,
        IRecommendationPhraseCatalog phraseCatalog,
        IPublishedKnowledgeCardCatalog knowledgeCardCatalog)
    {
        _recommendationQueryService = recommendationQueryService;
        _phraseCatalog = phraseCatalog;
        _knowledgeCardCatalog = knowledgeCardCatalog;
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

        var knowledgeCards = BuildKnowledgeCardSuggestions(snapshot, displayCount, rankingReferenceTime);

        return new CandidateRetrievalResult
        {
            Suggestions = suggestions,
            KnowledgeCards = knowledgeCards,
            SimilarReplyGroups = similarReplyGroups
        };
    }

    private IReadOnlyList<KnowledgeCardSuggestion> BuildKnowledgeCardSuggestions(
        ConversationRecommendationSnapshot snapshot,
        int displayCount,
        DateTimeOffset rankingReferenceTime)
    {
        var keywords = snapshot.Keywords.Where(keyword => !string.IsNullOrWhiteSpace(keyword)).Take(12).ToArray();
        if (keywords.Length == 0)
        {
            return [];
        }

        var platformHint = NormalizeCatalogText(snapshot.PlatformHint);
        var productKeySource = string.Join(
            " ",
            new[]
            {
                NormalizeCatalogText(snapshot.RecognizedProductId),
                NormalizeCatalogText(snapshot.RecognizedProductName),
                NormalizeCatalogText(snapshot.RecognizedContextText),
                NormalizeCatalogText(snapshot.RecognizedCustomerText)
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return _knowledgeCardCatalog
            .GetAllPublishedKnowledgeCards()
            .Select(card => BuildKnowledgeCardEntry(card, platformHint, productKeySource, keywords, rankingReferenceTime))
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .OrderByDescending(entry => entry.Score)
            .ThenByDescending(entry => entry.MatchCount)
            .ThenByDescending(entry => entry.Card.UpdatedAt)
            .Take(displayCount)
            .Select((entry, index) => new KnowledgeCardSuggestion
            {
                Card = entry.Card,
                DisplayRank = index + 1,
                MatchedKeyword = entry.PrimaryKeyword,
                MatchCount = entry.MatchCount,
                Confidence = entry.Confidence
            })
            .ToArray();
    }

    private CatalogKnowledgeCardEntry? BuildKnowledgeCardEntry(
        PublishedKnowledgeCard card,
        string platformHint,
        string productKeySource,
        IReadOnlyList<string> keywords,
        DateTimeOffset rankingReferenceTime)
    {
        if (!IsKnowledgeCardPlatformMatch(card.Platform, platformHint))
        {
            return null;
        }

        var normalizedQuestion = NormalizeCatalogText(card.Question);
        var normalizedAnswer = NormalizeCatalogText(card.Answer);
        var normalizedUsageNotes = NormalizeCatalogText(card.UsageNotes);
        var normalizedTags = string.Join(" ", card.Tags.Select(NormalizeCatalogText));
        var normalizedSynonyms = string.Join(" ", card.Synonyms.Select(NormalizeCatalogText));

        var score = 0d;
        var matchCount = 0;
        var primaryKeyword = string.Empty;

        foreach (var keyword in keywords)
        {
            var keywordScore = 0d;
            if (ContainsCatalogKeyword(normalizedQuestion, keyword))
            {
                keywordScore += 1.8d;
            }

            if (ContainsCatalogKeyword(normalizedSynonyms, keyword))
            {
                keywordScore += 1.4d;
            }

            if (ContainsCatalogKeyword(normalizedTags, keyword))
            {
                keywordScore += 1.2d;
            }

            if (ContainsCatalogKeyword(normalizedAnswer, keyword))
            {
                keywordScore += 0.8d;
            }

            if (ContainsCatalogKeyword(normalizedUsageNotes, keyword))
            {
                keywordScore += 0.5d;
            }

            if (keywordScore <= 0d)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(primaryKeyword))
            {
                primaryKeyword = keyword;
            }

            matchCount++;
            score += keywordScore;
        }

        if (matchCount == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(card.ProductKey) &&
            !string.IsNullOrWhiteSpace(productKeySource) &&
            productKeySource.Contains(card.ProductKey, StringComparison.OrdinalIgnoreCase))
        {
            score += 1.8d;
        }

        score += Math.Max(0d, (14d - Math.Max(0d, (rankingReferenceTime - card.UpdatedAt).TotalDays)) * 0.02d);

        var confidence = Math.Min(0.92d, 0.32d + score * 0.10d);
        return confidence < 0.40d
            ? null
            : new CatalogKnowledgeCardEntry(card, primaryKeyword, matchCount, confidence, score);
    }

    private static bool IsKnowledgeCardPlatformMatch(string cardPlatform, string requestPlatform)
    {
        var normalizedCardPlatform = NormalizeCatalogText(cardPlatform);
        if (string.IsNullOrWhiteSpace(normalizedCardPlatform))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(requestPlatform) || string.Equals(requestPlatform, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(normalizedCardPlatform, requestPlatform, StringComparison.OrdinalIgnoreCase);
    }

    private static double ComputePhraseRecommendationRecentBoost(DateTimeOffset? lastSentAt, DateTimeOffset referenceTime)
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
        var keywords = snapshot.Keywords.Where(keyword => !string.IsNullOrWhiteSpace(keyword)).Take(12).ToArray();
        if (keywords.Length == 0)
        {
            return [];
        }

        return _phraseCatalog.GetAllPhrases()
            .Where(phrase => !string.IsNullOrWhiteSpace(phrase.Content) || !string.IsNullOrWhiteSpace(phrase.Title))
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
        score += Math.Log2(sendCount + 1) * 0.09d;
        score += ComputePhraseRecommendationRecentBoost(
            _phraseCatalog.GetPhraseLastSentAt(phrase.Id),
            rankingReferenceTime) * 1.8d;

        var confidence = Math.Min(0.88d, 0.28d + score * 0.11d);
        return confidence < 0.33d
            ? null
            : new CatalogFallbackEntry(phrase, primaryKeyword, keywordHitCount, sendCount, confidence, score);
    }

    private static bool ContainsCatalogKeyword(string text, string keyword) =>
        !string.IsNullOrWhiteSpace(text) &&
        !string.IsNullOrWhiteSpace(keyword) &&
        text.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCatalogText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private sealed record CatalogFallbackEntry(
        ReplyPhrase Phrase,
        string PrimaryKeyword,
        int KeywordHitCount,
        int SendCount,
        double Confidence,
        double Score);

    private sealed record CatalogKnowledgeCardEntry(
        PublishedKnowledgeCard Card,
        string PrimaryKeyword,
        int MatchCount,
        double Confidence,
        double Score);
}

internal sealed class ReplayRecommendationQueryService : IRecommendationQueryService, IAsyncDisposable
{
    private readonly string _connectionString;

    public ReplayRecommendationQueryService(string logDatabasePath)
    {
        _connectionString = $"Data Source={logDatabasePath}";
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<IReadOnlyList<PhraseRecommendationCandidate>> GetPhraseRecommendationsAsync(
        ConversationRecommendationSnapshot snapshot,
        int maxResults = 5,
        RecommendationQueryScope? queryScope = null)
    {
        if (snapshot is null ||
            snapshot.RecognitionConfidence < 0.20d ||
            snapshot.Keywords.Count == 0 ||
            (string.IsNullOrWhiteSpace(snapshot.RecognizedCustomerText) &&
             string.IsNullOrWhiteSpace(snapshot.RecognizedContextText) &&
             !snapshot.Product.HasValue))
        {
            return [];
        }

        var candidates = new Dictionary<Guid, PhraseRecommendationCandidate>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        foreach (var keyword in snapshot.Keywords.Take(12))
        {
            var sampleCount = await GetRecommendationSampleCountAsync(connection, snapshot.PlatformHint, keyword, queryScope).ConfigureAwait(false);
            if (sampleCount < 3)
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT phrase_id,
                       phrase_title,
                       phrase_category_name,
                       COUNT(1) AS hit_count
                FROM conversation_logs
                WHERE payload_type = 'phrase'
                  AND result_success = 1
                  AND action_mode IN ('Send', 'Input')
                  AND phrase_id <> ''
                  AND recognition_confidence >= 0.2
                  AND (
                        recognized_customer_text LIKE $keywordLike
                     OR recognized_context_text LIKE $keywordLike
                     OR recognized_product_id LIKE $keywordLike
                     OR recognized_product_name LIKE $keywordLike
                  )
                  AND ($platformHint = '' OR platform_hint = $platformHint)
                  AND ($maxOccurredAt = '' OR occurred_at <= $maxOccurredAt)
                  AND ($excludedConversationLogId = '' OR id <> $excludedConversationLogId)
                GROUP BY phrase_id, phrase_title, phrase_category_name
                ORDER BY hit_count DESC
                LIMIT 8;
                """;
            command.Parameters.AddWithValue("$keywordLike", $"%{keyword}%");
            command.Parameters.AddWithValue(
                "$platformHint",
                string.IsNullOrWhiteSpace(snapshot.PlatformHint) || snapshot.PlatformHint == "unknown"
                    ? string.Empty
                    : snapshot.PlatformHint);
            BindRecommendationQueryScope(command, queryScope);

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (!Guid.TryParse(reader.GetString(0), out var phraseId))
                {
                    continue;
                }

                var matchCount = reader.GetInt32(3);
                var confidence = sampleCount <= 0 ? 0d : (double)matchCount / sampleCount;
                if (!ShouldKeepRecommendation(sampleCount, confidence))
                {
                    continue;
                }

                if (!candidates.TryGetValue(phraseId, out var current) ||
                    confidence > current.Confidence ||
                    (Math.Abs(confidence - current.Confidence) < 0.0001d && matchCount > current.MatchCount))
                {
                    candidates[phraseId] = new PhraseRecommendationCandidate
                    {
                        PhraseId = phraseId,
                        PhraseTitle = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        PhraseCategoryName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        MatchedKeyword = keyword,
                        MatchCount = matchCount,
                        SampleCount = sampleCount,
                        Confidence = confidence,
                        RecognizedCustomerText = snapshot.RecognizedCustomerText
                    };
                }
            }
        }

        return candidates.Values
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenByDescending(candidate => candidate.MatchCount)
            .ThenByDescending(candidate => candidate.SampleCount)
            .Take(maxResults)
            .ToList();
    }

    public async Task<IReadOnlyList<HistoricalReplyGroupSuggestion>> GetSimilarReplyGroupsAsync(
        ConversationRecommendationSnapshot snapshot,
        int maxResults = 3,
        RecommendationQueryScope? queryScope = null)
    {
        var sourceText = BuildSearchableRecommendationText(snapshot);
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return [];
        }

        var keywords = snapshot.Keywords.Count > 0
            ? snapshot.Keywords.Take(10).ToList()
            : RecommendationKeywordBuilder.Build(
                snapshot.RecognizedCustomerText,
                snapshot.RecognizedContextText,
                snapshot.RecognizedProductId,
                snapshot.RecognizedProductName).Take(10).ToList();
        if (keywords.Count == 0)
        {
            return [];
        }

        var groups = new Dictionary<Guid, ReplyGroupAccumulator>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT reply_group_id,
                   occurred_at,
                   recognized_customer_text,
                   recognized_context_text,
                   recognized_product_id,
                   recognized_product_name,
                   payload_text,
                   phrase_title,
                   phrase_category_name,
                   data_quality_grade
            FROM conversation_logs
            WHERE payload_type = 'phrase'
              AND result_success = 1
              AND action_mode IN ('Send', 'Input')
              AND reply_group_id <> ''
              AND (
                    recognized_customer_text <> ''
                 OR recognized_context_text <> ''
                 OR recognized_product_id <> ''
                 OR recognized_product_name <> ''
              )
              AND ($platformHint = '' OR platform_hint = $platformHint)
              AND ($maxOccurredAt = '' OR occurred_at <= $maxOccurredAt)
              AND ($excludedConversationLogId = '' OR id <> $excludedConversationLogId)
            ORDER BY occurred_at DESC
            LIMIT 600;
            """;
        command.Parameters.AddWithValue(
            "$platformHint",
            string.IsNullOrWhiteSpace(snapshot.PlatformHint) || snapshot.PlatformHint == "unknown"
                ? string.Empty
                : snapshot.PlatformHint);
        BindRecommendationQueryScope(command, queryScope);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var replyGroupIdText = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (!Guid.TryParse(replyGroupIdText, out var replyGroupId))
            {
                continue;
            }

            var searchableText = BuildSearchableRecommendationText(
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5));
            if (!LooksRelevantToKeywords(searchableText, keywords))
            {
                continue;
            }

            var matchScore = ComputeKeywordMatchScore(searchableText, keywords);
            if (matchScore <= 0d)
            {
                continue;
            }

            if (!groups.TryGetValue(replyGroupId, out var group))
            {
                group = new ReplyGroupAccumulator
                {
                    ReplyGroupId = replyGroupId,
                    RecognizedCustomerText = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    LastOccurredAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    QualityGrade = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                    MatchScore = matchScore
                };
                groups[replyGroupId] = group;
            }

            group.MatchScore = Math.Max(group.MatchScore, matchScore);
            group.Replies.Add(BuildReplyPreview(
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6)));
        }

        return groups.Values
            .OrderByDescending(group => group.MatchScore)
            .ThenByDescending(group => group.LastOccurredAt)
            .Take(maxResults)
            .Select(group => new HistoricalReplyGroupSuggestion
            {
                ReplyGroupId = group.ReplyGroupId,
                RecognizedCustomerText = group.RecognizedCustomerText,
                ReplyCount = group.Replies.Count,
                RepliesPreviewText = string.Join("  ->  ", group.Replies.Take(3)),
                LastOccurredAt = group.LastOccurredAt,
                MatchScore = group.MatchScore,
                QualityGrade = group.QualityGrade,
                MatchReasonText = $"历史相似问题，关键词匹配度 {Math.Round(group.MatchScore * 100):0}%"
            })
            .ToList();
    }

    private static async Task<int> GetRecommendationSampleCountAsync(
        SqliteConnection connection,
        string platformHint,
        string keyword,
        RecommendationQueryScope? queryScope)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM conversation_logs
            WHERE recognition_confidence >= 0.2
              AND (
                    recognized_customer_text LIKE $keywordLike
                 OR recognized_context_text LIKE $keywordLike
                 OR recognized_product_id LIKE $keywordLike
                 OR recognized_product_name LIKE $keywordLike
              )
              AND ($platformHint = '' OR platform_hint = $platformHint)
              AND ($maxOccurredAt = '' OR occurred_at <= $maxOccurredAt)
              AND ($excludedConversationLogId = '' OR id <> $excludedConversationLogId);
            """;
        command.Parameters.AddWithValue("$keywordLike", $"%{keyword}%");
        command.Parameters.AddWithValue(
            "$platformHint",
            string.IsNullOrWhiteSpace(platformHint) || platformHint == "unknown"
                ? string.Empty
                : platformHint);
        BindRecommendationQueryScope(command, queryScope);

        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false) ?? 0, CultureInfo.InvariantCulture);
    }

    private static void BindRecommendationQueryScope(SqliteCommand command, RecommendationQueryScope? queryScope)
    {
        command.Parameters.AddWithValue(
            "$maxOccurredAt",
            queryScope?.MaxOccurredAt is null
                ? string.Empty
                : queryScope.MaxOccurredAt.Value.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$excludedConversationLogId",
            NormalizeOptionalValue(queryScope?.ExcludedConversationLogId));
    }

    private static bool ShouldKeepRecommendation(int sampleCount, double confidence)
    {
        if (sampleCount >= 20)
        {
            return confidence >= 0.35d;
        }

        if (sampleCount >= 8)
        {
            return confidence >= 0.45d;
        }

        return confidence >= 0.60d;
    }

    private static string BuildSearchableRecommendationText(ConversationRecommendationSnapshot snapshot) =>
        BuildSearchableRecommendationText(
            snapshot.RecognizedCustomerText,
            snapshot.RecognizedContextText,
            snapshot.RecognizedProductId,
            snapshot.RecognizedProductName);

    private static string BuildSearchableRecommendationText(
        string customerText,
        string contextText,
        string productId,
        string productName)
    {
        return string.Join(
            Environment.NewLine,
            new[]
            {
                NormalizeOptionalValue(customerText),
                NormalizeOptionalValue(contextText),
                NormalizeOptionalValue(productId),
                NormalizeOptionalValue(productName)
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static bool LooksRelevantToKeywords(string customerText, IReadOnlyList<string> keywords) =>
        keywords.Any(keyword => customerText.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static double ComputeKeywordMatchScore(string customerText, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(customerText) || keywords.Count == 0)
        {
            return 0d;
        }

        var hits = keywords.Count(keyword => customerText.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return (double)hits / keywords.Count;
    }

    private static string BuildReplyPreview(string phraseTitle, string categoryName, string payloadText)
    {
        var title = NormalizeOptionalValue(phraseTitle);
        var content = NormalizeOptionalValue(payloadText).Replace("\r", " ").Replace("\n", " ");
        if (content.Length > 24)
        {
            content = $"{content[..24]}...";
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            return string.IsNullOrWhiteSpace(categoryName)
                ? $"{title}：{content}"
                : $"{categoryName}/{title}：{content}";
        }

        return content;
    }

    private static string NormalizeOptionalValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        return normalized;
    }

    private sealed class ReplyGroupAccumulator
    {
        public Guid ReplyGroupId { get; init; }

        public string RecognizedCustomerText { get; init; } = string.Empty;

        public DateTimeOffset LastOccurredAt { get; init; }

        public string QualityGrade { get; init; } = string.Empty;

        public double MatchScore { get; set; }

        public List<string> Replies { get; } = [];
    }
}
