namespace QuickReply.Desktop.Models;

public sealed class PhraseUsageReportItem
{
    public int Rank { get; init; }

    public Guid PhraseId { get; init; }

    public string CategoryName { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string ContentPreview { get; init; } = string.Empty;

    public int SendCount { get; init; }

    public DateTimeOffset? LastSentAt { get; init; }
}
