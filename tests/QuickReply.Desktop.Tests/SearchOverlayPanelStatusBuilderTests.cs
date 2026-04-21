using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class SearchOverlayPanelStatusBuilderTests
{
    [Fact]
    public void BuildResultsSectionSummary_ShowsEmptyPhrasePromptWithoutRecommendationCopy()
    {
        var text = SearchOverlayPanelStatusBuilder.BuildResultsSectionSummary(
            SearchOverlayMode.Phrase,
            "安装视频",
            resultCount: 0);

        Assert.Equal("当前没有匹配的短语结果。", text);
    }

    [Fact]
    public void BuildResultsSectionSummary_ShowsDefaultMixedPromptWhenSearchIsEmpty()
    {
        var text = SearchOverlayPanelStatusBuilder.BuildResultsSectionSummary(
            SearchOverlayMode.Mixed,
            string.Empty,
            resultCount: 0);

        Assert.Equal("可同时搜索短语和文件。", text);
    }

    [Fact]
    public void BuildEmptyStateHintText_ExplainsEmptyFileMode()
    {
        var text = SearchOverlayPanelStatusBuilder.BuildEmptyStateHintText(
            SearchOverlayMode.File,
            "安装包");

        Assert.Contains("换一个文件名关键词", text, StringComparison.Ordinal);
    }
}
