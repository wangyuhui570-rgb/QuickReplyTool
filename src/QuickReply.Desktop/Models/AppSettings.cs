using QuickReply.Desktop.Services;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop.Models;

public sealed class AppSettings : ObservableObject
{
    private SendHotkeyMode _sendHotkeyMode = SendHotkeyMode.Enter;
    private SearchFocusHotkeyMode _searchFocusHotkeyMode = SearchFocusHotkeyMode.Tab;
    private SearchTextSendShortcutMode _searchTextSendShortcutMode = SearchTextSendShortcutMode.CtrlEnter;
    private SidebarWorkMode _sidebarWorkMode = SidebarWorkMode.Standard;
    private bool _launchAtStartup;
    private bool _launchScreenshotToolWithApp = true;
    private string _screenshotToolHotkey = ScreenshotHotkeyBinding.DefaultHotkey;
    private int _screenshotToolHotkeyMigrationVersion = ScreenshotHotkeyBinding.CurrentMigrationVersion;
    private string _screenshotToolSaveDirectory = string.Empty;
    private string _issueReportDirectory = string.Empty;
    private bool _enableCloudSync;
    private bool _allowRawConversationCloudSync;
    private bool _allowAutomaticIssueReportUpload;
    private bool _enableUpdateChecks;
    private string _serverBaseUrl = QuickReplyServiceEndpointDefaults.DefaultRemoteServiceBaseUrl;
    private bool _enableAiDrafts;
    private string _ollamaBaseUrl = OllamaAiDraftProvider.DefaultBaseUrl;
    private string _ollamaModelName = string.Empty;
    private int _aiDraftCount = 3;
    private int _aiRequestTimeoutMs = 8000;
    private bool _enableAiApiFallback;
    private string _aiApiBaseUrl = string.Empty;
    private string _aiApiModelName = string.Empty;
    private string _aiApiKey = string.Empty;
    private bool _allowRemoteAiForCustomerText;
    private string _syncClientId = Guid.NewGuid().ToString("N");
    private string _syncApiToken = string.Empty;
    private DateTimeOffset? _lastKnowledgeSyncAt;
    private DateTimeOffset? _lastConversationSyncAt;
    private DateTimeOffset? _lastPublishedKnowledgeSyncAt;
    private long _lastPublishedKnowledgeRevision;
    private DateTimeOffset? _lastUpdateCheckAt;
    private string _lastKnownCloudKnowledgeVersion = string.Empty;
    private string _lastPublishedKnowledgeVersion = string.Empty;
    private bool _runAsAdministrator;
    private bool _hasCompletedFirstRunGuide;
    private PhraseImageSendMode _phraseImageSendMode = PhraseImageSendMode.TextThenImageThenSend;
    private PhraseListDensity _phraseListDensity = PhraseListDensity.Compact;
    private string _fileLibraryFolderPath = string.Empty;
    private string _phraseImageFolderPath = string.Empty;
    private int _restoreFocusDelayMs = 120;
    private int _activationRetryDelayMs = 90;
    private int _sendDelayMs = 160;
    private int _clipboardRestoreDelayMs = 120;
    private int _textPasteDelayMs = 120;
    private int _imagePasteDelayMs = 900;
    private int _richImageSendDelayMs = 450;
    private int _richImageClipboardRestoreDelayMs = 2000;
    private int _phraseHoverPreviewDelayMs = 500;
    private SearchOverlayMode _defaultSearchOverlayMode = SearchOverlayMode.Phrase;
    private int _searchOverlayWidthPercent = 100;
    private int _searchOverlayHeightPercent = 36;
    private int _searchInputWidthPercent = 100;
    private int _searchInputBoxHeight = 26;
    private int _recommendationDisplayCount = 3;
    private int _sidebarWidth = 340;
    private int _sidebarHeightPercent = 100;
    private int _categoryTabsPerRow = 4;
    private int _categoryFontSize = 13;
    private int _phraseFontSize = 12;
    private int _phraseRowSpacing;
    private int _logScreenshotRetentionDays = 7;
    private int _logScreenshotMaxSizeMb = 1024;
    private int _logTextRetentionDays = 30;
    private string _categoryColorHex = "#0071E3";
    private int _categoryOpacityPercent = 100;
    private string _tagColorHex = "#E8EEF9";
    private int _tagOpacityPercent = 100;
    private string _phraseColorHex = "#FFFFFF";
    private int _phraseOpacityPercent = 100;
    private string _sidebarColorHex = "#F5F5F7";
    private int _sidebarOpacityPercent = 94;
    private string _searchOverlayColorHex = "#FFFFFF";
    private int _searchOverlayOpacityPercent = 92;
    private string _searchInputColorHex = "#FFFFFF";
    private int _searchInputOpacityPercent = 100;

    internal bool ScreenshotHotkeyMigrationTouched { get; private set; }

    public SendHotkeyMode SendHotkeyMode
    {
        get => _sendHotkeyMode;
        set => SetProperty(ref _sendHotkeyMode, value);
    }

    public SearchFocusHotkeyMode SearchFocusHotkeyMode
    {
        get => _searchFocusHotkeyMode;
        set => SetProperty(ref _searchFocusHotkeyMode, value);
    }

    public SearchTextSendShortcutMode SearchTextSendShortcutMode
    {
        get => _searchTextSendShortcutMode;
        set => SetProperty(ref _searchTextSendShortcutMode, value);
    }

    public SidebarWorkMode SidebarWorkMode
    {
        get => _sidebarWorkMode;
        set => SetProperty(ref _sidebarWorkMode, value);
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set => SetProperty(ref _launchAtStartup, value);
    }

    public bool LaunchScreenshotToolWithApp
    {
        get => _launchScreenshotToolWithApp;
        set => SetProperty(ref _launchScreenshotToolWithApp, value);
    }

    public string ScreenshotToolHotkey
    {
        get => _screenshotToolHotkey;
        set => SetProperty(ref _screenshotToolHotkey, NormalizeScreenshotHotkey(value, ScreenshotHotkeyBinding.DefaultHotkey));
    }

    public int ScreenshotToolHotkeyMigrationVersion
    {
        get => _screenshotToolHotkeyMigrationVersion;
        set => SetProperty(ref _screenshotToolHotkeyMigrationVersion, Math.Max(0, value));
    }

    public string ScreenshotToolSaveDirectory
    {
        get => _screenshotToolSaveDirectory;
        set => SetProperty(ref _screenshotToolSaveDirectory, value?.Trim() ?? string.Empty);
    }

    public string IssueReportDirectory
    {
        get => _issueReportDirectory;
        set => SetProperty(ref _issueReportDirectory, value?.Trim() ?? string.Empty);
    }

    public bool EnableCloudSync
    {
        get => _enableCloudSync;
        set => SetProperty(ref _enableCloudSync, value);
    }

    public bool AllowRawConversationCloudSync
    {
        get => _allowRawConversationCloudSync;
        set => SetProperty(ref _allowRawConversationCloudSync, value);
    }

    public bool AllowAutomaticIssueReportUpload
    {
        get => _allowAutomaticIssueReportUpload;
        set => SetProperty(ref _allowAutomaticIssueReportUpload, value);
    }

    public bool EnableUpdateChecks
    {
        get => _enableUpdateChecks;
        set => SetProperty(ref _enableUpdateChecks, value);
    }

    public string ServerBaseUrl
    {
        get => _serverBaseUrl;
        set => SetProperty(ref _serverBaseUrl, NormalizeConfiguredServiceBaseUrl(value));
    }

    public string SyncClientId
    {
        get => _syncClientId;
        set => SetProperty(ref _syncClientId, string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value.Trim());
    }

    public string SyncApiToken
    {
        get => _syncApiToken;
        set => SetProperty(ref _syncApiToken, value?.Trim() ?? string.Empty);
    }

    public bool EnableAiDrafts
    {
        get => _enableAiDrafts;
        set => SetProperty(ref _enableAiDrafts, value);
    }

    public string OllamaBaseUrl
    {
        get => _ollamaBaseUrl;
        set => SetProperty(ref _ollamaBaseUrl, NormalizeServerBaseUrl(value));
    }

    public string OllamaModelName
    {
        get => _ollamaModelName;
        set => SetProperty(ref _ollamaModelName, value?.Trim() ?? string.Empty);
    }

    public int AiDraftCount
    {
        get => _aiDraftCount;
        set => SetProperty(ref _aiDraftCount, Math.Clamp(value, 1, 3));
    }

    public int AiRequestTimeoutMs
    {
        get => _aiRequestTimeoutMs;
        set => SetProperty(ref _aiRequestTimeoutMs, ClampDelay(value, 1000, 60000, 8000));
    }

    public bool EnableAiApiFallback
    {
        get => _enableAiApiFallback;
        set => SetProperty(ref _enableAiApiFallback, value);
    }

    public string AiApiBaseUrl
    {
        get => _aiApiBaseUrl;
        set => SetProperty(ref _aiApiBaseUrl, NormalizeServerBaseUrl(value));
    }

    public string AiApiModelName
    {
        get => _aiApiModelName;
        set => SetProperty(ref _aiApiModelName, value?.Trim() ?? string.Empty);
    }

    public string AiApiKey
    {
        get => _aiApiKey;
        set => SetProperty(ref _aiApiKey, value?.Trim() ?? string.Empty);
    }

    public bool AllowRemoteAiForCustomerText
    {
        get => _allowRemoteAiForCustomerText;
        set => SetProperty(ref _allowRemoteAiForCustomerText, value);
    }

    public DateTimeOffset? LastKnowledgeSyncAt
    {
        get => _lastKnowledgeSyncAt;
        set => SetProperty(ref _lastKnowledgeSyncAt, value);
    }

    public DateTimeOffset? LastConversationSyncAt
    {
        get => _lastConversationSyncAt;
        set => SetProperty(ref _lastConversationSyncAt, value);
    }

    public DateTimeOffset? LastPublishedKnowledgeSyncAt
    {
        get => _lastPublishedKnowledgeSyncAt;
        set => SetProperty(ref _lastPublishedKnowledgeSyncAt, value);
    }

    public long LastPublishedKnowledgeRevision
    {
        get => _lastPublishedKnowledgeRevision;
        set => SetProperty(ref _lastPublishedKnowledgeRevision, Math.Max(0, value));
    }

    public DateTimeOffset? LastUpdateCheckAt
    {
        get => _lastUpdateCheckAt;
        set => SetProperty(ref _lastUpdateCheckAt, value);
    }

    public string LastKnownCloudKnowledgeVersion
    {
        get => _lastKnownCloudKnowledgeVersion;
        set => SetProperty(ref _lastKnownCloudKnowledgeVersion, value?.Trim() ?? string.Empty);
    }

    public string LastPublishedKnowledgeVersion
    {
        get => _lastPublishedKnowledgeVersion;
        set => SetProperty(ref _lastPublishedKnowledgeVersion, value?.Trim() ?? string.Empty);
    }

    public bool RunAsAdministrator
    {
        get => _runAsAdministrator;
        set => SetProperty(ref _runAsAdministrator, value);
    }

    public bool HasCompletedFirstRunGuide
    {
        get => _hasCompletedFirstRunGuide;
        set => SetProperty(ref _hasCompletedFirstRunGuide, value);
    }

    public PhraseImageSendMode PhraseImageSendMode
    {
        get => _phraseImageSendMode;
        set => SetProperty(ref _phraseImageSendMode, value);
    }

    public PhraseListDensity PhraseListDensity
    {
        get => _phraseListDensity;
        set => SetProperty(ref _phraseListDensity, value);
    }

    public string FileLibraryFolderPath
    {
        get => _fileLibraryFolderPath;
        set => SetProperty(ref _fileLibraryFolderPath, value?.Trim() ?? string.Empty);
    }

    public string PhraseImageFolderPath
    {
        get => _phraseImageFolderPath;
        set => SetProperty(ref _phraseImageFolderPath, value?.Trim() ?? string.Empty);
    }

    public int RestoreFocusDelayMs
    {
        get => _restoreFocusDelayMs;
        set => SetProperty(ref _restoreFocusDelayMs, ClampDelay(value, 50, 2000, 120));
    }

    public int ActivationRetryDelayMs
    {
        get => _activationRetryDelayMs;
        set => SetProperty(ref _activationRetryDelayMs, ClampDelay(value, 50, 2000, 90));
    }

    public int SendDelayMs
    {
        get => _sendDelayMs;
        set => SetProperty(ref _sendDelayMs, ClampDelay(value, 50, 2000, 160));
    }

    public int ClipboardRestoreDelayMs
    {
        get => _clipboardRestoreDelayMs;
        set => SetProperty(ref _clipboardRestoreDelayMs, ClampDelay(value, 50, 2000, 120));
    }

    public int TextPasteDelayMs
    {
        get => _textPasteDelayMs;
        set => SetProperty(ref _textPasteDelayMs, ClampDelay(value, 50, 2000, 120));
    }

    public int ImagePasteDelayMs
    {
        get => _imagePasteDelayMs;
        set => SetProperty(ref _imagePasteDelayMs, ClampDelay(value, 50, 2000, 900));
    }

    public int RichImageSendDelayMs
    {
        get => _richImageSendDelayMs;
        set => SetProperty(ref _richImageSendDelayMs, ClampDelay(value, 50, 2000, 450));
    }

    public int RichImageClipboardRestoreDelayMs
    {
        get => _richImageClipboardRestoreDelayMs;
        set => SetProperty(ref _richImageClipboardRestoreDelayMs, ClampDelay(value, 50, 2000, 2000));
    }

    public int PhraseHoverPreviewDelayMs
    {
        get => _phraseHoverPreviewDelayMs;
        set => SetProperty(ref _phraseHoverPreviewDelayMs, ClampDelay(value, 50, 2000, 500));
    }

    public SearchOverlayMode DefaultSearchOverlayMode
    {
        get => _defaultSearchOverlayMode;
        set => SetProperty(ref _defaultSearchOverlayMode, value);
    }

    public int SearchOverlayWidthPercent
    {
        get => _searchOverlayWidthPercent;
        set => SetProperty(ref _searchOverlayWidthPercent, Math.Clamp(value, 40, 100));
    }

    public int SearchOverlayHeightPercent
    {
        get => _searchOverlayHeightPercent;
        set => SetProperty(ref _searchOverlayHeightPercent, Math.Clamp(value, 18, 56));
    }

    public int SearchInputWidthPercent
    {
        get => _searchInputWidthPercent;
        set => SetProperty(ref _searchInputWidthPercent, Math.Clamp(value, 35, 100));
    }

    public int SearchInputBoxHeight
    {
        get => _searchInputBoxHeight;
        set => SetProperty(ref _searchInputBoxHeight, Math.Clamp(value, 26, 64));
    }

    public int RecommendationDisplayCount
    {
        get => _recommendationDisplayCount;
        set => SetProperty(ref _recommendationDisplayCount, Math.Clamp(value, 1, 5));
    }

    public int SidebarWidth
    {
        get => _sidebarWidth;
        set => SetProperty(ref _sidebarWidth, Math.Clamp(value, 180, 620));
    }

    public int SidebarHeightPercent
    {
        get => _sidebarHeightPercent;
        set => SetProperty(ref _sidebarHeightPercent, Math.Clamp(value, 40, 100));
    }

    public int CategoryTabsPerRow
    {
        get => _categoryTabsPerRow;
        set => SetProperty(ref _categoryTabsPerRow, Math.Clamp(value, 3, 10));
    }

    public int CategoryFontSize
    {
        get => _categoryFontSize;
        set => SetProperty(ref _categoryFontSize, Math.Clamp(value, 8, 20));
    }

    public int PhraseFontSize
    {
        get => _phraseFontSize;
        set => SetProperty(ref _phraseFontSize, Math.Clamp(value, 8, 22));
    }

    public int PhraseRowSpacing
    {
        get => _phraseRowSpacing;
        set => SetProperty(ref _phraseRowSpacing, Math.Clamp(value, 0, 14));
    }

    public int LogScreenshotRetentionDays
    {
        get => _logScreenshotRetentionDays;
        set => SetProperty(ref _logScreenshotRetentionDays, value);
    }

    public int LogScreenshotMaxSizeMb
    {
        get => _logScreenshotMaxSizeMb;
        set => SetProperty(ref _logScreenshotMaxSizeMb, value);
    }

    public int LogTextRetentionDays
    {
        get => _logTextRetentionDays;
        set => SetProperty(ref _logTextRetentionDays, Math.Clamp(value, 3, 180));
    }

    public string CategoryColorHex
    {
        get => _categoryColorHex;
        set => SetProperty(ref _categoryColorHex, NormalizeColorHex(value, "#0071E3"));
    }

    public int CategoryOpacityPercent
    {
        get => _categoryOpacityPercent;
        set => SetProperty(ref _categoryOpacityPercent, ClampPercent(value));
    }

    public string TagColorHex
    {
        get => _tagColorHex;
        set => SetProperty(ref _tagColorHex, NormalizeColorHex(value, "#E8EEF9"));
    }

    public int TagOpacityPercent
    {
        get => _tagOpacityPercent;
        set => SetProperty(ref _tagOpacityPercent, ClampPercent(value));
    }

    public string PhraseColorHex
    {
        get => _phraseColorHex;
        set => SetProperty(ref _phraseColorHex, NormalizeColorHex(value, "#FFFFFF"));
    }

    public int PhraseOpacityPercent
    {
        get => _phraseOpacityPercent;
        set => SetProperty(ref _phraseOpacityPercent, ClampPercent(value));
    }

    public string SidebarColorHex
    {
        get => _sidebarColorHex;
        set => SetProperty(ref _sidebarColorHex, NormalizeColorHex(value, "#F5F5F7"));
    }

    public int SidebarOpacityPercent
    {
        get => _sidebarOpacityPercent;
        set => SetProperty(ref _sidebarOpacityPercent, ClampPercent(value));
    }

    public string SearchOverlayColorHex
    {
        get => _searchOverlayColorHex;
        set => SetProperty(ref _searchOverlayColorHex, NormalizeColorHex(value, "#FFFFFF"));
    }

    public int SearchOverlayOpacityPercent
    {
        get => _searchOverlayOpacityPercent;
        set => SetProperty(ref _searchOverlayOpacityPercent, ClampPercent(value));
    }

    public string SearchInputColorHex
    {
        get => _searchInputColorHex;
        set => SetProperty(ref _searchInputColorHex, NormalizeColorHex(value, "#FFFFFF"));
    }

    public int SearchInputOpacityPercent
    {
        get => _searchInputOpacityPercent;
        set => SetProperty(ref _searchInputOpacityPercent, ClampPercent(value));
    }

    public bool EnsureScreenshotHotkeyMigrationApplied()
    {
        if (ScreenshotToolHotkeyMigrationVersion >= ScreenshotHotkeyBinding.CurrentMigrationVersion)
        {
            return false;
        }

        if (ScreenshotHotkeyBinding.ShouldMigrateLegacyDefault(ScreenshotToolHotkey))
        {
            ScreenshotToolHotkey = ScreenshotHotkeyBinding.DefaultHotkey;
        }

        ScreenshotToolHotkeyMigrationVersion = ScreenshotHotkeyBinding.CurrentMigrationVersion;
        ScreenshotHotkeyMigrationTouched = true;
        return true;
    }

    private static int ClampPercent(int value)
    {
        return Math.Clamp(value, 10, 100);
    }

    private static int ClampDelay(int value, int min, int max, int fallback)
    {
        if (value <= 0)
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

    private static string NormalizeColorHex(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizeScreenshotHotkey(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return ScreenshotHotkeyBinding.NormalizeOrDefault(value);
    }

    private static string NormalizeServerBaseUrl(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.TrimEnd('/');
    }

    private static string NormalizeConfiguredServiceBaseUrl(string? value)
    {
        return QuickReplyServiceEndpointDefaults.NormalizeConfiguredServiceBaseUrl(value);
    }
}
