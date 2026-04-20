using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class ScreenOcrReaderTests
{
    [Fact]
    public void GetPowerShellHostCandidatesForTesting_PrefersWindowsPowerShellBeforePwsh()
    {
        var hosts = ScreenOcrReader.GetPowerShellHostCandidatesForTesting();

        Assert.Equal(["powershell.exe", "pwsh.exe"], hosts);
    }

    [Fact]
    public void GetPreferredFallbackOcrLanguageTagsForTesting_PrioritizesChineseBeforeEnglish()
    {
        var languages = ScreenOcrReader.GetPreferredFallbackOcrLanguageTagsForTesting();
        var orderedLanguages = languages.ToArray();

        Assert.Contains("zh-CN", languages);
        Assert.Contains("en-US", languages);
        Assert.True(Array.IndexOf(orderedLanguages, "zh-CN") < Array.IndexOf(orderedLanguages, "en-US"));
    }

    [Fact]
    public void GetNormalizedCropRectanglesForTesting_UsesLowerChatRegionForWeCom()
    {
        var rectangles = ScreenOcrReader.GetNormalizedCropRectanglesForTesting("wecom");

        Assert.NotEmpty(rectangles);
        Assert.All(rectangles, rectangle => Assert.True(rectangle.Y >= 0.18d));
    }

    [Fact]
    public void GetNormalizedCropRectanglesForTesting_UsesLowerChatRegionForWorkbench()
    {
        var rectangles = ScreenOcrReader.GetNormalizedCropRectanglesForTesting("qianniu");

        Assert.NotEmpty(rectangles);
        Assert.All(rectangles, rectangle => Assert.True(rectangle.Y >= 0.18d));
    }
}
