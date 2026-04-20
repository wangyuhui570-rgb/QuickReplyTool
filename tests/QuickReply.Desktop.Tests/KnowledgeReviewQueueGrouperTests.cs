using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class KnowledgeReviewQueueGrouperTests
{
    [Fact]
    public void Group_MergesNearDuplicateCandidatesWithinSamePlatformAndProduct()
    {
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var groups = KnowledgeReviewQueueGrouper.Group(
        [
            new KnowledgeReviewQueueItem
            {
                Id = firstId,
                CandidateKind = "resource",
                Status = "reviewed",
                Platform = "qianniu",
                ProductKey = "SKU-1",
                QuestionNormalized = "智骑软件",
                AnswerDraft = "智骑软件下载地址：https://example.com/app",
                Tags = ["资料"],
                Confidence = 0.61,
                SourceCount = 3,
                UpdatedAt = DateTimeOffset.Parse("2026-04-16T10:00:00+08:00")
            },
            new KnowledgeReviewQueueItem
            {
                Id = secondId,
                CandidateKind = "resource",
                Status = "reviewed",
                Platform = "qianniu",
                ProductKey = "SKU-1",
                QuestionNormalized = "智骑软件",
                AnswerDraft = "智骑软件下载地址 https://example.com/app",
                Tags = ["资料"],
                Confidence = 0.53,
                SourceCount = 2,
                UpdatedAt = DateTimeOffset.Parse("2026-04-16T09:00:00+08:00")
            }
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(firstId, group.Primary.Id);
        Assert.Equal(2, group.Members.Count);
        Assert.Equal(5, group.CombinedSourceCount);
    }

    [Fact]
    public void Group_DoesNotMergeCandidatesAcrossPlatforms_WhenQuestionAndAnswerMatch()
    {
        var groups = KnowledgeReviewQueueGrouper.Group(
        [
            new KnowledgeReviewQueueItem
            {
                Id = Guid.NewGuid(),
                CandidateKind = "resource",
                Status = "reviewed",
                Platform = "qianniu",
                QuestionNormalized = "锁片安装",
                AnswerDraft = "锁片安装视频：https://example.com/1",
                Tags = ["资料"],
                Confidence = 0.61,
                SourceCount = 3,
                UpdatedAt = DateTimeOffset.Parse("2026-04-16T10:00:00+08:00")
            },
            new KnowledgeReviewQueueItem
            {
                Id = Guid.NewGuid(),
                CandidateKind = "resource",
                Status = "reviewed",
                Platform = "pdd_workbench",
                QuestionNormalized = "锁片安装",
                AnswerDraft = "锁片安装视频：https://example.com/1",
                Tags = ["资料"],
                Confidence = 0.58,
                SourceCount = 2,
                UpdatedAt = DateTimeOffset.Parse("2026-04-16T09:00:00+08:00")
            }
        ]);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Single(group.Members));
    }

    [Fact]
    public void Group_DoesNotMergeCandidatesAcrossProducts_WhenQuestionAndAnswerMatch()
    {
        var groups = KnowledgeReviewQueueGrouper.Group(
        [
            new KnowledgeReviewQueueItem
            {
                Id = Guid.NewGuid(),
                CandidateKind = "resource",
                Status = "reviewed",
                Platform = "qianniu",
                ProductKey = "SKU-1",
                QuestionNormalized = "锁片安装",
                AnswerDraft = "锁片安装视频：https://example.com/1",
                Tags = ["资料"],
                Confidence = 0.61,
                SourceCount = 3,
                UpdatedAt = DateTimeOffset.Parse("2026-04-16T10:00:00+08:00")
            },
            new KnowledgeReviewQueueItem
            {
                Id = Guid.NewGuid(),
                CandidateKind = "resource",
                Status = "reviewed",
                Platform = "qianniu",
                ProductKey = "SKU-2",
                QuestionNormalized = "锁片安装",
                AnswerDraft = "锁片安装视频：https://example.com/1",
                Tags = ["资料"],
                Confidence = 0.58,
                SourceCount = 2,
                UpdatedAt = DateTimeOffset.Parse("2026-04-16T09:00:00+08:00")
            }
        ]);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Single(group.Members));
    }
}
