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
    public void GetPreferredOcrLanguageTagsForTesting_PrioritizesChineseForChineseWorkbench()
    {
        var languages = ScreenOcrReader.GetPreferredOcrLanguageTagsForTesting("wecom");
        var orderedLanguages = languages.ToArray();

        Assert.Contains("zh-CN", languages);
        Assert.Contains("en-US", languages);
        Assert.True(Array.IndexOf(orderedLanguages, "zh-CN") < Array.IndexOf(orderedLanguages, "en-US"));
    }

    [Fact]
    public void GetPreferredOcrLanguageTagsForTesting_UsesLatinFallbackForUnknownPlatform()
    {
        var languages = ScreenOcrReader.GetPreferredOcrLanguageTagsForTesting("unknown");

        Assert.Equal(["en-US"], languages);
    }

    [Fact]
    public void GetNormalizedCropRectanglesForTesting_CoversBroaderConversationAreaForWeCom()
    {
        var rectangles = ScreenOcrReader.GetNormalizedCropRectanglesForTesting("wecom");

        Assert.NotEmpty(rectangles);
        Assert.Contains(rectangles, rectangle => rectangle.Y <= 0.10d && rectangle.Height >= 0.70d);
    }

    [Fact]
    public void GetNormalizedCropRectanglesForTesting_CoversBroaderConversationAreaForWorkbench()
    {
        var rectangles = ScreenOcrReader.GetNormalizedCropRectanglesForTesting("qianniu");

        Assert.NotEmpty(rectangles);
        Assert.Contains(rectangles, rectangle => rectangle.Y <= 0.10d && rectangle.Height >= 0.70d);
    }

    [Fact]
    public async Task TryReadTextDetailedAsync_ReturnsMissingImageStatus_WhenScreenshotDoesNotExist()
    {
        var result = await ScreenOcrReader.TryReadTextDetailedAsync("/tmp/quickreply-missing-image.png", "wecom");

        Assert.False(result.Success);
        Assert.Equal("missing_image", result.Status);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void GetResultPriorityForTesting_PrefersErrorsOverEmptyReads()
    {
        Assert.True(
            ScreenOcrReader.GetResultPriorityForTesting("ocr_error") >
            ScreenOcrReader.GetResultPriorityForTesting("ocr_empty"));
    }
}
