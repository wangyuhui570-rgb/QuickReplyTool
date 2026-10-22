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
    public void ShouldShowOverlayAssistText_OnlyShowsActionStatus()
    {
        Assert.False(SearchOverlayLayoutModeResolver.ShouldShowOverlayAssistText(string.Empty, false));
        Assert.False(SearchOverlayLayoutModeResolver.ShouldShowOverlayAssistText("退款", false));
        Assert.True(SearchOverlayLayoutModeResolver.ShouldShowOverlayAssistText(string.Empty, true));
        Assert.True(SearchOverlayLayoutModeResolver.ShouldShowOverlayAssistText("退款", true));
    }
}
