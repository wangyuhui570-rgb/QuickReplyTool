namespace QuickReply.Desktop.Services;

internal static class SearchOverlayPlacementStrategy
{
    internal const string AnchorCaretBelow = "caret_below";
    internal const string AnchorInputZoneBelow = "input_zone_below";

    private const double ScreenMargin = 24d;
    private const double CaretGap = 8d;
    private const double BottomMargin = 12d;
    private const double TopMargin = 12d;
    private const double OverlayVerticalPadding = 20d;
    private const double ResultPanelTopMargin = 8d;
    private const double ResultHeaderHeight = 16d;
    private const double ResultListTopMargin = 8d;
    private const double ResultRowHeight = 52d;
    private const double OverlayHeightSafetyPadding = 10d;

    public static SearchOverlayPlacement Calculate(
        WindowDockTargetInfo targetInfo,
        SearchOverlayWorkingArea workingArea,
        SearchOverlayPlacementOptions options,
        SearchOverlayCaretAnchor? caretAnchor = null)
    {
        ArgumentNullException.ThrowIfNull(targetInfo);

        var desiredWidth = targetInfo.Width * (options.WidthPercent / 100d);
        var width = Math.Max(
            options.MinWidth,
            Math.Min(desiredWidth, Math.Max(options.MinWidth, workingArea.Width - ScreenMargin)));
        var inputBoxHeight = Math.Clamp(options.InputBoxHeight, 26d, 64d);
        var visibleRows = ResolveVisibleResultCount(options.VisibleResultCount);
        var preferredHeight = CalculateOverlayHeight(visibleRows, inputBoxHeight);
        var maxUsefulHeight = CalculateOverlayHeight(9d, inputBoxHeight);

        var centeredLeft = targetInfo.Left + ((targetInfo.Width - width) / 2d);
        var left = Clamp(centeredLeft, workingArea.Left, workingArea.Right - width);

        string anchor;
        double preferredTop;
        if (caretAnchor is { IsUsable: true } caret)
        {
            preferredTop = caret.Bottom + CaretGap;
            anchor = AnchorCaretBelow;
        }
        else
        {
            // Fallback: start near the lower input zone of the workbench and expand downward.
            preferredTop = targetInfo.Top + (targetInfo.Height * 0.62d);
            preferredTop = Math.Min(preferredTop, targetInfo.Bottom - 48d);
            preferredTop = Math.Max(preferredTop, targetInfo.Top + 80d);
            anchor = AnchorInputZoneBelow;
        }

        var maxBottom = workingArea.Bottom - BottomMargin;
        preferredTop = Clamp(preferredTop, workingArea.Top + TopMargin, maxBottom);
        var availableBelow = Math.Max(0d, maxBottom - preferredTop);
        // Prefer staying just below the caret whenever a usable results area fits.
        // Only pull upward when the remaining space is too short for ~4-5 candidates.
        var minUsableHeight = Math.Min(CalculateOverlayHeight(5d, inputBoxHeight), 300d);

        double top;
        double height;
        if (availableBelow >= minUsableHeight)
        {
            top = preferredTop;
            height = Math.Min(availableBelow, Math.Max(minUsableHeight, Math.Min(preferredHeight, maxUsefulHeight)));
            // Take remaining space under the caret when it helps show more candidates.
            if (availableBelow > height)
            {
                height = Math.Min(availableBelow, maxUsefulHeight);
            }
        }
        else
        {
            // Remaining space under the caret is too short: pull up so results stay usable.
            // Covering the composer is acceptable.
            height = preferredHeight;
            top = maxBottom - height;
            top = Math.Max(workingArea.Top + TopMargin, top);
            height = Math.Min(height, Math.Max(0d, maxBottom - top));
        }

        var inputBottomOffset = Math.Clamp(inputBoxHeight + 10d, 30d, 80d);
        return new SearchOverlayPlacement(left, top, width, height, anchor, inputBottomOffset);
    }

    internal static double CalculateOverlayHeight(double visibleResultCount, double inputBoxHeight)
    {
        var clampedVisibleResultCount = Math.Clamp(visibleResultCount, 5d, 9d);
        var clampedInputBoxHeight = Math.Clamp(inputBoxHeight, 26d, 64d);
        // Assist tip bar removed; keep only chrome + results rows.
        return OverlayVerticalPadding +
               clampedInputBoxHeight +
               ResultPanelTopMargin +
               ResultHeaderHeight +
               ResultListTopMargin +
               (clampedVisibleResultCount * ResultRowHeight) +
               OverlayHeightSafetyPadding;
    }

    private static double ResolveVisibleResultCount(double configuredValue)
    {
        // Settings historically store height percent (18-56). Map that to ~5-9 visible rows.
        if (configuredValue >= 12d)
        {
            return Math.Clamp(configuredValue / 6d, 5d, 9d);
        }

        return Math.Clamp(configuredValue, 5d, 9d);
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        if (maximum < minimum)
        {
            return minimum;
        }

        return Math.Max(minimum, Math.Min(value, maximum));
    }
}

internal readonly record struct SearchOverlayPlacement(
    double Left,
    double Top,
    double Width,
    double Height,
    string Anchor,
    double InputBottomOffset);

internal readonly record struct SearchOverlayWorkingArea(
    double Left,
    double Top,
    double Right,
    double Bottom)
{
    public double Width => Math.Max(0d, Right - Left);

    public double Height => Math.Max(0d, Bottom - Top);
}

internal readonly record struct SearchOverlayPlacementOptions(
    double MinWidth,
    double WidthPercent,
    double VisibleResultCount,
    double InputBoxHeight);

internal readonly record struct SearchOverlayCaretAnchor(
    double Left,
    double Top,
    double Right,
    double Bottom)
{
    public bool IsUsable => Right >= Left && Bottom > Top;
}
