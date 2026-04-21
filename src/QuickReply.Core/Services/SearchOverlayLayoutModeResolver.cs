namespace QuickReply.Desktop.Services;

public static class SearchOverlayLayoutModeResolver
{
    public static bool HasManualSearchText(string? searchText) =>
        !string.IsNullOrWhiteSpace(searchText);

    public static bool ShouldShowOverlayAssistText(string? searchText, bool hasActionStatusMessage) =>
        hasActionStatusMessage || HasManualSearchText(searchText);
}
