namespace QuickReply.Desktop.Models;

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

    public string SourceBadgeText => IsCatalogFallbackSource ? "短语库匹配" : "历史高频";

    public string RecommendationKind => IsCatalogFallbackSource ? "catalog_phrase" : "historical_phrase";

    public string ConfidenceText => $"{Math.Round(Confidence * 100):0}%";

    public string SummaryText => string.IsNullOrWhiteSpace(MatchedKeyword)
        ? RecommendationSource == CatalogFallbackSource
            ? $"短语库匹配 | 推荐 {ConfidenceText}"
            : $"推荐 {ConfidenceText}"
        : RecommendationSource == CatalogFallbackSource
            ? $"命中短语库“{MatchedKeyword}” | 推荐 {ConfidenceText}"
            : $"命中“{MatchedKeyword}” | 推荐 {ConfidenceText}";

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
                    ? $"历史高频回复，命中率 {ConfidenceText}"
                    : $"命中关键词“{MatchedKeyword}”，历史命中率 {ConfidenceText}";
            }

            return string.IsNullOrWhiteSpace(MatchedKeyword)
                ? $"在 {SampleCount} 次相似问题里，有 {MatchCount} 次发送这条"
                : $"命中关键词“{MatchedKeyword}”，在 {SampleCount} 次相似问题里有 {MatchCount} 次发送这条";
        }
    }

    public string CustomerPreviewText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(RecognizedCustomerText))
            {
                return string.Empty;
            }

            var text = RecognizedCustomerText.Trim();
            return text.Length <= 48 ? text : $"{text[..48]}...";
        }
    }
}
