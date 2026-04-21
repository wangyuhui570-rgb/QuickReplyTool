using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal interface ISettingsWindowSelections
{
    SearchFocusHotkeyMode SelectedSearchHotkeyMode { get; set; }
    SearchTextSendShortcutMode SelectedSearchTextSendShortcutMode { get; set; }
    SidebarWorkMode SelectedSidebarWorkMode { get; set; }
    bool SelectedRunAsAdministrator { get; set; }
    bool SelectedLaunchAtStartup { get; set; }
    bool SelectedLaunchScreenshotToolWithApp { get; set; }
    string SelectedScreenshotToolHotkey { get; set; }
    string SelectedScreenshotToolSaveDirectory { get; set; }
    string SelectedIssueReportDirectory { get; set; }
    bool SelectedEnableCloudSync { get; set; }
    bool SelectedAllowRawConversationCloudSync { get; set; }
    bool SelectedAllowAutomaticIssueReportUpload { get; set; }
    bool SelectedEnableUpdateChecks { get; set; }
    string SelectedServerBaseUrl { get; set; }
    string SelectedSyncClientId { get; set; }
    string SelectedSyncApiToken { get; set; }
    PhraseImageSendMode SelectedPhraseImageSendMode { get; set; }
    SearchOverlayMode SelectedDefaultSearchOverlayMode { get; set; }
    int SelectedRecommendationDisplayCount { get; set; }
    int SelectedSearchOverlayWidthPercent { get; set; }
    int SelectedSearchOverlayHeightPercent { get; set; }
    int SelectedSearchInputWidthPercent { get; set; }
    int SelectedSearchInputBoxHeight { get; set; }
    int SelectedSidebarWidth { get; set; }
    int SelectedSidebarHeightPercent { get; set; }
    int SelectedCategoryTabsPerRow { get; set; }
    int SelectedCategoryFontSize { get; set; }
    int SelectedPhraseFontSize { get; set; }
    int SelectedPhraseRowSpacing { get; set; }
    SendHotkeyMode SelectedSendHotkeyMode { get; set; }
    string SelectedFileLibraryFolderPath { get; set; }
    string SelectedPhraseImageFolderPath { get; set; }
    int SelectedRestoreFocusDelayMs { get; set; }
    int SelectedActivationRetryDelayMs { get; set; }
    int SelectedSendDelayMs { get; set; }
    int SelectedClipboardRestoreDelayMs { get; set; }
    int SelectedTextPasteDelayMs { get; set; }
    int SelectedImagePasteDelayMs { get; set; }
    int SelectedRichImageSendDelayMs { get; set; }
    int SelectedRichImageClipboardRestoreDelayMs { get; set; }
    int SelectedPhraseHoverPreviewDelayMs { get; set; }
    int SelectedLogScreenshotRetentionDays { get; set; }
    int SelectedLogScreenshotMaxSizeMb { get; set; }
    int SelectedLogTextRetentionDays { get; set; }
    string SelectedCategoryColorHex { get; set; }
    int SelectedCategoryOpacityPercent { get; set; }
    string SelectedTagColorHex { get; set; }
    int SelectedTagOpacityPercent { get; set; }
    string SelectedPhraseColorHex { get; set; }
    int SelectedPhraseOpacityPercent { get; set; }
    string SelectedSidebarColorHex { get; set; }
    int SelectedSidebarOpacityPercent { get; set; }
    string SelectedSearchOverlayColorHex { get; set; }
    int SelectedSearchOverlayOpacityPercent { get; set; }
    string SelectedSearchInputColorHex { get; set; }
    int SelectedSearchInputOpacityPercent { get; set; }
}

internal static class SettingsWindowSelectionMapper
{
    public static void ApplySettingsToSelections(AppSettings source, ISettingsWindowSelections target)
    {
        target.SelectedSearchHotkeyMode = source.SearchFocusHotkeyMode;
        target.SelectedSearchTextSendShortcutMode = source.SearchTextSendShortcutMode;
        target.SelectedSidebarWorkMode = source.SidebarWorkMode;
        target.SelectedRunAsAdministrator = source.RunAsAdministrator;
        target.SelectedLaunchAtStartup = source.LaunchAtStartup;
        target.SelectedLaunchScreenshotToolWithApp = source.LaunchScreenshotToolWithApp;
        target.SelectedScreenshotToolHotkey = source.ScreenshotToolHotkey;
        target.SelectedScreenshotToolSaveDirectory = source.ScreenshotToolSaveDirectory;
        target.SelectedIssueReportDirectory = source.IssueReportDirectory;
        target.SelectedEnableCloudSync = false;
        target.SelectedAllowRawConversationCloudSync = false;
        target.SelectedAllowAutomaticIssueReportUpload = false;
        target.SelectedEnableUpdateChecks = source.EnableUpdateChecks;
        target.SelectedServerBaseUrl = source.ServerBaseUrl;
        target.SelectedSyncClientId = source.SyncClientId;
        target.SelectedSyncApiToken = source.SyncApiToken;
        target.SelectedPhraseImageSendMode = source.PhraseImageSendMode;
        target.SelectedDefaultSearchOverlayMode = source.DefaultSearchOverlayMode;
        target.SelectedRecommendationDisplayCount = source.RecommendationDisplayCount;
        target.SelectedSearchOverlayWidthPercent = source.SearchOverlayWidthPercent;
        target.SelectedSearchOverlayHeightPercent = source.SearchOverlayHeightPercent;
        target.SelectedSearchInputWidthPercent = source.SearchInputWidthPercent;
        target.SelectedSearchInputBoxHeight = source.SearchInputBoxHeight;
        target.SelectedSidebarWidth = source.SidebarWidth;
        target.SelectedSidebarHeightPercent = source.SidebarHeightPercent;
        target.SelectedCategoryTabsPerRow = source.CategoryTabsPerRow;
        target.SelectedCategoryFontSize = source.CategoryFontSize;
        target.SelectedPhraseFontSize = source.PhraseFontSize;
        target.SelectedPhraseRowSpacing = source.PhraseRowSpacing;
        target.SelectedSendHotkeyMode = source.SendHotkeyMode;
        target.SelectedFileLibraryFolderPath = source.FileLibraryFolderPath;
        target.SelectedPhraseImageFolderPath = source.PhraseImageFolderPath;
        target.SelectedRestoreFocusDelayMs = source.RestoreFocusDelayMs;
        target.SelectedActivationRetryDelayMs = source.ActivationRetryDelayMs;
        target.SelectedSendDelayMs = source.SendDelayMs;
        target.SelectedClipboardRestoreDelayMs = source.ClipboardRestoreDelayMs;
        target.SelectedTextPasteDelayMs = source.TextPasteDelayMs;
        target.SelectedImagePasteDelayMs = source.ImagePasteDelayMs;
        target.SelectedRichImageSendDelayMs = source.RichImageSendDelayMs;
        target.SelectedRichImageClipboardRestoreDelayMs = source.RichImageClipboardRestoreDelayMs;
        target.SelectedPhraseHoverPreviewDelayMs = source.PhraseHoverPreviewDelayMs;
        target.SelectedLogScreenshotRetentionDays = source.LogScreenshotRetentionDays;
        target.SelectedLogScreenshotMaxSizeMb = source.LogScreenshotMaxSizeMb;
        target.SelectedLogTextRetentionDays = source.LogTextRetentionDays;
        target.SelectedCategoryColorHex = source.CategoryColorHex;
        target.SelectedCategoryOpacityPercent = source.CategoryOpacityPercent;
        target.SelectedTagColorHex = source.TagColorHex;
        target.SelectedTagOpacityPercent = source.TagOpacityPercent;
        target.SelectedPhraseColorHex = source.PhraseColorHex;
        target.SelectedPhraseOpacityPercent = source.PhraseOpacityPercent;
        target.SelectedSidebarColorHex = source.SidebarColorHex;
        target.SelectedSidebarOpacityPercent = source.SidebarOpacityPercent;
        target.SelectedSearchOverlayColorHex = source.SearchOverlayColorHex;
        target.SelectedSearchOverlayOpacityPercent = source.SearchOverlayOpacityPercent;
        target.SelectedSearchInputColorHex = source.SearchInputColorHex;
        target.SelectedSearchInputOpacityPercent = source.SearchInputOpacityPercent;
    }

    public static void ApplySelectionsToSettings(ISettingsWindowSelections source, AppSettings target)
    {
        target.SearchFocusHotkeyMode = source.SelectedSearchHotkeyMode;
        target.SearchTextSendShortcutMode = source.SelectedSearchTextSendShortcutMode;
        target.SidebarWorkMode = source.SelectedSidebarWorkMode;
        target.RunAsAdministrator = source.SelectedRunAsAdministrator;
        target.LaunchAtStartup = source.SelectedLaunchAtStartup;
        target.LaunchScreenshotToolWithApp = source.SelectedLaunchScreenshotToolWithApp;
        target.ScreenshotToolHotkey = source.SelectedScreenshotToolHotkey;
        target.ScreenshotToolSaveDirectory = source.SelectedScreenshotToolSaveDirectory;
        target.IssueReportDirectory = source.SelectedIssueReportDirectory;
        target.EnableCloudSync = false;
        target.AllowRawConversationCloudSync = false;
        target.AllowAutomaticIssueReportUpload = false;
        target.EnableUpdateChecks = source.SelectedEnableUpdateChecks;
        target.ServerBaseUrl = source.SelectedServerBaseUrl;
        target.SyncClientId = source.SelectedSyncClientId;
        target.SyncApiToken = source.SelectedSyncApiToken;
        target.PhraseImageSendMode = source.SelectedPhraseImageSendMode;
        target.DefaultSearchOverlayMode = source.SelectedDefaultSearchOverlayMode;
        target.RecommendationDisplayCount = source.SelectedRecommendationDisplayCount;
        target.SearchOverlayWidthPercent = source.SelectedSearchOverlayWidthPercent;
        target.SearchOverlayHeightPercent = source.SelectedSearchOverlayHeightPercent;
        target.SearchInputWidthPercent = source.SelectedSearchInputWidthPercent;
        target.SearchInputBoxHeight = source.SelectedSearchInputBoxHeight;
        target.SidebarWidth = source.SelectedSidebarWidth;
        target.SidebarHeightPercent = source.SelectedSidebarHeightPercent;
        target.CategoryTabsPerRow = source.SelectedCategoryTabsPerRow;
        target.CategoryFontSize = source.SelectedCategoryFontSize;
        target.PhraseFontSize = source.SelectedPhraseFontSize;
        target.PhraseRowSpacing = source.SelectedPhraseRowSpacing;
        target.SendHotkeyMode = source.SelectedSendHotkeyMode;
        target.FileLibraryFolderPath = source.SelectedFileLibraryFolderPath;
        target.PhraseImageFolderPath = source.SelectedPhraseImageFolderPath;
        target.RestoreFocusDelayMs = source.SelectedRestoreFocusDelayMs;
        target.ActivationRetryDelayMs = source.SelectedActivationRetryDelayMs;
        target.SendDelayMs = source.SelectedSendDelayMs;
        target.ClipboardRestoreDelayMs = source.SelectedClipboardRestoreDelayMs;
        target.TextPasteDelayMs = source.SelectedTextPasteDelayMs;
        target.ImagePasteDelayMs = source.SelectedImagePasteDelayMs;
        target.RichImageSendDelayMs = source.SelectedRichImageSendDelayMs;
        target.RichImageClipboardRestoreDelayMs = source.SelectedRichImageClipboardRestoreDelayMs;
        target.PhraseHoverPreviewDelayMs = source.SelectedPhraseHoverPreviewDelayMs;
        target.LogScreenshotRetentionDays = source.SelectedLogScreenshotRetentionDays;
        target.LogScreenshotMaxSizeMb = source.SelectedLogScreenshotMaxSizeMb;
        target.LogTextRetentionDays = source.SelectedLogTextRetentionDays;
        target.CategoryColorHex = source.SelectedCategoryColorHex;
        target.CategoryOpacityPercent = source.SelectedCategoryOpacityPercent;
        target.TagColorHex = source.SelectedTagColorHex;
        target.TagOpacityPercent = source.SelectedTagOpacityPercent;
        target.PhraseColorHex = source.SelectedPhraseColorHex;
        target.PhraseOpacityPercent = source.SelectedPhraseOpacityPercent;
        target.SidebarColorHex = source.SelectedSidebarColorHex;
        target.SidebarOpacityPercent = source.SelectedSidebarOpacityPercent;
        target.SearchOverlayColorHex = source.SelectedSearchOverlayColorHex;
        target.SearchOverlayOpacityPercent = source.SelectedSearchOverlayOpacityPercent;
        target.SearchInputColorHex = source.SelectedSearchInputColorHex;
        target.SearchInputOpacityPercent = source.SelectedSearchInputOpacityPercent;
    }
}
