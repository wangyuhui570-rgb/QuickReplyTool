using System.Text;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal static class KnowledgeReviewQueueGrouper
{
    public static IReadOnlyList<KnowledgeReviewQueueGroup> Group(IReadOnlyList<KnowledgeReviewQueueItem> items)
    {
        if (items.Count == 0)
        {
            return [];
        }

        return items
            .GroupBy(BuildGroupKey)
            .Select(CreateGroup)
            .OrderByDescending(group => group.Primary.Confidence)
            .ThenByDescending(group => group.Primary.UpdatedAt)
            .ToArray();
    }

    private static KnowledgeReviewQueueGroup CreateGroup(IGrouping<string, KnowledgeReviewQueueItem> group)
    {
        var members = group
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.UpdatedAt)
            .ToArray();
        var primary = members[0];
        var sourceCount = members.Sum(item => item.SourceCount);
        var platforms = members
            .Select(item => item.Platform)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var products = members
            .Select(item => item.ProductKey)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new KnowledgeReviewQueueGroup(
            primary,
            members,
            sourceCount,
            platforms,
            products);
    }

    private static string BuildGroupKey(KnowledgeReviewQueueItem item)
    {
        return string.Join("|",
            item.CandidateKind.Trim().ToLowerInvariant(),
            NormalizeForGrouping(item.Platform),
            NormalizeForGrouping(item.ProductKey),
            NormalizeForGrouping(item.QuestionNormalized),
            NormalizeForGrouping(item.AnswerDraft));
    }

    private static string NormalizeForGrouping(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch))
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}

public sealed record KnowledgeReviewQueueGroup(
    KnowledgeReviewQueueItem Primary,
    IReadOnlyList<KnowledgeReviewQueueItem> Members,
    int CombinedSourceCount,
    IReadOnlyList<string> Platforms,
    IReadOnlyList<string> ProductKeys);
