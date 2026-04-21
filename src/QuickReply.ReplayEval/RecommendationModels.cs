namespace QuickReply.Desktop.Models;

public sealed class ReplyPhrase
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid CategoryId { get; init; }

    public string CategoryName { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Tags { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public int SortOrder { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed class PhraseUsageStat
{
    public Guid PhraseId { get; init; }

    public int SendCount { get; init; }

    public DateTimeOffset? LastSentAt { get; init; }
}

public sealed class PhraseRecommendationCandidate
{
    public Guid PhraseId { get; init; }

    public string PhraseTitle { get; init; } = string.Empty;

    public string PhraseCategoryName { get; init; } = string.Empty;

    public string MatchedKeyword { get; init; } = string.Empty;

    public int MatchCount { get; init; }

    public int SampleCount { get; init; }

    public double Confidence { get; init; }

    public string RecognizedCustomerText { get; init; } = string.Empty;
}

public sealed class CandidateRetrievalResult
{
    public IReadOnlyList<PhraseRecommendationSuggestion> Suggestions { get; init; } = [];

    public IReadOnlyList<KnowledgeCardSuggestion> KnowledgeCards { get; init; } = [];

    public IReadOnlyList<HistoricalReplyGroupSuggestion> SimilarReplyGroups { get; init; } = [];
}

public sealed class PhraseRecommendationSuggestion
{
    public const string HistoricalSource = "historical";
    public const string CatalogFallbackSource = "catalog_fallback";

    public required ReplyPhrase Phrase { get; init; }

    public int DisplayRank { get; init; }

    public string MatchedKeyword { get; init; } = string.Empty;

    public int MatchCount { get; init; }

    public int SampleCount { get; init; }

    public double Confidence { get; init; }

    public string RecognizedCustomerText { get; init; } = string.Empty;

    public string RecommendationSource { get; init; } = HistoricalSource;

    public bool IsHistoricalSource => string.Equals(RecommendationSource, HistoricalSource, StringComparison.OrdinalIgnoreCase);

    public bool IsCatalogFallbackSource => string.Equals(RecommendationSource, CatalogFallbackSource, StringComparison.OrdinalIgnoreCase);

    public string ReasonText
    {
        get
        {
            if (RecommendationSource == CatalogFallbackSource)
            {
                return string.IsNullOrWhiteSpace(MatchedKeyword)
                    ? "当前历史样本不足，先按短语库内容匹配给出建议。"
                    : $"当前历史样本不足，先按短语库关键词“{MatchedKeyword}”给出建议。";
            }

            if (SampleCount <= 0)
            {
                return string.IsNullOrWhiteSpace(MatchedKeyword)
                    ? $"历史高频回复，命中率 {Confidence:P0}"
                    : $"命中关键词“{MatchedKeyword}”，历史命中率 {Confidence:P0}";
            }

            return string.IsNullOrWhiteSpace(MatchedKeyword)
                ? $"在 {SampleCount} 次相似问题里，有 {MatchCount} 次发送这条"
                : $"命中关键词“{MatchedKeyword}”，在 {SampleCount} 次相似问题里有 {MatchCount} 次发送这条";
        }
    }
}
