using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class SettingsWindowSelectionMapperTests
{
    [Fact]
    public void ApplySettingsToSelections_MapsRepresentativeDesktopFields()
    {
        var settings = new AppSettings
        {
            SearchFocusHotkeyMode = SearchFocusHotkeyMode.DoubleCtrl,
            SearchTextSendShortcutMode = SearchTextSendShortcutMode.AltEnter,
            SidebarWorkMode = SidebarWorkMode.Compact,
            LaunchAtStartup = true,
            LaunchScreenshotToolWithApp = false,
            ScreenshotToolHotkey = "Alt+A",
            ScreenshotToolSaveDirectory = @"C:\Screenshots",
            IssueReportDirectory = @"C:\Feedback",
            EnableCloudSync = true,
            AllowRawConversationCloudSync = true,
            AllowAutomaticIssueReportUpload = true,
            EnableUpdateChecks = false,
            ServerBaseUrl = "https://example.com",
            SyncClientId = "client-123",
            SyncApiToken = "token-456",
            PhraseImageSendMode = PhraseImageSendMode.ImageThenTextThenSend,
            DefaultSearchOverlayMode = SearchOverlayMode.Mixed,
            RecommendationDisplayCount = 8,
            SearchOverlayWidthPercent = 72,
            SearchOverlayHeightPercent = 66,
            SearchInputWidthPercent = 58,
            SearchInputBoxHeight = 46,
            SidebarWidth = 360,
            SidebarHeightPercent = 78,
            CategoryTabsPerRow = 4,
            CategoryFontSize = 15,
            PhraseFontSize = 16,
            PhraseRowSpacing = 7,
            SendHotkeyMode = SendHotkeyMode.CtrlEnter,
            FileLibraryFolderPath = @"C:\Library",
            PhraseImageFolderPath = @"C:\PhraseImages",
            RestoreFocusDelayMs = 200,
            ActivationRetryDelayMs = 350,
            SendDelayMs = 120,
            ClipboardRestoreDelayMs = 80,
            TextPasteDelayMs = 95,
            ImagePasteDelayMs = 110,
            RichImageSendDelayMs = 140,
            RichImageClipboardRestoreDelayMs = 155,
            PhraseHoverPreviewDelayMs = 420,
            LogScreenshotRetentionDays = 14,
            LogScreenshotMaxSizeMb = 2048,
            LogTextRetentionDays = 60,
            CategoryColorHex = "#112233",
            CategoryOpacityPercent = 65,
            TagColorHex = "#223344",
            TagOpacityPercent = 55,
            PhraseColorHex = "#334455",
            PhraseOpacityPercent = 45,
            SidebarColorHex = "#445566",
            SidebarOpacityPercent = 75,
            SearchOverlayColorHex = "#556677",
            SearchOverlayOpacityPercent = 85,
            SearchInputColorHex = "#667788",
            SearchInputOpacityPercent = 90
        };

        var selections = new FakeSettingsWindowSelections();

        SettingsWindowSelectionMapper.ApplySettingsToSelections(settings, selections);

        Assert.Equal(settings.SearchFocusHotkeyMode, selections.SelectedSearchHotkeyMode);
        Assert.Equal(settings.SearchTextSendShortcutMode, selections.SelectedSearchTextSendShortcutMode);
        Assert.Equal(settings.SidebarWorkMode, selections.SelectedSidebarWorkMode);
        Assert.Equal(settings.ScreenshotToolHotkey, selections.SelectedScreenshotToolHotkey);
        Assert.Equal(settings.ServerBaseUrl, selections.SelectedServerBaseUrl);
        Assert.Equal(settings.AllowRawConversationCloudSync, selections.SelectedAllowRawConversationCloudSync);
        Assert.Equal(settings.AllowAutomaticIssueReportUpload, selections.SelectedAllowAutomaticIssueReportUpload);
        Assert.Equal(settings.PhraseImageSendMode, selections.SelectedPhraseImageSendMode);
        Assert.Equal(settings.SearchOverlayWidthPercent, selections.SelectedSearchOverlayWidthPercent);
        Assert.Equal(settings.SidebarWidth, selections.SelectedSidebarWidth);
        Assert.Equal(settings.LogScreenshotRetentionDays, selections.SelectedLogScreenshotRetentionDays);
        Assert.Equal(settings.LogTextRetentionDays, selections.SelectedLogTextRetentionDays);
        Assert.Equal(settings.SearchInputOpacityPercent, selections.SelectedSearchInputOpacityPercent);
    }

    [Fact]
    public void ApplySelectionsToSettings_RoundTripsRepresentativeDesktopFields()
    {
        var selections = new FakeSettingsWindowSelections
        {
            SelectedSearchHotkeyMode = SearchFocusHotkeyMode.AltQ,
            SelectedSearchTextSendShortcutMode = SearchTextSendShortcutMode.CtrlEnter,
            SelectedSidebarWorkMode = SidebarWorkMode.Standard,
            SelectedRunAsAdministrator = true,
            SelectedLaunchAtStartup = true,
            SelectedLaunchScreenshotToolWithApp = false,
            SelectedScreenshotToolHotkey = "Ctrl+Shift+S",
            SelectedScreenshotToolSaveDirectory = @"D:\Screenshots",
            SelectedIssueReportDirectory = @"D:\IssueReports",
            SelectedEnableCloudSync = true,
            SelectedAllowRawConversationCloudSync = true,
            SelectedAllowAutomaticIssueReportUpload = true,
            SelectedEnableUpdateChecks = true,
            SelectedServerBaseUrl = "https://sync.example.com",
            SelectedSyncClientId = "client-a",
            SelectedSyncApiToken = "token-b",
            SelectedPhraseImageSendMode = PhraseImageSendMode.ImageImmediateThenTextSend,
            SelectedDefaultSearchOverlayMode = SearchOverlayMode.File,
            SelectedRecommendationDisplayCount = 6,
            SelectedSearchOverlayWidthPercent = 68,
            SelectedSearchOverlayHeightPercent = 63,
            SelectedSearchInputWidthPercent = 57,
            SelectedSearchInputBoxHeight = 44,
            SelectedSidebarWidth = 320,
            SelectedSidebarHeightPercent = 73,
            SelectedCategoryTabsPerRow = 3,
            SelectedCategoryFontSize = 14,
            SelectedPhraseFontSize = 15,
            SelectedPhraseRowSpacing = 6,
            SelectedSendHotkeyMode = SendHotkeyMode.Enter,
            SelectedFileLibraryFolderPath = @"D:\Library",
            SelectedPhraseImageFolderPath = @"D:\PhraseImages",
            SelectedRestoreFocusDelayMs = 180,
            SelectedActivationRetryDelayMs = 280,
            SelectedSendDelayMs = 90,
            SelectedClipboardRestoreDelayMs = 70,
            SelectedTextPasteDelayMs = 75,
            SelectedImagePasteDelayMs = 105,
            SelectedRichImageSendDelayMs = 115,
            SelectedRichImageClipboardRestoreDelayMs = 125,
            SelectedPhraseHoverPreviewDelayMs = 500,
            SelectedLogScreenshotRetentionDays = 30,
            SelectedLogScreenshotMaxSizeMb = 4096,
            SelectedLogTextRetentionDays = 90,
            SelectedCategoryColorHex = "#AA1100",
            SelectedCategoryOpacityPercent = 60,
            SelectedTagColorHex = "#BB2200",
            SelectedTagOpacityPercent = 50,
            SelectedPhraseColorHex = "#CC3300",
            SelectedPhraseOpacityPercent = 40,
            SelectedSidebarColorHex = "#DD4400",
            SelectedSidebarOpacityPercent = 70,
            SelectedSearchOverlayColorHex = "#EE5500",
            SelectedSearchOverlayOpacityPercent = 80,
            SelectedSearchInputColorHex = "#FF6600",
            SelectedSearchInputOpacityPercent = 90
        };
        var settings = new AppSettings();

        SettingsWindowSelectionMapper.ApplySelectionsToSettings(selections, settings);

        Assert.Equal(selections.SelectedRunAsAdministrator, settings.RunAsAdministrator);
        Assert.Equal(selections.SelectedLaunchAtStartup, settings.LaunchAtStartup);
        Assert.Equal(selections.SelectedIssueReportDirectory, settings.IssueReportDirectory);
        Assert.Equal(selections.SelectedAllowRawConversationCloudSync, settings.AllowRawConversationCloudSync);
        Assert.Equal(selections.SelectedAllowAutomaticIssueReportUpload, settings.AllowAutomaticIssueReportUpload);
        Assert.Equal(selections.SelectedSyncApiToken, settings.SyncApiToken);
        Assert.Equal(selections.SelectedDefaultSearchOverlayMode, settings.DefaultSearchOverlayMode);
        Assert.Equal(selections.SelectedSendHotkeyMode, settings.SendHotkeyMode);
        Assert.Equal(selections.SelectedPhraseImageFolderPath, settings.PhraseImageFolderPath);
        Assert.Equal(selections.SelectedRichImageClipboardRestoreDelayMs, settings.RichImageClipboardRestoreDelayMs);
        Assert.Equal(selections.SelectedLogTextRetentionDays, settings.LogTextRetentionDays);
        Assert.Equal(selections.SelectedSidebarOpacityPercent, settings.SidebarOpacityPercent);
    }

    private sealed class FakeSettingsWindowSelections : ISettingsWindowSelections
    {
        public SearchFocusHotkeyMode SelectedSearchHotkeyMode { get; set; }
        public SearchTextSendShortcutMode SelectedSearchTextSendShortcutMode { get; set; }
        public SidebarWorkMode SelectedSidebarWorkMode { get; set; }
        public bool SelectedRunAsAdministrator { get; set; }
        public bool SelectedLaunchAtStartup { get; set; }
        public bool SelectedLaunchScreenshotToolWithApp { get; set; }
        public string SelectedScreenshotToolHotkey { get; set; } = string.Empty;
        public string SelectedScreenshotToolSaveDirectory { get; set; } = string.Empty;
        public string SelectedIssueReportDirectory { get; set; } = string.Empty;
        public bool SelectedEnableCloudSync { get; set; }
        public bool SelectedAllowRawConversationCloudSync { get; set; }
        public bool SelectedAllowAutomaticIssueReportUpload { get; set; }
        public bool SelectedEnableUpdateChecks { get; set; }
        public string SelectedServerBaseUrl { get; set; } = string.Empty;
        public string SelectedSyncClientId { get; set; } = string.Empty;
        public string SelectedSyncApiToken { get; set; } = string.Empty;
        public PhraseImageSendMode SelectedPhraseImageSendMode { get; set; }
        public SearchOverlayMode SelectedDefaultSearchOverlayMode { get; set; }
        public int SelectedRecommendationDisplayCount { get; set; }
        public int SelectedSearchOverlayWidthPercent { get; set; }
        public int SelectedSearchOverlayHeightPercent { get; set; }
        public int SelectedSearchInputWidthPercent { get; set; }
        public int SelectedSearchInputBoxHeight { get; set; }
        public int SelectedSidebarWidth { get; set; }
        public int SelectedSidebarHeightPercent { get; set; }
        public int SelectedCategoryTabsPerRow { get; set; }
        public int SelectedCategoryFontSize { get; set; }
        public int SelectedPhraseFontSize { get; set; }
        public int SelectedPhraseRowSpacing { get; set; }
        public SendHotkeyMode SelectedSendHotkeyMode { get; set; }
        public string SelectedFileLibraryFolderPath { get; set; } = string.Empty;
        public string SelectedPhraseImageFolderPath { get; set; } = string.Empty;
        public int SelectedRestoreFocusDelayMs { get; set; }
        public int SelectedActivationRetryDelayMs { get; set; }
        public int SelectedSendDelayMs { get; set; }
        public int SelectedClipboardRestoreDelayMs { get; set; }
        public int SelectedTextPasteDelayMs { get; set; }
        public int SelectedImagePasteDelayMs { get; set; }
        public int SelectedRichImageSendDelayMs { get; set; }
        public int SelectedRichImageClipboardRestoreDelayMs { get; set; }
        public int SelectedPhraseHoverPreviewDelayMs { get; set; }
        public int SelectedLogScreenshotRetentionDays { get; set; }
        public int SelectedLogScreenshotMaxSizeMb { get; set; }
        public int SelectedLogTextRetentionDays { get; set; }
        public string SelectedCategoryColorHex { get; set; } = string.Empty;
        public int SelectedCategoryOpacityPercent { get; set; }
        public string SelectedTagColorHex { get; set; } = string.Empty;
        public int SelectedTagOpacityPercent { get; set; }
        public string SelectedPhraseColorHex { get; set; } = string.Empty;
        public int SelectedPhraseOpacityPercent { get; set; }
        public string SelectedSidebarColorHex { get; set; } = string.Empty;
        public int SelectedSidebarOpacityPercent { get; set; }
        public string SelectedSearchOverlayColorHex { get; set; } = string.Empty;
        public int SelectedSearchOverlayOpacityPercent { get; set; }
        public string SelectedSearchInputColorHex { get; set; } = string.Empty;
        public int SelectedSearchInputOpacityPercent { get; set; }
    }
}
