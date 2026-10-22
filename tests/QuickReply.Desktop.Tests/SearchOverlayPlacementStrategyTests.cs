using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class SearchOverlayPlacementStrategyTests
{
    [Fact]
    public void Calculate_StartsJustBelowCaretAndExpandsDownward()
    {
        var targetInfo = CreateTargetInfo(height: 900);
        var workingArea = CreateWorkingArea(height: 1080);
        var caret = new SearchOverlayCaretAnchor(120, 700, 121, 720);
        var placement = SearchOverlayPlacementStrategy.Calculate(
            targetInfo,
            workingArea,
            CreateOptions(visibleResultCount: 7),
            caret);

        Assert.Equal(SearchOverlayPlacementStrategy.AnchorCaretBelow, placement.Anchor);
        Assert.Equal(caret.Bottom + 8d, placement.Top);
        Assert.True(placement.Height >= 280d);
        AssertFitsWorkingArea(placement, workingArea);
        Assert.True(placement.Top + placement.Height <= workingArea.Bottom);
    }

    [Fact]
    public void Calculate_WithoutCaret_StartsNearInputZoneAndStaysTall()
    {
        var targetInfo = CreateTargetInfo(height: 900);
        var workingArea = CreateWorkingArea(height: 1080);
        var placement = SearchOverlayPlacementStrategy.Calculate(
            targetInfo,
            workingArea,
            CreateOptions(visibleResultCount: 7));

        Assert.Equal(SearchOverlayPlacementStrategy.AnchorInputZoneBelow, placement.Anchor);
        Assert.True(placement.Top > targetInfo.Top);
        Assert.True(placement.Height >= SearchOverlayPlacementStrategy.CalculateOverlayHeight(5, 36));
        AssertFitsWorkingArea(placement, workingArea);
    }

    [Fact]
    public void Calculate_WhenCaretNearBottom_PullsUpToKeepTallResults()
    {
        var targetInfo = CreateTargetInfo(height: 900);
        var workingArea = CreateWorkingArea(height: 900);
        var caret = new SearchOverlayCaretAnchor(120, 860, 121, 878);
        var preferred = SearchOverlayPlacementStrategy.CalculateOverlayHeight(7, 36);
        var placement = SearchOverlayPlacementStrategy.Calculate(
            targetInfo,
            workingArea,
            CreateOptions(visibleResultCount: 7),
            caret);

        Assert.Equal(SearchOverlayPlacementStrategy.AnchorCaretBelow, placement.Anchor);
        Assert.True(placement.Height >= preferred - 1);
        AssertFitsWorkingArea(placement, workingArea);
        // Overlay may cover the composer; top should be above caret so height fits.
        Assert.True(placement.Top < caret.Bottom);
    }

    [Fact]
    public void CalculateOverlayHeight_FitsFiveToNineRows()
    {
        var five = SearchOverlayPlacementStrategy.CalculateOverlayHeight(5, 36);
        var nine = SearchOverlayPlacementStrategy.CalculateOverlayHeight(9, 36);
        Assert.True(five < nine);
        Assert.True(five > 300);
    }

    [Fact]
    public void Calculate_MapsHeightPercentSettingsToVisibleRows()
    {
        var targetInfo = CreateTargetInfo(height: 900);
        var workingArea = CreateWorkingArea(height: 1080);
        var shortPlacement = SearchOverlayPlacementStrategy.Calculate(
            targetInfo,
            workingArea,
            CreateOptions(visibleResultCount: 18));
        var tallPlacement = SearchOverlayPlacementStrategy.Calculate(
            targetInfo,
            workingArea,
            CreateOptions(visibleResultCount: 56));

        Assert.True(shortPlacement.Height <= tallPlacement.Height);
    }

    private static SearchOverlayPlacementOptions CreateOptions(double visibleResultCount = 7) =>
        new(
            MinWidth: 420,
            WidthPercent: 100,
            VisibleResultCount: visibleResultCount,
            InputBoxHeight: 36);

    private static WindowDockTargetInfo CreateTargetInfo(int height) =>
        new()
        {
            Handle = new IntPtr(1),
            Title = "抖店客服",
            WindowClassName = "Chrome_WidgetWin_1",
            ProcessName = "msedge",
            ProcessPath = @"C:\Program Files\msedge.exe",
            Left = 0,
            Top = 0,
            Width = 1280,
            Height = height
        };

    private static SearchOverlayWorkingArea CreateWorkingArea(int height) =>
        new(0, 0, 1280, height);

    private static void AssertFitsWorkingArea(
        SearchOverlayPlacement placement,
        SearchOverlayWorkingArea workingArea)
    {
        Assert.True(placement.Left >= workingArea.Left);
        Assert.True(placement.Top >= workingArea.Top);
        Assert.True(placement.Left + placement.Width <= workingArea.Right + 0.5);
        Assert.True(placement.Top + placement.Height <= workingArea.Bottom + 0.5);
        Assert.True(placement.Width > 0);
        Assert.True(placement.Height > 0);
    }
}
