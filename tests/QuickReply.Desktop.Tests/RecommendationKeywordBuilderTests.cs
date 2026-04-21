using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class RecommendationKeywordBuilderTests
{
    [Fact]
    public void Build_ReservesKeywordSlotsForContext()
    {
        var keywords = RecommendationKeywordBuilder.Build(
            "order,shipping,refund,install,video,size,battery,warranty,return,exchange,logistics,coupon,color",
            "damage,invoice",
            "SKU-2000",
            "QuickReply Shoes");

        Assert.Contains("damage", keywords);
        Assert.Contains("invoice", keywords);
        Assert.True(keywords.Count <= 12);
    }
}
