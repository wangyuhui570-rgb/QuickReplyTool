namespace QuickReply.Desktop.Services;

public static class SearchOverlayLayoutModeResolver
{
    public static bool HasManualSearchText(string? searchText) =>
        !string.IsNullOrWhiteSpace(searchText);

    // Only show the assist strip for real action feedback — never the mode shortcut tip bar.
    public static bool ShouldShowOverlayAssistText(string? searchText, bool hasActionStatusMessage) =>
        hasActionStatusMessage;
}
