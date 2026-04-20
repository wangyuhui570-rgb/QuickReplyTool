using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class ScreenOcrReaderTests
{
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
