namespace QuickReply.Desktop.Models;

public sealed class HistoricalReplyGroupSuggestion
{
    public Guid ReplyGroupId { get; init; }

    public string RecognizedCustomerText { get; init; } = string.Empty;

    public string RepliesPreviewText { get; init; } = string.Empty;

    public int ReplyCount { get; init; }

    public DateTimeOffset LastOccurredAt { get; init; }

    public double MatchScore { get; init; }

    public string QualityGrade { get; init; } = string.Empty;

    public string MatchReasonText { get; init; } = string.Empty;

    public string LastOccurredAtText => LastOccurredAt == default
        ? string.Empty
        : LastOccurredAt.ToLocalTime().ToString("MM-dd HH:mm");
}
