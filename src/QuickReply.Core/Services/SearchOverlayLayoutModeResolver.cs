namespace QuickReply.Desktop.Services;

public static class SearchOverlayLayoutModeResolver
{
    public static bool HasManualSearchText(string? searchText) =>
        !string.IsNullOrWhiteSpace(searchText);

    public static bool ShouldShowRecommendationPanel(string? searchText, bool hasRecommendationPanelContent) =>
        !HasManualSearchText(searchText) && hasRecommendationPanelContent;

    public static bool ShouldShowSearchResultsPanel(string? searchText) =>
        HasManualSearchText(searchText);

    public static bool ShouldShowOverlayAssistText(string? searchText, bool hasActionStatusMessage) =>
        hasActionStatusMessage || HasManualSearchText(searchText);
}
