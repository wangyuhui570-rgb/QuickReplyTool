namespace QuickReply.Desktop.Models;

public sealed class UsageDashboardStats
{
    public int LaunchCount { get; init; }

    public long TotalRuntimeSeconds { get; init; }

    public IReadOnlyList<PhraseUsageDashboardItem> MostUsedPhrases { get; init; } = [];

    public IReadOnlyList<PhraseUsageDashboardItem> LeastUsedPhrases { get; init; } = [];
}

public sealed class PhraseUsageDashboardItem
{
    public required Guid PhraseId { get; init; }

    public required string CategoryName { get; init; }

    public required string Title { get; init; }

    public required string ContentPreview { get; init; }

    public int SendCount { get; init; }

    public DateTimeOffset? LastSentAt { get; init; }
}
