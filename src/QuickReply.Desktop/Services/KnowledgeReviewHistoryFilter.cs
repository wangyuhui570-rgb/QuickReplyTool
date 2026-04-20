using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal static class KnowledgeReviewHistoryFilter
{
    public const string All = "all";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Unpublished = "unpublished";

    public static IReadOnlyList<KnowledgeReviewHistoryEventRecord> Apply(
        IReadOnlyList<KnowledgeReviewHistoryEventRecord> records,
        string filterKey)
    {
        return NormalizeFilterKey(filterKey) switch
        {
            Approved => records.Where(item => IsAction(item, Approved)).ToArray(),
            Rejected => records.Where(item => IsAction(item, Rejected)).ToArray(),
            Unpublished => records.Where(item => IsAction(item, Unpublished)).ToArray(),
            _ => records.ToArray()
        };
    }

    public static KnowledgeReviewHistoryStats BuildStats(IReadOnlyList<KnowledgeReviewHistoryEventRecord> records)
    {
        return new KnowledgeReviewHistoryStats(
            records.Count,
            records.Count(item => IsAction(item, Approved)),
            records.Count(item => IsAction(item, Rejected)),
            records.Count(item => IsAction(item, Unpublished)));
    }

    public static string NormalizeFilterKey(string? filterKey)
    {
        if (string.IsNullOrWhiteSpace(filterKey))
        {
            return All;
        }

        var normalized = filterKey.Trim().ToLowerInvariant();
        return normalized switch
        {
            Approved or Rejected or Unpublished => normalized,
            _ => All
        };
    }

    private static bool IsAction(KnowledgeReviewHistoryEventRecord record, string actionType)
    {
        return string.Equals(
            record.ActionType?.Trim(),
            actionType,
            StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record KnowledgeReviewHistoryStats(
    int TotalCount,
    int ApprovedCount,
    int RejectedCount,
    int UnpublishedCount);
