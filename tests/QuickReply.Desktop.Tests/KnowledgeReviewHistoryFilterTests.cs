using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class KnowledgeReviewHistoryFilterTests
{
    [Fact]
    public void BuildStats_CountsEachActionType()
    {
        var records = CreateHistoryRecords();

        var stats = KnowledgeReviewHistoryFilter.BuildStats(records);

        Assert.Equal(4, stats.TotalCount);
        Assert.Equal(2, stats.ApprovedCount);
        Assert.Equal(1, stats.RejectedCount);
        Assert.Equal(1, stats.UnpublishedCount);
    }

    [Fact]
    public void Apply_FiltersApprovedRecords()
    {
        var records = CreateHistoryRecords();

        var filtered = KnowledgeReviewHistoryFilter.Apply(records, KnowledgeReviewHistoryFilter.Approved);

        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, item => Assert.Equal("approved", item.ActionType?.ToLowerInvariant()));
    }

    [Fact]
    public void Apply_UnknownFilterFallsBackToAll()
    {
        var records = CreateHistoryRecords();

        var filtered = KnowledgeReviewHistoryFilter.Apply(records, "unknown-filter");

        Assert.Equal(records.Count, filtered.Count);
    }

    private static IReadOnlyList<KnowledgeReviewHistoryEventRecord> CreateHistoryRecords()
    {
        return
        [
            new KnowledgeReviewHistoryEventRecord
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                CandidateId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                ActionType = "approved",
                CreatedAt = DateTimeOffset.Parse("2026-04-17T10:00:00+08:00")
            },
            new KnowledgeReviewHistoryEventRecord
            {
                Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                CandidateId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                ActionType = "rejected",
                CreatedAt = DateTimeOffset.Parse("2026-04-17T11:00:00+08:00")
            },
            new KnowledgeReviewHistoryEventRecord
            {
                Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                CandidateId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                ActionType = "unpublished",
                CreatedAt = DateTimeOffset.Parse("2026-04-17T12:00:00+08:00")
            },
            new KnowledgeReviewHistoryEventRecord
            {
                Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                CandidateId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                ActionType = "approved",
                CreatedAt = DateTimeOffset.Parse("2026-04-17T13:00:00+08:00")
            }
        ];
    }
}
