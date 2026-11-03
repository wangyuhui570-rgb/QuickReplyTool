using QuickReply.Core.Services;

namespace QuickReply.Desktop.Tests;

public sealed class SearchKeywordMatcherTests
{
    [Theory]
    [InlineData("退货运费", "thyf")]
    [InlineData("发货时效 7天", "fhsx_7t")]
    [InlineData("Hello 世界", "hello_sj")]
    public void GetPinyinInitials_KeepsOneLetterPerCharacter(string text, string expected)
    {
        Assert.Equal(expected, SearchKeywordMatcher.GetPinyinInitials(text));
    }

    [Fact]
    public void Score_RequiresEverySpaceSeparatedKeyword()
    {
        Assert.True(SearchKeywordMatcher.Matches("退货 运费", "退货说明", "", "运费由买家承担"));
        Assert.False(SearchKeywordMatcher.Matches("退货 发票", "退货说明", "", "运费由买家承担"));
    }

    [Fact]
    public void Score_MatchesPinyinInitialsInTitleAndTags()
    {
        Assert.True(SearchKeywordMatcher.Matches("th", "退货流程", "", ""));
        Assert.True(SearchKeywordMatcher.Matches("fh", "", "发货", ""));
        // Body text is not matched by initials: two letters would hit almost every long reply.
        Assert.False(SearchKeywordMatcher.Matches("th", "", "", "退货请联系客服"));
    }

    [Fact]
    public void Score_RanksLiteralTitleAbovePinyinAndBody()
    {
        var literalTitle = SearchKeywordMatcher.Score("退货", "退货", "", "");
        var pinyinTitle = SearchKeywordMatcher.Score("th", "退货", "", "");
        var body = SearchKeywordMatcher.Score("退货", "说明", "", "退货");
        Assert.True(literalTitle > pinyinTitle);
        Assert.True(pinyinTitle > body);
    }

    [Fact]
    public void FindLiteralRanges_MergesOverlappingKeywords()
    {
        Assert.Equal([(0, 2), (3, 3)], SearchKeywordMatcher.FindLiteralRanges("退货运费退货", "退货 费"));
        Assert.Empty(SearchKeywordMatcher.FindLiteralRanges("退货", ""));
    }
}
