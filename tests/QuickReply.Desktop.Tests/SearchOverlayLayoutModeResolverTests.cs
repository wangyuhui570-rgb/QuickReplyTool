using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class SearchOverlayLayoutModeResolverTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("发货", true)]
    public void HasManualSearchText_ReturnsExpectedState(string searchText, bool expected)
    {
        Assert.Equal(expected, SearchOverlayLayoutModeResolver.HasManualSearchText(searchText));
    }

    [Fact]
    public void ShouldShowRecommendationPanel_OnlyWhenSearchTextIsEmpty()
    {
        Assert.True(SearchOverlayLayoutModeResolver.ShouldShowRecommendationPanel(string.Empty, true));
        Assert.False(SearchOverlayLayoutModeResolver.ShouldShowRecommendationPanel("发货", true));
        Assert.False(SearchOverlayLayoutModeResolver.ShouldShowRecommendationPanel(string.Empty, false));
    }

    [Fact]
    public void ShouldShowSearchResultsPanel_OnlyWhenManualSearchExists()
    {
        Assert.False(SearchOverlayLayoutModeResolver.ShouldShowSearchResultsPanel(string.Empty));
        Assert.True(SearchOverlayLayoutModeResolver.ShouldShowSearchResultsPanel("安装"));
    }

    [Fact]
    public void ShouldShowOverlayAssistText_ShowsActionStatusEvenWithoutManualSearch()
    {
        Assert.False(SearchOverlayLayoutModeResolver.ShouldShowOverlayAssistText(string.Empty, false));
        Assert.True(SearchOverlayLayoutModeResolver.ShouldShowOverlayAssistText("退款", false));
        Assert.True(SearchOverlayLayoutModeResolver.ShouldShowOverlayAssistText(string.Empty, true));
    }
}
