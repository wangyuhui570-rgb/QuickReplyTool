namespace QuickReply.Desktop.Models;

public sealed class KnowledgeCardSuggestion
{
    public required PublishedKnowledgeCard Card { get; init; }

    public int DisplayRank { get; init; }

    public string MatchedKeyword { get; init; } = string.Empty;

    public int MatchCount { get; init; }

    public double Confidence { get; init; }

    public bool IsResourceCard => string.Equals(Card.CandidateKind, "resource", StringComparison.OrdinalIgnoreCase);

    public string SourceBadgeText => IsResourceCard ? "资料卡" : "问答卡";

    public string RecommendationKind => IsResourceCard ? "knowledge_card_resource" : "knowledge_card_qa";

    public string SummaryText => string.IsNullOrWhiteSpace(Card.ProductKey)
        ? Card.Question
        : $"{Card.Question} | {Card.ProductKey}";

    public string ReasonText
    {
        get
        {
            var cardType = IsResourceCard ? "资料卡" : "问答卡";
            var prefix = string.IsNullOrWhiteSpace(MatchedKeyword)
                ? $"已发布{cardType}命中"
                : $"命中{cardType}关键词“{MatchedKeyword}”";

            if (Card.HasProductKey)
            {
                return $"{prefix}，适用商品 {Card.ProductKey}";
            }

            return prefix;
        }
    }
}
