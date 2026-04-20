using QuickReply.CloudService;

namespace QuickReply.CloudService.Tests;

public sealed class KnowledgeCandidateQualityRulesTests
{
    [Theory]
    [InlineData("cor", false)]
    [InlineData("x2max", false)]
    [InlineData("智骑软件", true)]
    [InlineData("锁片安装", true)]
    public void CanUseSearchKeywordFallback_FiltersWeakKeywords(string keyword, bool expected)
    {
        Assert.Equal(expected, KnowledgeCandidateQualityRules.CanUseSearchKeywordFallback(keyword));
    }

    [Theory]
    [InlineData("可以开发票吗", true)]
    [InlineData("您好亲", false)]
    [InlineData("功率计", false)]
    public void LooksLikeCompleteQaQuestion_UsesStricterGate(string question, bool expected)
    {
        Assert.Equal(expected, KnowledgeCandidateQualityRules.LooksLikeCompleteQaQuestion(question));
    }

    [Fact]
    public void ShouldUseAiSynthesis_ReturnsFalse_ForResourceCandidates()
    {
        var candidate = new KnowledgeCandidateRecord
        {
            Id = Guid.NewGuid(),
            CandidateKind = KnowledgeCandidateKinds.Resource,
            Status = "draft",
            Platform = "qianniu",
            QuestionNormalized = "智骑软件下载",
            AnswerDraft = "智骑软件下载地址：https://example.com",
            SourceCount = 3
        };

        List<KnowledgeCandidateSourceRecord> sources =
        [
            new KnowledgeCandidateSourceRecord { Id = Guid.NewGuid(), CandidateId = candidate.Id, SourceType = "conversation", SourceId = "1", Excerpt = "智骑软件 => 下载地址 https://example.com" },
            new KnowledgeCandidateSourceRecord { Id = Guid.NewGuid(), CandidateId = candidate.Id, SourceType = "conversation", SourceId = "2", Excerpt = "智骑软件 => 下载地址 https://example.com" }
        ];

        Assert.False(KnowledgeCandidateQualityRules.ShouldUseAiSynthesis(candidate, sources));
    }

    [Fact]
    public void ShouldKeepImportedCandidate_RejectsLowValueQaDraft()
    {
        var shouldKeep = KnowledgeCandidateQualityRules.ShouldKeepImportedCandidate(
            KnowledgeCandidateKinds.Qa,
            "您好亲",
            "好的亲麻烦您了",
            sourceCount: 1,
            confidence: 0.8,
            fromKeywordFallback: false);

        Assert.False(shouldKeep);
    }

    [Fact]
    public void ShouldKeepImportedCandidate_AllowsHighConfidenceSingleSourceQa()
    {
        var shouldKeep = KnowledgeCandidateQualityRules.ShouldKeepImportedCandidate(
            KnowledgeCandidateKinds.Qa,
            "这个支持开发票吗",
            "支持开票，下单后联系客服登记抬头即可。",
            sourceCount: 1,
            confidence: 0.72,
            fromKeywordFallback: false);

        Assert.True(shouldKeep);
    }

    [Fact]
    public void ShouldKeepImportedCandidate_AllowsStrongResourceKeywordFallback()
    {
        var shouldKeep = KnowledgeCandidateQualityRules.ShouldKeepImportedCandidate(
            KnowledgeCandidateKinds.Resource,
            "智骑软件下载",
            "智骑软件下载地址：https://example.com/app",
            sourceCount: 1,
            confidence: 0.31,
            fromKeywordFallback: true);

        Assert.True(shouldKeep);
    }

    [Fact]
    public void ShouldKeepImportedCandidate_RejectsConversationalVideoRequest()
    {
        var shouldKeep = KnowledgeCandidateQualityRules.ShouldKeepImportedCandidate(
            KnowledgeCandidateKinds.Resource,
            "麻烦您拍个视频这边看一下的",
            "麻烦您拍个视频这边看一下的",
            sourceCount: 1,
            confidence: 0.31,
            fromKeywordFallback: false);

        Assert.False(shouldKeep);
    }

    [Fact]
    public void IsResourceCandidate_DoesNotTreatGenericAddressComplaintAsResource()
    {
        var isResource = KnowledgeCandidateQualityRules.IsResourceCandidate(
            "顶天立地",
            "您好亲 在的吗很抱歉您的地址要经过集运仓。这款停车架是属于大件的。");

        Assert.False(isResource);
    }
}
