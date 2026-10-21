using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal static class AppSettingsSnapshotService
{
    public static AppSettings Clone(AppSettings source)
    {
        var clone = new AppSettings();
        Apply(clone, source);
        return clone;
    }

    public static void Apply(AppSettings target, AppSettings source)
    {
        target.SendHotkeyMode = source.SendHotkeyMode;
        target.SearchFocusHotkeyMode = source.SearchFocusHotkeyMode;
        target.SearchTextSendShortcutMode = source.SearchTextSendShortcutMode;
        target.SidebarWorkMode = source.SidebarWorkMode;
        target.RunAsAdministrator = source.RunAsAdministrator;
        target.LaunchAtStartup = source.LaunchAtStartup;
        target.TriggerAppFilterEnabled = source.TriggerAppFilterEnabled;
        target.TriggerAppRules = (source.TriggerAppRules ?? []).Select(rule => rule.Clone()).ToList();
        target.LaunchScreenshotToolWithApp = source.LaunchScreenshotToolWithApp;
        target.ScreenshotToolHotkey = source.ScreenshotToolHotkey;
        target.ScreenshotToolHotkeyMigrationVersion = source.ScreenshotToolHotkeyMigrationVersion;
        target.ScreenshotToolSaveDirectory = source.ScreenshotToolSaveDirectory;
        target.IssueReportDirectory = source.IssueReportDirectory;
        target.EnableCloudSync = source.EnableCloudSync;
        target.AllowRawConversationCloudSync = source.AllowRawConversationCloudSync;
        target.AllowAutomaticIssueReportUpload = source.AllowAutomaticIssueReportUpload;
        target.EnableUpdateChecks = source.EnableUpdateChecks;
        target.ServerBaseUrl = source.ServerBaseUrl;
        target.SyncClientId = source.SyncClientId;
        target.SyncApiToken = source.SyncApiToken;
        target.LastKnowledgeSyncAt = source.LastKnowledgeSyncAt;
        target.LastConversationSyncAt = source.LastConversationSyncAt;
        target.LastPublishedKnowledgeSyncAt = source.LastPublishedKnowledgeSyncAt;
        target.LastPublishedKnowledgeRevision = source.LastPublishedKnowledgeRevision;
        target.LastUpdateCheckAt = source.LastUpdateCheckAt;
        target.LastKnownCloudKnowledgeVersion = source.LastKnownCloudKnowledgeVersion;
        target.LastPublishedKnowledgeVersion = source.LastPublishedKnowledgeVersion;
        target.HasCompletedFirstRunGuide = source.HasCompletedFirstRunGuide;
        target.PhraseImageSendMode = source.PhraseImageSendMode;
        target.PhraseListDensity = source.PhraseListDensity;
        target.FileLibraryFolderPath = source.FileLibraryFolderPath;
        target.PhraseImageFolderPath = source.PhraseImageFolderPath;
        target.RestoreFocusDelayMs = source.RestoreFocusDelayMs;
        target.ActivationRetryDelayMs = source.ActivationRetryDelayMs;
        target.SendDelayMs = source.SendDelayMs;
        target.ClipboardRestoreDelayMs = source.ClipboardRestoreDelayMs;
        target.TextPasteDelayMs = source.TextPasteDelayMs;
        target.ImagePasteDelayMs = source.ImagePasteDelayMs;
        target.RichImageSendDelayMs = source.RichImageSendDelayMs;
        target.RichImageClipboardRestoreDelayMs = source.RichImageClipboardRestoreDelayMs;
        target.PhraseHoverPreviewDelayMs = source.PhraseHoverPreviewDelayMs;
        target.DefaultSearchOverlayMode = source.DefaultSearchOverlayMode;
        target.RecommendationDisplayCount = source.RecommendationDisplayCount;
        target.SearchOverlayWidthPercent = source.SearchOverlayWidthPercent;
        target.SearchOverlayHeightPercent = source.SearchOverlayHeightPercent;
        target.SearchInputWidthPercent = source.SearchInputWidthPercent;
        target.SearchInputBoxHeight = source.SearchInputBoxHeight;
        target.SidebarWidth = source.SidebarWidth;
        target.SidebarHeightPercent = source.SidebarHeightPercent;
        target.CategoryTabsPerRow = source.CategoryTabsPerRow;
        target.CategoryFontSize = source.CategoryFontSize;
        target.PhraseFontSize = source.PhraseFontSize;
        target.PhraseRowSpacing = source.PhraseRowSpacing;
        target.LogScreenshotRetentionDays = source.LogScreenshotRetentionDays;
        target.LogScreenshotMaxSizeMb = source.LogScreenshotMaxSizeMb;
        target.LogTextRetentionDays = source.LogTextRetentionDays;
        target.CategoryColorHex = source.CategoryColorHex;
        target.CategoryOpacityPercent = source.CategoryOpacityPercent;
        target.TagColorHex = source.TagColorHex;
        target.TagOpacityPercent = source.TagOpacityPercent;
        target.PhraseColorHex = source.PhraseColorHex;
        target.PhraseOpacityPercent = source.PhraseOpacityPercent;
        target.SidebarColorHex = source.SidebarColorHex;
        target.SidebarOpacityPercent = source.SidebarOpacityPercent;
        target.SearchOverlayColorHex = source.SearchOverlayColorHex;
        target.SearchOverlayOpacityPercent = source.SearchOverlayOpacityPercent;
        target.SearchInputColorHex = source.SearchInputColorHex;
        target.SearchInputOpacityPercent = source.SearchInputOpacityPercent;
        RetiredFeatureSettingsPolicy.Apply(target);
    }
}
