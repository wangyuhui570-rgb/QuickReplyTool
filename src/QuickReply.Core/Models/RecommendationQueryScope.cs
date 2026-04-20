namespace QuickReply.Desktop.Models;

public sealed class RecommendationQueryScope
{
    public DateTimeOffset? MaxOccurredAt { get; init; }

    public string ExcludedConversationLogId { get; init; } = string.Empty;
}
