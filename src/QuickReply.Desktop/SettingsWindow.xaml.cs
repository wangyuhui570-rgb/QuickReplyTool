using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Collections.ObjectModel;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.ViewModels;
using Forms = System.Windows.Forms;

namespace QuickReply.Desktop;

public partial class SettingsWindow : Window, INotifyPropertyChanged, ISettingsWindowSelections
{
    public const string GeneralSectionKey = "general";
    public const string SearchSectionKey = "search";
    public const string QuickActionsSectionKey = "quick-actions";
    public const string DelaySectionKey = "delay-control";
    public const string AppearanceSectionKey = "appearance";
    public const string FilesSectionKey = "files";

    private sealed record ColorPreset(string Name, string Hex);
    private sealed class CurrentColorBrushConverter(Func<string> currentColorAccessor) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var hex = NormalizeColorHex(currentColorAccessor(), "#FFFFFF");
            return CreateBrush(hex);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    private sealed record ThemePalettePreset(string Name, AppearanceThemePreset Theme);

    public sealed class PhraseUsageDisplayItem
    {
        public required int Rank { get; init; }

        public required string DisplayName { get; init; }

        public required string ContentPreview { get; init; }

        public required string CountText { get; init; }

        public required string LastUsedText { get; init; }

        public required double HeatPercent { get; init; }
    }

    public sealed class ExceptionDisplayItem
    {
        public required string OccurredAtText { get; init; }

        public required string SourceText { get; init; }

        public required string ExceptionType { get; init; }

        public required string Message { get; init; }

        public required string ContextText { get; init; }

        public bool IsTerminating { get; init; }
    }

    private static readonly IReadOnlyList<ColorPreset> CommonColorPresets =
    [
        new("云蓝", "#D8EAFE"),
        new("浅蓝", "#DBEAFE"),
        new("青绿", "#CCFBF1"),
        new("豆绿", "#DCFCE7"),
        new("米黄", "#FEF3C7"),
        new("蜜桃", "#FED7AA"),
        new("浅红", "#FECACA"),
        new("浅紫", "#E9D5FF"),
        new("浅灰", "#E2E8F0"),
        new("白色", "#FFFFFF")
    ];

    private static readonly IReadOnlyList<ThemePalettePreset> BuiltInThemePresets =
    [
        new("雾青", new AppearanceThemePreset
        {
            CategoryColorHex = "#D7E9E4",
            TagColorHex = "#DCEEF8",
            PhraseColorHex = "#F7FBFA",
            SidebarColorHex = "#F4F8F7",
            SearchOverlayColorHex = "#F7FBFA",
            SearchInputColorHex = "#FFFFFF",
            SidebarOpacityPercent = 96,
            SearchOverlayOpacityPercent = 96,
            SearchInputOpacityPercent = 100
        }),
        new("纸白", new AppearanceThemePreset
        {
            CategoryColorHex = "#E7E5E4",
            TagColorHex = "#E2E8F0",
            PhraseColorHex = "#FFFFFF",
            SidebarColorHex = "#FAFAF9",
            SearchOverlayColorHex = "#FFFFFF",
            SearchInputColorHex = "#FFFFFF"
        }),
        new("晚樱", new AppearanceThemePreset
        {
            CategoryColorHex = "#FBCFE8",
            TagColorHex = "#FCE7F3",
            PhraseColorHex = "#FFF8FB",
            SidebarColorHex = "#FFF7FA",
            SearchOverlayColorHex = "#FFF8FB",
            SearchInputColorHex = "#FFFFFF"
        }),
        new("松烟", new AppearanceThemePreset
        {
            CategoryColorHex = "#C7D2FE",
            TagColorHex = "#BFDBFE",
            PhraseColorHex = "#EEF2FF",
            SidebarColorHex = "#F5F7FB",
            SearchOverlayColorHex = "#F8FAFC",
            SearchInputColorHex = "#FFFFFF"
        }),
        new("杏雨", new AppearanceThemePreset
        {
            CategoryColorHex = "#FDE68A",
            TagColorHex = "#FED7AA",
            PhraseColorHex = "#FFFDF5",
            SidebarColorHex = "#FFFBF0",
            SearchOverlayColorHex = "#FFFDF5",
            SearchInputColorHex = "#FFFFFF"
        })
    ];

    private readonly MainViewModel _viewModel;
    private readonly FolderSelectionDialogService _folderSelectionDialogService = new();
    private readonly ObservableCollection<AppearanceThemePreset> _customAppearanceThemes = [];
    private string _selectedFileLibraryFolderPath = string.Empty;
    private string _selectedPhraseImageFolderPath = string.Empty;
    private string _logDatabasePath = string.Empty;
    private string _logCaptureFolderPath = string.Empty;
    private string _logSessionSummary = "正在读取...";
    private string _logTotalSummary = "正在读取...";
    private string _logStorageSummary = "正在读取...";
    private string _logPathSummary = "正在读取...";
    private string _usageLaunchSummary = "正在读取...";
    private string _usageRuntimeSummary = "正在读取...";
    private string _usageMostUsedSummary = "正在读取...";
    private string _usageLeastUsedSummary = "正在读取...";
    private string _usageMostUsedEmptyMessage = "正在读取...";
    private string _usageLeastUsedEmptyMessage = "正在读取...";
    private string _cloudKnowledgeSyncSummary = "正在读取...";
    private string _cloudConversationSyncSummary = "正在读取...";
    private string _cloudUpdateCheckSummary = "正在读取...";
    private string _cloudKnowledgeVersionSummary = "正在读取...";
    private string _cloudActionSummary = "正在读取...";
    private string _exceptionSummary = "正在读取...";
    private string _exceptionEmptyMessage = "正在读取...";
    private string _validationSummary = string.Empty;
    private string _exceptionLogFolderPath = string.Empty;
    private string _selectedSectionKey = GeneralSectionKey;
    private SearchFocusHotkeyMode _selectedSearchHotkeyMode;
    private SearchTextSendShortcutMode _selectedSearchTextSendShortcutMode = SearchTextSendShortcutMode.CtrlEnter;
    private SidebarWorkMode _selectedSidebarWorkMode = SidebarWorkMode.Standard;
    private bool _selectedLaunchAtStartup;
    private bool _selectedLaunchScreenshotToolWithApp = true;
    private string _selectedScreenshotToolHotkey = ScreenshotHotkeyBinding.DefaultHotkey;
    private string _selectedScreenshotToolSaveDirectory = string.Empty;
    private string _selectedIssueReportDirectory = string.Empty;
    private bool _selectedEnableCloudSync;
    private bool _selectedAllowRawConversationCloudSync;
    private bool _selectedAllowAutomaticIssueReportUpload;
    private bool _selectedEnableUpdateChecks;
    private string _selectedServerBaseUrl = QuickReplyServiceEndpointDefaults.DefaultRemoteServiceBaseUrl;
    private string _selectedSyncClientId = string.Empty;
    private string _selectedSyncApiToken = string.Empty;
    private bool _selectedRunAsAdministrator;
    private PhraseImageSendMode _selectedPhraseImageSendMode;
    private SearchOverlayMode _selectedDefaultSearchOverlayMode;
    private int _selectedSearchOverlayWidthPercent = 100;
    private int _selectedSearchOverlayHeightPercent = 32;
    private int _selectedSearchInputWidthPercent = 100;
    private int _selectedSearchInputBoxHeight = 36;
    private int _selectedRecommendationDisplayCount = 3;
    private int _selectedSidebarWidth = 300;
    private int _selectedSidebarHeightPercent = 100;
    private int _selectedCategoryTabsPerRow = 5;
    private int _selectedCategoryFontSize = 12;
    private int _selectedPhraseFontSize = 11;
    private int _selectedPhraseRowSpacing = 1;
    private SendHotkeyMode _selectedSendHotkeyMode;
    private int _selectedRestoreFocusDelayMs = 120;
    private int _selectedActivationRetryDelayMs = 90;
    private int _selectedSendDelayMs = 160;
    private int _selectedClipboardRestoreDelayMs = 120;
    private int _selectedTextPasteDelayMs = 120;
    private int _selectedImagePasteDelayMs = 900;
    private int _selectedRichImageSendDelayMs = 450;
    private int _selectedRichImageClipboardRestoreDelayMs = 2000;
    private int _selectedPhraseHoverPreviewDelayMs = 500;
    private int _selectedLogScreenshotRetentionDays = 7;
    private int _selectedLogScreenshotMaxSizeMb = 1024;
    private int _selectedLogTextRetentionDays = 30;
    private string _selectedCategoryColorHex = "#D8EAFE";
    private int _selectedCategoryOpacityPercent = 100;
    private string _selectedTagColorHex = "#DBEAFE";
    private int _selectedTagOpacityPercent = 100;
    private string _selectedPhraseColorHex = "#F8FAFC";
    private int _selectedPhraseOpacityPercent = 100;
    private string _selectedSidebarColorHex = "#FFFFFF";
    private int _selectedSidebarOpacityPercent = 96;
    private string _selectedSearchOverlayColorHex = "#FFFFFF";
    private int _selectedSearchOverlayOpacityPercent = 96;
    private string _selectedSearchInputColorHex = "#FFFFFF";
    private int _selectedSearchInputOpacityPercent = 100;
    private bool _isRefreshingStats;
    private bool _isRunningCloudAction;
    private bool _isNavigatingToSection;
    private string _newThemeName = string.Empty;
    private WrapPanel? _customThemePanel;

    public SettingsWindow(
        MainViewModel viewModel,
        AppSettings initialSettings)
    {
        InitializeComponent();
        WindowTransitionService.Attach(this);
        _viewModel = viewModel;
        SettingsWindowSelectionMapper.ApplySettingsToSelections(initialSettings, this);
        DataContext = this;
        RefreshValidationState();
        ConfigureQuickActionsSection();
        ConfigureAppearanceSection();
        ConfigureSidebarAppearancePreviewSection();
        ConfigureSearchOverlayAppearanceSection();
        Loaded += SettingsWindow_OnLoaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AppearanceThemePreset> CustomAppearanceThemes => _customAppearanceThemes;

    public ObservableCollection<PhraseUsageDisplayItem> MostUsedPhraseItems { get; } = [];

    public ObservableCollection<PhraseUsageDisplayItem> LeastUsedPhraseItems { get; } = [];

    public ObservableCollection<ExceptionDisplayItem> RecentExceptionItems { get; } = [];
    public bool WasConfirmed { get; private set; }

    public string NewThemeName
    {
        get => _newThemeName;
        set => SetField(ref _newThemeName, value);
    }

    public SearchFocusHotkeyMode SelectedSearchHotkeyMode
    {
        get => _selectedSearchHotkeyMode;
        set => SetField(ref _selectedSearchHotkeyMode, value);
    }

    public SearchTextSendShortcutMode SelectedSearchTextSendShortcutMode
    {
        get => _selectedSearchTextSendShortcutMode;
        set => SetField(ref _selectedSearchTextSendShortcutMode, value);
    }

    public SidebarWorkMode SelectedSidebarWorkMode
    {
        get => _selectedSidebarWorkMode;
        set => SetField(ref _selectedSidebarWorkMode, value);
    }

    public bool SelectedLaunchAtStartup
    {
        get => _selectedLaunchAtStartup;
        set => SetField(ref _selectedLaunchAtStartup, value);
    }

    public bool SelectedLaunchScreenshotToolWithApp
    {
        get => _selectedLaunchScreenshotToolWithApp;
        set => SetField(ref _selectedLaunchScreenshotToolWithApp, value);
    }

    public string SelectedScreenshotToolHotkey
    {
        get => _selectedScreenshotToolHotkey;
        set => SetField(ref _selectedScreenshotToolHotkey, NormalizeScreenshotHotkey(value));
    }

    public string SelectedScreenshotToolSaveDirectory
    {
        get => _selectedScreenshotToolSaveDirectory;
        set
        {
            var normalizedValue = value?.Trim() ?? string.Empty;
            if (string.Equals(_selectedScreenshotToolSaveDirectory, normalizedValue, StringComparison.Ordinal))
            {
                return;
            }

            _selectedScreenshotToolSaveDirectory = normalizedValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedScreenshotToolSaveDirectory));
            OnPropertyChanged(nameof(ScreenshotToolSaveDirectoryDisplayPath));
            OnPropertyChanged(nameof(ScreenshotToolSaveDirectorySummary));
            RefreshValidationState();
        }
    }

    public bool SelectedRunAsAdministrator
    {
        get => _selectedRunAsAdministrator;
        set => SetField(ref _selectedRunAsAdministrator, value);
    }

    public string SelectedIssueReportDirectory
    {
        get => _selectedIssueReportDirectory;
        set
        {
            var normalizedValue = value?.Trim() ?? string.Empty;
            if (string.Equals(_selectedIssueReportDirectory, normalizedValue, StringComparison.Ordinal))
            {
                return;
            }

            _selectedIssueReportDirectory = normalizedValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedIssueReportDirectory));
            OnPropertyChanged(nameof(IssueReportDirectoryDisplayPath));
            OnPropertyChanged(nameof(IssueReportDirectorySummary));
            RefreshValidationState();
        }
    }

    public bool SelectedEnableCloudSync
    {
        get => _selectedEnableCloudSync;
        set
        {
            if (SetField(ref _selectedEnableCloudSync, value))
            {
                RefreshValidationState();
            }
        }
    }

    public bool SelectedAllowRawConversationCloudSync
    {
        get => _selectedAllowRawConversationCloudSync;
        set => SetField(ref _selectedAllowRawConversationCloudSync, value);
    }

    public bool SelectedAllowAutomaticIssueReportUpload
    {
        get => _selectedAllowAutomaticIssueReportUpload;
        set => SetField(ref _selectedAllowAutomaticIssueReportUpload, value);
    }

    public bool SelectedEnableUpdateChecks
    {
        get => _selectedEnableUpdateChecks;
        set
        {
            if (SetField(ref _selectedEnableUpdateChecks, value))
            {
                RefreshValidationState();
            }
        }
    }

    public string SelectedServerBaseUrl
    {
        get => _selectedServerBaseUrl;
        set
        {
            if (SetField(ref _selectedServerBaseUrl, NormalizeServerBaseUrl(value)))
            {
                RefreshValidationState();
            }
        }
    }

    public string SelectedSyncClientId
    {
        get => _selectedSyncClientId;
        set => SetField(ref _selectedSyncClientId, value?.Trim() ?? string.Empty);
    }

    public string SelectedSyncApiToken
    {
        get => _selectedSyncApiToken;
        set => SetField(ref _selectedSyncApiToken, value?.Trim() ?? string.Empty);
    }

    public PhraseImageSendMode SelectedPhraseImageSendMode
    {
        get => _selectedPhraseImageSendMode;
        set => SetField(ref _selectedPhraseImageSendMode, value);
    }

    public SearchOverlayMode SelectedDefaultSearchOverlayMode
    {
        get => _selectedDefaultSearchOverlayMode;
        set => SetField(ref _selectedDefaultSearchOverlayMode, value);
    }

    public int SelectedSearchOverlayWidthPercent
    {
        get => _selectedSearchOverlayWidthPercent;
        set => SetField(ref _selectedSearchOverlayWidthPercent, value);
    }

    public int SelectedSearchOverlayHeightPercent
    {
        get => _selectedSearchOverlayHeightPercent;
        set => SetField(ref _selectedSearchOverlayHeightPercent, value);
    }

    public int SelectedSearchInputWidthPercent
    {
        get => _selectedSearchInputWidthPercent;
        set => SetField(ref _selectedSearchInputWidthPercent, value);
    }

    public int SelectedSearchInputBoxHeight
    {
        get => _selectedSearchInputBoxHeight;
        set => SetField(ref _selectedSearchInputBoxHeight, value);
    }

    public int SelectedRecommendationDisplayCount
    {
        get => _selectedRecommendationDisplayCount;
        set => SetField(ref _selectedRecommendationDisplayCount, value);
    }

    public int SelectedSidebarWidth
    {
        get => _selectedSidebarWidth;
        set => SetField(ref _selectedSidebarWidth, value);
    }

    public int SelectedSidebarHeightPercent
    {
        get => _selectedSidebarHeightPercent;
        set => SetField(ref _selectedSidebarHeightPercent, value);
    }

    public int SelectedCategoryTabsPerRow
    {
        get => _selectedCategoryTabsPerRow;
        set => SetField(ref _selectedCategoryTabsPerRow, value);
    }

    public int SelectedCategoryFontSize
    {
        get => _selectedCategoryFontSize;
        set => SetField(ref _selectedCategoryFontSize, value);
    }

    public int SelectedPhraseFontSize
    {
        get => _selectedPhraseFontSize;
        set => SetField(ref _selectedPhraseFontSize, value);
    }

    public int SelectedPhraseRowSpacing
    {
        get => _selectedPhraseRowSpacing;
        set => SetField(ref _selectedPhraseRowSpacing, value);
    }

    public SendHotkeyMode SelectedSendHotkeyMode
    {
        get => _selectedSendHotkeyMode;
        set => SetField(ref _selectedSendHotkeyMode, value);
    }

    public int SelectedRestoreFocusDelayMs
    {
        get => _selectedRestoreFocusDelayMs;
        set => SetField(ref _selectedRestoreFocusDelayMs, NormalizeDelaySelection(value, 120));
    }

    public int SelectedActivationRetryDelayMs
    {
        get => _selectedActivationRetryDelayMs;
        set => SetField(ref _selectedActivationRetryDelayMs, NormalizeDelaySelection(value, 90));
    }

    public int SelectedSendDelayMs
    {
        get => _selectedSendDelayMs;
        set => SetField(ref _selectedSendDelayMs, NormalizeDelaySelection(value, 160));
    }

    public int SelectedClipboardRestoreDelayMs
    {
        get => _selectedClipboardRestoreDelayMs;
        set => SetField(ref _selectedClipboardRestoreDelayMs, NormalizeDelaySelection(value, 120));
    }

    public int SelectedTextPasteDelayMs
    {
        get => _selectedTextPasteDelayMs;
        set => SetField(ref _selectedTextPasteDelayMs, NormalizeDelaySelection(value, 120));
    }

    public int SelectedImagePasteDelayMs
    {
        get => _selectedImagePasteDelayMs;
        set => SetField(ref _selectedImagePasteDelayMs, NormalizeDelaySelection(value, 900));
    }

    public int SelectedRichImageSendDelayMs
    {
        get => _selectedRichImageSendDelayMs;
        set => SetField(ref _selectedRichImageSendDelayMs, NormalizeDelaySelection(value, 450));
    }

    public int SelectedRichImageClipboardRestoreDelayMs
    {
        get => _selectedRichImageClipboardRestoreDelayMs;
        set => SetField(ref _selectedRichImageClipboardRestoreDelayMs, NormalizeDelaySelection(value, 2000));
    }

    public int SelectedPhraseHoverPreviewDelayMs
    {
        get => _selectedPhraseHoverPreviewDelayMs;
        set => SetField(ref _selectedPhraseHoverPreviewDelayMs, NormalizeDelaySelection(value, 500));
    }

    public int SelectedLogScreenshotRetentionDays
    {
        get => _selectedLogScreenshotRetentionDays;
        set => SetField(ref _selectedLogScreenshotRetentionDays, value);
    }

    public int SelectedLogScreenshotMaxSizeMb
    {
        get => _selectedLogScreenshotMaxSizeMb;
        set => SetField(ref _selectedLogScreenshotMaxSizeMb, value);
    }

    public int SelectedLogTextRetentionDays
    {
        get => _selectedLogTextRetentionDays;
        set => SetField(ref _selectedLogTextRetentionDays, value);
    }

    public string SelectedCategoryColorHex
    {
        get => _selectedCategoryColorHex;
        set => SetField(ref _selectedCategoryColorHex, NormalizeColorHex(value, "#D8EAFE"));
    }

    public int SelectedCategoryOpacityPercent
    {
        get => _selectedCategoryOpacityPercent;
        set => SetField(ref _selectedCategoryOpacityPercent, value);
    }

    public string SelectedTagColorHex
    {
        get => _selectedTagColorHex;
        set => SetField(ref _selectedTagColorHex, NormalizeColorHex(value, "#DBEAFE"));
    }

    public int SelectedTagOpacityPercent
    {
        get => _selectedTagOpacityPercent;
        set => SetField(ref _selectedTagOpacityPercent, value);
    }

    public string SelectedPhraseColorHex
    {
        get => _selectedPhraseColorHex;
        set => SetField(ref _selectedPhraseColorHex, NormalizeColorHex(value, "#F8FAFC"));
    }

    public int SelectedPhraseOpacityPercent
    {
        get => _selectedPhraseOpacityPercent;
        set => SetField(ref _selectedPhraseOpacityPercent, value);
    }

    public string SelectedSidebarColorHex
    {
        get => _selectedSidebarColorHex;
        set => SetField(ref _selectedSidebarColorHex, NormalizeColorHex(value, "#FFFFFF"));
    }

    public int SelectedSidebarOpacityPercent
    {
        get => _selectedSidebarOpacityPercent;
        set => SetField(ref _selectedSidebarOpacityPercent, value);
    }

    public string SelectedSearchOverlayColorHex
    {
        get => _selectedSearchOverlayColorHex;
        set => SetField(ref _selectedSearchOverlayColorHex, NormalizeColorHex(value, "#FFFFFF"));
    }

    public int SelectedSearchOverlayOpacityPercent
    {
        get => _selectedSearchOverlayOpacityPercent;
        set => SetField(ref _selectedSearchOverlayOpacityPercent, value);
    }

    public string SelectedSearchInputColorHex
    {
        get => _selectedSearchInputColorHex;
        set => SetField(ref _selectedSearchInputColorHex, NormalizeColorHex(value, "#FFFFFF"));
    }

    public int SelectedSearchInputOpacityPercent
    {
        get => _selectedSearchInputOpacityPercent;
        set => SetField(ref _selectedSearchInputOpacityPercent, value);
    }

    public System.Windows.Media.Brush SelectedCategoryColorPreviewBrush => BuildPreviewBrush(SelectedCategoryColorHex, 100, "#D8EAFE");

    public System.Windows.Media.Brush SelectedTagColorPreviewBrush => BuildPreviewBrush(SelectedTagColorHex, 100, "#DBEAFE");

    public System.Windows.Media.Brush SelectedPhraseColorPreviewBrush => BuildPreviewBrush(SelectedPhraseColorHex, 100, "#F8FAFC");

    public System.Windows.Media.Brush SelectedSidebarColorPreviewBrush => BuildPreviewBrush(SelectedSidebarColorHex, 100, "#FFFFFF");

    public System.Windows.Media.Brush SelectedSearchOverlayColorPreviewBrush => BuildPreviewBrush(SelectedSearchOverlayColorHex, 100, "#FFFFFF");

    public System.Windows.Media.Brush SelectedSearchInputColorPreviewBrush => BuildPreviewBrush(SelectedSearchInputColorHex, 100, "#FFFFFF");

    public string SelectedFileLibraryFolderPath
    {
        get => _selectedFileLibraryFolderPath;
        set
        {
            var normalizedValue = value?.Trim() ?? string.Empty;
            if (string.Equals(_selectedFileLibraryFolderPath, normalizedValue, StringComparison.Ordinal))
            {
                return;
            }

            _selectedFileLibraryFolderPath = normalizedValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FileLibraryFolderSummary));
            OnPropertyChanged(nameof(HasSelectedFileLibraryFolder));
            RefreshValidationState();
        }
    }

    public string FileLibraryFolderSummary => string.IsNullOrWhiteSpace(SelectedFileLibraryFolderPath)
        ? $"{GetEffectiveFileLibraryFolderPath()}{Environment.NewLine}还没有设置素材文件夹。你可以直接点“打开文件夹”，程序会先创建一个默认素材目录，再作为当前素材目录使用。"
        : SelectedFileLibraryFolderPath;

    public bool HasSelectedFileLibraryFolder => !string.IsNullOrWhiteSpace(SelectedFileLibraryFolderPath);

    public string SelectedPhraseImageFolderPath
    {
        get => _selectedPhraseImageFolderPath;
        set
        {
            var normalizedValue = value?.Trim() ?? string.Empty;
            if (string.Equals(_selectedPhraseImageFolderPath, normalizedValue, StringComparison.Ordinal))
            {
                return;
            }

            _selectedPhraseImageFolderPath = normalizedValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PhraseImageFolderSummary));
            OnPropertyChanged(nameof(HasSelectedPhraseImageFolder));
            OnPropertyChanged(nameof(PhraseImageFolderDisplayPath));
            RefreshValidationState();
        }
    }

    public bool HasSelectedPhraseImageFolder => !string.IsNullOrWhiteSpace(SelectedPhraseImageFolderPath);

    public string PhraseImageFolderDisplayPath => HasSelectedPhraseImageFolder
        ? SelectedPhraseImageFolderPath
        : _viewModel.DefaultPhraseImageFolderPath;

    public string PhraseImageFolderSummary => HasSelectedPhraseImageFolder
        ? $"{SelectedPhraseImageFolderPath}{Environment.NewLine}已切换为自定义图片目录。程序会把绑定到快捷短语的图片副本保存到这里，后续发送和编辑都直接从这里读取。"
        : $"{_viewModel.DefaultPhraseImageFolderPath}{Environment.NewLine}当前使用默认图片目录。程序会把拖入或粘贴到短语里的图片复制到这里，避免原图被移动后短语失效。";

    public bool HasSelectedScreenshotToolSaveDirectory => !string.IsNullOrWhiteSpace(SelectedScreenshotToolSaveDirectory);

    public string ScreenshotToolSaveDirectoryDisplayPath => HasSelectedScreenshotToolSaveDirectory
        ? SelectedScreenshotToolSaveDirectory
        : GetDefaultScreenshotToolSaveDirectory();

    public string ScreenshotToolSaveDirectorySummary => HasSelectedScreenshotToolSaveDirectory
        ? $"{SelectedScreenshotToolSaveDirectory}{Environment.NewLine}截图工具会把手动保存的图片默认落到这里。"
        : $"{GetDefaultScreenshotToolSaveDirectory()}{Environment.NewLine}当前使用默认保存目录。未自定义时会保存到桌面。";

    public bool HasSelectedIssueReportDirectory => !string.IsNullOrWhiteSpace(SelectedIssueReportDirectory);

    public string IssueReportDirectoryDisplayPath => HasSelectedIssueReportDirectory
        ? SelectedIssueReportDirectory
        : GetDefaultIssueReportDirectory();

    public string IssueReportDirectorySummary => HasSelectedIssueReportDirectory
        ? $"{SelectedIssueReportDirectory}{Environment.NewLine}从托盘菜单生成的问题反馈文件会直接保存到这里。"
        : $"{GetDefaultIssueReportDirectory()}{Environment.NewLine}当前使用默认反馈目录。未自定义时会保存到用户数据目录下的 feedback 文件夹。";

    public string UserDataFolderSummary => _viewModel.UserDataRootPath;

    public string LogSessionSummary
    {
        get => _logSessionSummary;
        private set => SetField(ref _logSessionSummary, value);
    }

    public string LogTotalSummary
    {
        get => _logTotalSummary;
        private set => SetField(ref _logTotalSummary, value);
    }

    public string LogStorageSummary
    {
        get => _logStorageSummary;
        private set => SetField(ref _logStorageSummary, value);
    }

    public string LogPathSummary
    {
        get => _logPathSummary;
        private set => SetField(ref _logPathSummary, value);
    }

    public string UsageLaunchSummary
    {
        get => _usageLaunchSummary;
        private set => SetField(ref _usageLaunchSummary, value);
    }

    public string UsageRuntimeSummary
    {
        get => _usageRuntimeSummary;
        private set => SetField(ref _usageRuntimeSummary, value);
    }

    public string UsageMostUsedSummary
    {
        get => _usageMostUsedSummary;
        private set => SetField(ref _usageMostUsedSummary, value);
    }

    public string UsageLeastUsedSummary
    {
        get => _usageLeastUsedSummary;
        private set => SetField(ref _usageLeastUsedSummary, value);
    }

    public string UsageMostUsedEmptyMessage
    {
        get => _usageMostUsedEmptyMessage;
        private set => SetField(ref _usageMostUsedEmptyMessage, value);
    }

    public string UsageLeastUsedEmptyMessage
    {
        get => _usageLeastUsedEmptyMessage;
        private set => SetField(ref _usageLeastUsedEmptyMessage, value);
    }

    public string CloudKnowledgeSyncSummary
    {
        get => _cloudKnowledgeSyncSummary;
        private set => SetField(ref _cloudKnowledgeSyncSummary, value);
    }

    public string CloudConversationSyncSummary
    {
        get => _cloudConversationSyncSummary;
        private set => SetField(ref _cloudConversationSyncSummary, value);
    }

    public string CloudUpdateCheckSummary
    {
        get => _cloudUpdateCheckSummary;
        private set => SetField(ref _cloudUpdateCheckSummary, value);
    }

    public string CloudKnowledgeVersionSummary
    {
        get => _cloudKnowledgeVersionSummary;
        private set => SetField(ref _cloudKnowledgeVersionSummary, value);
    }

    public string CloudActionSummary
    {
        get => _cloudActionSummary;
        private set => SetField(ref _cloudActionSummary, value);
    }

    public string ValidationSummary
    {
        get => _validationSummary;
        private set => SetField(ref _validationSummary, value);
    }

    public bool HasValidationErrors => !string.IsNullOrWhiteSpace(ValidationSummary);

    public bool CanSave => !HasValidationErrors;

    public string CloudActionAvailabilitySummary => SettingsInputValidator.GetCloudActionAvailabilitySummary(SelectedServerBaseUrl);

    public System.Windows.Media.Brush CloudActionAvailabilityBrush => SettingsInputValidator.CanRunCloudSyncActions(SelectedServerBaseUrl)
        ? CreateBrush("#64748B")
        : CreateBrush("#C9342C");

    public bool HasMostUsedPhraseItems => MostUsedPhraseItems.Count > 0;

    public bool HasLeastUsedPhraseItems => LeastUsedPhraseItems.Count > 0;

    public string ExceptionSummary
    {
        get => _exceptionSummary;
        private set => SetField(ref _exceptionSummary, value);
    }

    public string ExceptionEmptyMessage
    {
        get => _exceptionEmptyMessage;
        private set => SetField(ref _exceptionEmptyMessage, value);
    }

    public bool HasRecentExceptionItems => RecentExceptionItems.Count > 0;

    public bool IsRefreshingStats
    {
        get => _isRefreshingStats;
        private set
        {
            if (SetField(ref _isRefreshingStats, value))
            {
                OnPropertyChanged(nameof(CanRefreshStats));
            }
        }
    }

    public bool CanRefreshStats => !IsRefreshingStats;

    public bool IsRunningCloudAction
    {
        get => _isRunningCloudAction;
        private set
        {
            if (SetField(ref _isRunningCloudAction, value))
            {
                OnPropertyChanged(nameof(CanRunCloudSyncActions));
                OnPropertyChanged(nameof(CanRunUpdateActions));
            }
        }
    }

    public bool CanRunCloudSyncActions => !IsRunningCloudAction && SettingsInputValidator.CanRunCloudSyncActions(SelectedServerBaseUrl);

    public bool CanRunUpdateActions => !IsRunningCloudAction && SettingsInputValidator.CanRunUpdateActions(SelectedServerBaseUrl);

    public string SelectedSectionKey
    {
        get => _selectedSectionKey;
        private set => SetField(ref _selectedSectionKey, value);
    }

    public int[] CategoryTabsPerRowOptions { get; } = [3, 4, 5, 6, 7, 8, 9, 10];

    public int[] SearchOverlayWidthPercentOptions { get; } = [40, 50, 60, 70, 80, 90, 100];

    public int[] SearchOverlayHeightPercentOptions { get; } = [18, 22, 26, 30, 34, 38, 42, 48, 56];

    public int[] SearchInputWidthPercentOptions { get; } = [35, 45, 55, 65, 75, 85, 100];

    public int[] SearchInputBoxHeightOptions { get; } = [26, 30, 34, 38, 42, 46, 50, 56, 64];

    public int[] RecommendationDisplayCountOptions { get; } = [1, 2, 3, 4, 5];

    public int[] SidebarWidthOptions { get; } = [180, 220, 260, 300, 340, 380, 420, 480, 540, 600];

    public int[] SidebarHeightPercentOptions { get; } = [40, 50, 60, 70, 80, 90, 100];

    public int[] CategoryFontSizeOptions { get; } = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20];

    public int[] PhraseFontSizeOptions { get; } = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 22];

    public int[] PhraseRowSpacingOptions { get; } = [0, 1, 2, 3, 4, 5, 6, 8, 10, 12, 14];

    public int[] LogScreenshotRetentionDayOptions { get; } = [3, 7, 14, 30, 60];

    public int[] LogScreenshotMaxSizeOptions { get; } = [256, 512, 1024, 2048, 4096];

    public int[] LogTextRetentionDayOptions { get; } = [7, 14, 30, 60, 90, 180];

    public int[] ThemeOpacityOptions { get; } = [20, 35, 50, 65, 80, 90, 100];

    public double SearchPreviewWidth => 160 + ((SelectedSearchOverlayWidthPercent - 40) * 3.75);

    public double SearchPreviewInputHeight => SelectedSearchInputBoxHeight;

    public System.Windows.Media.Brush SearchPreviewBackgroundBrush => BuildPreviewBrush(SelectedSearchOverlayColorHex, SelectedSearchOverlayOpacityPercent, "#FFFFFF");

    public System.Windows.Media.Brush SearchPreviewBorderBrush => BuildPreviewBrush(DarkenColor(ParseColor(SelectedSearchOverlayColorHex, "#FFFFFF"), 0.20), 100);

    public System.Windows.Media.Brush SearchPreviewInputBackgroundBrush => BuildPreviewBrush(SelectedSearchInputColorHex, SelectedSearchInputOpacityPercent, "#FFFFFF");

    public System.Windows.Media.Brush SearchPreviewInputBorderBrush => BuildPreviewBrush(DarkenColor(ParseColor(SelectedSearchInputColorHex, "#FFFFFF"), 0.18), 100);

    public System.Windows.Media.Brush SearchPreviewTextBrush => BuildPreviewBrush(GetReadableForeground(ParseColor(SelectedSearchInputColorHex, "#FFFFFF")), 100);

    public System.Windows.Media.Brush SearchPreviewMutedBrush => BuildPreviewBrush(DarkenColor(ParseColor(SelectedSearchInputColorHex, "#FFFFFF"), 0.42), 100);

    public System.Windows.Media.Brush SearchPreviewTagBackgroundBrush => BuildPreviewBrush(SelectedTagColorHex, SelectedTagOpacityPercent, "#DBEAFE");

    public System.Windows.Media.Brush SearchPreviewTagBorderBrush => BuildPreviewBrush(DarkenColor(ParseColor(SelectedTagColorHex, "#DBEAFE"), 0.18), 100);

    public System.Windows.Media.Brush SearchPreviewTagTextBrush => BuildPreviewBrush(GetReadableForeground(ParseColor(SelectedTagColorHex, "#DBEAFE")), 100);

    public System.Windows.Media.Brush SidebarPreviewBackgroundBrush => BuildPreviewBrush(SelectedSidebarColorHex, SelectedSidebarOpacityPercent, "#FFFFFF");

    public System.Windows.Media.Brush SidebarPreviewBorderBrush => BuildPreviewBrush(DarkenColor(ParseColor(SelectedSidebarColorHex, "#FFFFFF"), 0.16), 100);

    public System.Windows.Media.Brush SidebarPreviewCategoryBackgroundBrush => BuildPreviewBrush(SelectedCategoryColorHex, SelectedCategoryOpacityPercent, "#D8EAFE");

    public System.Windows.Media.Brush SidebarPreviewCategoryBorderBrush => BuildPreviewBrush(DarkenColor(ParseColor(SelectedCategoryColorHex, "#D8EAFE"), 0.18), 100);

    public System.Windows.Media.Brush SidebarPreviewCategoryTextBrush => BuildPreviewBrush(GetReadableForeground(ParseColor(SelectedCategoryColorHex, "#D8EAFE")), 100);

    public System.Windows.Media.Brush SidebarPreviewPhraseBackgroundBrush => BuildPreviewBrush(SelectedPhraseColorHex, SelectedPhraseOpacityPercent, "#F8FAFC");

    public System.Windows.Media.Brush SidebarPreviewPhraseBorderBrush => BuildPreviewBrush(DarkenColor(ParseColor(SelectedPhraseColorHex, "#F8FAFC"), 0.12), 100);

    public System.Windows.Media.Brush SidebarPreviewPhraseTextBrush => BuildPreviewBrush(GetReadableForeground(ParseColor(SelectedPhraseColorHex, "#F8FAFC")), 100);

    public System.Windows.Media.Brush SidebarPreviewTagBackgroundBrush => BuildPreviewBrush(SelectedTagColorHex, SelectedTagOpacityPercent, "#DBEAFE");

    public System.Windows.Media.Brush SidebarPreviewTagBorderBrush => BuildPreviewBrush(DarkenColor(ParseColor(SelectedTagColorHex, "#DBEAFE"), 0.18), 100);

    public System.Windows.Media.Brush SidebarPreviewTagTextBrush => BuildPreviewBrush(GetReadableForeground(ParseColor(SelectedTagColorHex, "#DBEAFE")), 100);

    public double SidebarPreviewPhraseBottomSpacing => Math.Max(4, SelectedPhraseRowSpacing + 4);

    public Thickness SidebarPreviewPhraseMargin => new(0, 0, 0, SidebarPreviewPhraseBottomSpacing);

    private void ConfigureQuickActionsSection()
    {
        return;
    }

    private void ConfigureAppearanceSection()
    {
        if (AppearanceSection.Child is not Grid grid)
        {
            return;
        }

        if (grid.Children.OfType<FrameworkElement>().Any(element => Equals(element.Tag, "category-tabs-per-row")))
        {
            return;
        }

        while (grid.RowDefinitions.Count < 7)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        foreach (var element in grid.Children.OfType<FrameworkElement>().Where(element => Grid.GetRow(element) >= 2).ToList())
        {
            Grid.SetRow(element, Grid.GetRow(element) + 1);
        }

        var label = new TextBlock
        {
            Text = "每行分类数",
            Tag = "category-tabs-per-row"
        };
        label.SetResourceReference(StyleProperty, "FieldLabelStyle");
        Grid.SetRow(label, 2);

        var comboBox = new System.Windows.Controls.ComboBox
        {
            Margin = new Thickness(0, 6, 0, 0),
            Tag = "category-tabs-per-row",
            ItemsSource = CategoryTabsPerRowOptions
        };
        comboBox.SetResourceReference(StyleProperty, "InputComboBoxStyle");
        comboBox.SetBinding(
            System.Windows.Controls.ComboBox.SelectedItemProperty,
            new System.Windows.Data.Binding(nameof(SelectedCategoryTabsPerRow))
            {
                Source = this,
                Mode = System.Windows.Data.BindingMode.TwoWay
            });
        Grid.SetRow(comboBox, 2);
        Grid.SetColumn(comboBox, 1);

        grid.Children.Add(label);
        grid.Children.Add(comboBox);
        ConfigureAppearancePaletteSection(grid);
    }

    private void ConfigureSidebarAppearancePreviewSection()
    {
        if (AppearanceSection.Child is not Grid grid)
        {
            return;
        }

        if (grid.Children.OfType<FrameworkElement>().Any(element => Equals(element.Tag, "category-font-size")))
        {
            return;
        }

        InsertComboRow(grid, 3, "侧栏宽度", SidebarWidthOptions, nameof(SelectedSidebarWidth), "sidebar-width");
        InsertComboRow(grid, 4, "侧栏高度占比", SidebarHeightPercentOptions, nameof(SelectedSidebarHeightPercent), "sidebar-height-percent");
        InsertComboRow(grid, 5, "分类字体大小", CategoryFontSizeOptions, nameof(SelectedCategoryFontSize), "category-font-size");
        InsertComboRow(grid, 6, "短语字体大小", PhraseFontSizeOptions, nameof(SelectedPhraseFontSize), "phrase-font-size");
        InsertComboRow(grid, 7, "短语行距", PhraseRowSpacingOptions, nameof(SelectedPhraseRowSpacing), "phrase-row-spacing");
    }

    private void InsertComboRow(Grid grid, int rowIndex, string labelText, IEnumerable<int> itemsSource, string bindingPath, string tag)
    {
        InsertGridRow(grid, rowIndex);

        var label = new TextBlock
        {
            Text = labelText,
            Tag = tag
        };
        label.SetResourceReference(StyleProperty, "FieldLabelStyle");
        Grid.SetRow(label, rowIndex);

        var comboBox = new System.Windows.Controls.ComboBox
        {
            Margin = new Thickness(0, 6, 0, 0),
            Tag = tag,
            ItemsSource = itemsSource
        };
        comboBox.SetResourceReference(StyleProperty, "InputComboBoxStyle");
        comboBox.SetBinding(
            System.Windows.Controls.ComboBox.SelectedItemProperty,
            new System.Windows.Data.Binding(bindingPath)
            {
                Source = this,
                Mode = System.Windows.Data.BindingMode.TwoWay
            });
        Grid.SetRow(comboBox, rowIndex);
        Grid.SetColumn(comboBox, 1);

        grid.Children.Add(label);
        grid.Children.Add(comboBox);
    }

    private static void InsertGridRow(Grid grid, int rowIndex)
    {
        grid.RowDefinitions.Insert(rowIndex, new RowDefinition { Height = GridLength.Auto });

        foreach (var element in grid.Children.OfType<FrameworkElement>().ToList())
        {
            if (Grid.GetRow(element) >= rowIndex)
            {
                Grid.SetRow(element, Grid.GetRow(element) + 1);
            }
        }
    }

    private void ConfigureAppearancePaletteSection(Grid grid)
    {
        if (grid.Children.OfType<FrameworkElement>().Any(element => Equals(element.Tag, "appearance-palette-intro")))
        {
            return;
        }

        AddAppearanceThemeSection(grid);

        var introRow = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var introText = new TextBlock
        {
            Tag = "appearance-palette-intro",
            Margin = new Thickness(0, 14, 0, 0),
            Foreground = CreateBrush("#64748B"),
            Text = "先点常用色快速试，再按“自定义颜色”打开颜色面板精调。",
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(introText, introRow);
        Grid.SetColumnSpan(introText, 2);
        grid.Children.Add(introText);

        AddColorPresetRow(grid, "分类标签常用色", () => SelectedCategoryColorHex, hex => SelectedCategoryColorHex = hex);
        AddColorPresetRow(grid, "备注标签常用色", () => SelectedTagColorHex, hex => SelectedTagColorHex = hex);
        AddColorPresetRow(grid, "快捷短语常用色", () => SelectedPhraseColorHex, hex => SelectedPhraseColorHex = hex);
        AddColorPresetRow(grid, "快捷短语栏常用色", () => SelectedSidebarColorHex, hex => SelectedSidebarColorHex = hex);
    }

    private void AddAppearanceThemeSection(Grid grid)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = "配色主题",
            Margin = new Thickness(0, 12, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = CreateBrush("#334155"),
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(label, row);
        grid.Children.Add(label);

        var host = new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 0)
        };

        host.Children.Add(new TextBlock
        {
            Foreground = CreateBrush("#64748B"),
            Text = "先试试这些文艺简约的配色，再把你调好的颜色保存成自己的主题。",
            TextWrapping = TextWrapping.Wrap
        });

        host.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 6),
            Foreground = CreateBrush("#334155"),
            FontWeight = FontWeights.SemiBold,
            Text = "内置主题"
        });

        var builtInPanel = new WrapPanel();
        foreach (var preset in BuiltInThemePresets)
        {
            builtInPanel.Children.Add(CreateThemeApplyButton(preset.Name, preset.Theme, isBuiltIn: true));
        }

        host.Children.Add(builtInPanel);

        host.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 6),
            Foreground = CreateBrush("#334155"),
            FontWeight = FontWeights.SemiBold,
            Text = "我的主题"
        });

        _customThemePanel = new WrapPanel();
        host.Children.Add(_customThemePanel);

        var savePanel = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            Orientation = System.Windows.Controls.Orientation.Horizontal
        };

        var themeNameTextBox = new System.Windows.Controls.TextBox
        {
            Width = 220,
            Height = 36,
            Padding = new Thickness(12, 8, 12, 8)
        };
        themeNameTextBox.SetResourceReference(StyleProperty, "InputTextBoxStyle");
        themeNameTextBox.SetBinding(System.Windows.Controls.TextBox.TextProperty, new System.Windows.Data.Binding(nameof(NewThemeName))
        {
            Source = this,
            Mode = System.Windows.Data.BindingMode.TwoWay,
            UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
        });

        var saveButton = new System.Windows.Controls.Button
        {
            Width = 116,
            Margin = new Thickness(10, 0, 0, 0),
            Content = "保存当前主题"
        };
        saveButton.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        saveButton.Click += SaveCurrentThemeButton_OnClick;

        savePanel.Children.Add(themeNameTextBox);
        savePanel.Children.Add(saveButton);
        host.Children.Add(savePanel);

        host.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = CreateBrush("#64748B"),
            Text = "输入一个名字，比如“清晨纸页”或“晚樱客服台”，就能把当前所有配色保存下来。",
            TextWrapping = TextWrapping.Wrap
        });

        Grid.SetRow(host, row);
        Grid.SetColumn(host, 1);
        grid.Children.Add(host);
    }

    private void ConfigureSearchOverlayAppearanceSection()
    {
        if (StabilitySection.Child is not Grid grid)
        {
            return;
        }

        if (grid.Children.OfType<FrameworkElement>().Any(element => Equals(element.Tag, "search-palette-intro")))
        {
            return;
        }

        var introRow = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var introText = new TextBlock
        {
            Tag = "search-palette-intro",
            Margin = new Thickness(0, 14, 0, 0),
            Foreground = CreateBrush("#64748B"),
            Text = "搜索栏颜色支持常用色和自定义颜色，真实底部搜索栏会实时变化。",
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(introText, introRow);
        Grid.SetColumnSpan(introText, 2);
        grid.Children.Add(introText);

        AddColorPresetRow(grid, "搜索栏常用色", () => SelectedSearchOverlayColorHex, hex => SelectedSearchOverlayColorHex = hex);
        AddColorPresetRow(grid, "输入框常用色", () => SelectedSearchInputColorHex, hex => SelectedSearchInputColorHex = hex);
    }

    private FrameworkElement CreateAdvancedTextField(string bindingPath, string labelText)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Foreground = CreateBrush("#334155"),
            FontWeight = FontWeights.SemiBold,
            Text = labelText
        });

        var textBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 6, 0, 0) };
        textBox.SetResourceReference(StyleProperty, "InputTextBoxStyle");
        textBox.SetBinding(System.Windows.Controls.TextBox.TextProperty, new System.Windows.Data.Binding(bindingPath)
        {
            Source = this,
            Mode = System.Windows.Data.BindingMode.TwoWay,
            UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
        });
        panel.Children.Add(textBox);
        return panel;
    }

    private void AddColorPresetRow(Grid grid, string labelText, Func<string> currentColorAccessor, Action<string> applyColor)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = labelText,
            Margin = new Thickness(0, 12, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = CreateBrush("#334155"),
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(label, row);
        grid.Children.Add(label);

        var panel = new WrapPanel
        {
            Margin = new Thickness(0, 12, 0, 0),
            Orientation = System.Windows.Controls.Orientation.Horizontal
        };

        panel.Children.Add(CreateCurrentColorBadge(currentColorAccessor));

        foreach (var preset in CommonColorPresets)
        {
            panel.Children.Add(CreateColorPresetButton(preset, applyColor));
        }

        panel.Children.Add(CreateCustomColorButton(currentColorAccessor, applyColor));

        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
    }

    private FrameworkElement CreateCurrentColorBadge(Func<string> currentColorAccessor)
    {
        var border = new Border
        {
            Height = 28,
            Margin = new Thickness(0, 0, 10, 8),
            Padding = new Thickness(10, 0, 10, 0),
            Background = CreateBrush("#FFFFFF"),
            BorderBrush = CreateBrush("#BFD0E3"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14)
        };

        var panel = new DockPanel { LastChildFill = true };
        var swatch = new Border
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 8, 0),
            CornerRadius = new CornerRadius(8),
            BorderBrush = CreateBrush("#94A3B8"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
        swatch.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding
        {
            Source = this,
            Mode = System.Windows.Data.BindingMode.OneWay,
            Path = new System.Windows.PropertyPath("."),
            Converter = new CurrentColorBrushConverter(currentColorAccessor)
        });
        DockPanel.SetDock(swatch, Dock.Left);

        panel.Children.Add(swatch);
        panel.Children.Add(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = CreateBrush("#475569"),
            Text = "当前颜色"
        });

        border.Child = panel;
        return border;
    }

    private static System.Windows.Controls.Button CreateColorPresetButton(ColorPreset preset, Action<string> applyColor)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(preset.Hex)!;
        var button = new System.Windows.Controls.Button
        {
            Width = 28,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(color),
            BorderBrush = CreateBrush(DarkenColor(color, 0.18)),
            BorderThickness = new Thickness(1),
            ToolTip = $"{preset.Name} {preset.Hex}",
            Cursor = System.Windows.Input.Cursors.Hand
        };

        button.Click += (_, _) => applyColor(preset.Hex);
        return button;
    }

    private System.Windows.Controls.Button CreateCustomColorButton(Func<string> currentColorAccessor, Action<string> applyColor)
    {
        var button = new System.Windows.Controls.Button
        {
            Height = 28,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(12, 0, 12, 0),
            Background = CreateBrush("#FFFFFF"),
            BorderBrush = CreateBrush("#BFD0E3"),
            BorderThickness = new Thickness(1),
            Foreground = CreateBrush("#334155"),
            Content = "自定义颜色",
            Cursor = System.Windows.Input.Cursors.Hand
        };

        button.Click += (_, _) =>
        {
            var selectedColor = OpenCustomColorDialog(currentColorAccessor());
            if (!string.IsNullOrWhiteSpace(selectedColor))
            {
                applyColor(selectedColor);
            }
        };

        return button;
    }

    private static string? OpenCustomColorDialog(string currentHex)
    {
        using var colorDialog = new Forms.ColorDialog
        {
            AnyColor = true,
            FullOpen = true
        };

        try
        {
            colorDialog.Color = System.Drawing.ColorTranslator.FromHtml(NormalizeColorHex(currentHex, "#FFFFFF"));
        }
        catch
        {
            colorDialog.Color = System.Drawing.Color.White;
        }

        return colorDialog.ShowDialog() == Forms.DialogResult.OK
            ? $"#{colorDialog.Color.R:X2}{colorDialog.Color.G:X2}{colorDialog.Color.B:X2}"
            : null;
    }

    private async void SettingsWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateNavigationVisualState();
        await LoadCustomThemesAsync();
        await RefreshDashboardAsync();
    }

    private async Task LoadCustomThemesAsync()
    {
        IReadOnlyList<AppearanceThemePreset> themes;
        try
        {
            themes = await _viewModel.LoadAppearanceThemesAsync();
        }
        catch
        {
            themes = [];
        }

        _customAppearanceThemes.Clear();
        foreach (var theme in themes
                     .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                     .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _customAppearanceThemes.Add(CloneTheme(theme));
        }

        RefreshCustomThemePanel();
    }

    private void RefreshCustomThemePanel()
    {
        if (_customThemePanel is null)
        {
            return;
        }

        _customThemePanel.Children.Clear();

        if (CustomAppearanceThemes.Count == 0)
        {
            _customThemePanel.Children.Add(new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Background = CreateBrush("#F8FBFF"),
                BorderBrush = CreateBrush("#D9E4F1"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Child = new TextBlock
                {
                    Foreground = CreateBrush("#64748B"),
                    Text = "还没有保存自己的主题。先把颜色调到顺眼，再给它取个名字保存。",
                    TextWrapping = TextWrapping.Wrap
                }
            });

            return;
        }

        foreach (var theme in CustomAppearanceThemes.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _customThemePanel.Children.Add(CreateCustomThemeCard(theme));
        }
    }

    private Border CreateCustomThemeCard(AppearanceThemePreset theme)
    {
        var nameText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = CreateBrush("#334155"),
            Text = theme.Name
        };

        var buttonPanel = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            Orientation = System.Windows.Controls.Orientation.Horizontal
        };

        var applyButton = CreateThemeApplyButton("套用", theme, isBuiltIn: false);
        applyButton.Width = 72;
        applyButton.Height = 30;
        applyButton.Margin = new Thickness(0, 0, 8, 0);

        var deleteButton = new System.Windows.Controls.Button
        {
            Width = 56,
            Height = 30,
            Padding = new Thickness(0),
            Content = "删除",
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = theme.Name
        };
        deleteButton.SetResourceReference(StyleProperty, "SubtleButtonStyle");
        deleteButton.Click += DeleteThemeButton_OnClick;

        buttonPanel.Children.Add(applyButton);
        buttonPanel.Children.Add(deleteButton);

        var card = new Border
        {
            Width = 220,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(12),
            Background = CreateBrush("#FFFFFF"),
            BorderBrush = CreateBrush("#D9E4F1"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16)
        };

        var stack = new StackPanel();
        stack.Children.Add(nameText);
        stack.Children.Add(CreateThemeSwatchStrip(theme, new Thickness(0, 8, 0, 0)));
        stack.Children.Add(buttonPanel);
        card.Child = stack;
        return card;
    }

    private System.Windows.Controls.Button CreateThemeApplyButton(string label, AppearanceThemePreset theme, bool isBuiltIn)
    {
        var button = new System.Windows.Controls.Button
        {
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(12, 10, 12, 10),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = theme
        };
        button.SetResourceReference(StyleProperty, isBuiltIn ? "PrimaryButtonStyle" : "SubtleButtonStyle");
        button.Click += ThemeApplyButton_OnClick;

        if (isBuiltIn)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                Text = label
            });
            content.Children.Add(CreateThemeSwatchStrip(theme, new Thickness(0, 8, 0, 0)));
            button.Content = content;
        }
        else
        {
            button.Content = label;
        }

        return button;
    }

    private static StackPanel CreateThemeSwatchStrip(AppearanceThemePreset theme, Thickness margin)
    {
        var panel = new StackPanel
        {
            Margin = margin,
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        foreach (var hex in new[]
                 {
                     theme.CategoryColorHex,
                     theme.TagColorHex,
                     theme.PhraseColorHex,
                     theme.SidebarColorHex,
                     theme.SearchOverlayColorHex,
                     theme.SearchInputColorHex
                 })
        {
            panel.Children.Add(new Border
            {
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 6, 0),
                CornerRadius = new CornerRadius(7),
                Background = CreateBrush(NormalizeColorHex(hex, "#FFFFFF")),
                BorderBrush = CreateBrush("#C7D2E0"),
                BorderThickness = new Thickness(1)
            });
        }

        return panel;
    }

    private void ThemeApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: AppearanceThemePreset theme })
        {
            return;
        }

        ApplyAppearanceTheme(theme);
    }

    private async void SaveCurrentThemeButton_OnClick(object sender, RoutedEventArgs e)
    {
        var themeName = (NewThemeName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(themeName))
        {
            System.Windows.MessageBox.Show(
                this,
                "先给主题取个名字，再保存当前配色。",
                "主题名称不能为空",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await _viewModel.SaveAppearanceThemeAsync(BuildCurrentAppearanceTheme(themeName));
        NewThemeName = string.Empty;
        await LoadCustomThemesAsync();
    }

    private async void DeleteThemeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string themeName } || string.IsNullOrWhiteSpace(themeName))
        {
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            this,
            $"确定删除主题“{themeName}”吗？",
            "删除主题",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.DeleteAppearanceThemeAsync(themeName);
        await LoadCustomThemesAsync();
    }

    private void ApplyAppearanceTheme(AppearanceThemePreset theme)
    {
        SelectedCategoryColorHex = NormalizeColorHex(theme.CategoryColorHex, "#D8EAFE");
        SelectedCategoryOpacityPercent = theme.CategoryOpacityPercent;
        SelectedTagColorHex = NormalizeColorHex(theme.TagColorHex, "#DBEAFE");
        SelectedTagOpacityPercent = theme.TagOpacityPercent;
        SelectedPhraseColorHex = NormalizeColorHex(theme.PhraseColorHex, "#F8FAFC");
        SelectedPhraseOpacityPercent = theme.PhraseOpacityPercent;
        SelectedSidebarColorHex = NormalizeColorHex(theme.SidebarColorHex, "#FFFFFF");
        SelectedSidebarOpacityPercent = theme.SidebarOpacityPercent;
        SelectedSearchOverlayColorHex = NormalizeColorHex(theme.SearchOverlayColorHex, "#FFFFFF");
        SelectedSearchOverlayOpacityPercent = theme.SearchOverlayOpacityPercent;
        SelectedSearchInputColorHex = NormalizeColorHex(theme.SearchInputColorHex, "#FFFFFF");
        SelectedSearchInputOpacityPercent = theme.SearchInputOpacityPercent;
    }

    private AppearanceThemePreset BuildCurrentAppearanceTheme(string themeName)
    {
        return new AppearanceThemePreset
        {
            Name = themeName.Trim(),
            CategoryColorHex = SelectedCategoryColorHex,
            CategoryOpacityPercent = SelectedCategoryOpacityPercent,
            TagColorHex = SelectedTagColorHex,
            TagOpacityPercent = SelectedTagOpacityPercent,
            PhraseColorHex = SelectedPhraseColorHex,
            PhraseOpacityPercent = SelectedPhraseOpacityPercent,
            SidebarColorHex = SelectedSidebarColorHex,
            SidebarOpacityPercent = SelectedSidebarOpacityPercent,
            SearchOverlayColorHex = SelectedSearchOverlayColorHex,
            SearchOverlayOpacityPercent = SelectedSearchOverlayOpacityPercent,
            SearchInputColorHex = SelectedSearchInputColorHex,
            SearchInputOpacityPercent = SelectedSearchInputOpacityPercent
        };
    }

    private static AppearanceThemePreset CloneTheme(AppearanceThemePreset theme)
    {
        return new AppearanceThemePreset
        {
            Name = theme.Name,
            CategoryColorHex = theme.CategoryColorHex,
            CategoryOpacityPercent = theme.CategoryOpacityPercent,
            TagColorHex = theme.TagColorHex,
            TagOpacityPercent = theme.TagOpacityPercent,
            PhraseColorHex = theme.PhraseColorHex,
            PhraseOpacityPercent = theme.PhraseOpacityPercent,
            SidebarColorHex = theme.SidebarColorHex,
            SidebarOpacityPercent = theme.SidebarOpacityPercent,
            SearchOverlayColorHex = theme.SearchOverlayColorHex,
            SearchOverlayOpacityPercent = theme.SearchOverlayOpacityPercent,
            SearchInputColorHex = theme.SearchInputColorHex,
            SearchInputOpacityPercent = theme.SearchInputOpacityPercent
        };
    }

    private void NavigateButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string sectionKey })
        {
            return;
        }

        ScrollToSection(sectionKey);
    }

    private void SettingsScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isNavigatingToSection)
        {
            return;
        }

        UpdateSelectedSectionFromScroll();
    }

    private async void RefreshStatsButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshDashboardAsync();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        WasConfirmed = false;
        Close();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject dependencyObject && IsInteractiveElement(dependencyObject))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // Ignore transient drag failures when the mouse is released during DragMove.
        }
    }

    private static bool IsInteractiveElement(DependencyObject dependencyObject)
    {
        for (DependencyObject? current = dependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase or
                System.Windows.Controls.TextBox or
                System.Windows.Controls.ComboBox or
                System.Windows.Controls.Primitives.ScrollBar or
                System.Windows.Controls.Primitives.Thumb or
                System.Windows.Controls.Primitives.Selector or
                System.Windows.Controls.TabItem)
            {
                return true;
            }
        }

        return false;
    }

    private async Task RefreshDashboardAsync()
    {
        if (IsRefreshingStats)
        {
            return;
        }

        IsRefreshingStats = true;
        ResetStatsLoadingState();

        try
        {
            RefreshCloudStatusSummary();
            await RefreshLogStatsCoreAsync();
            await RefreshUsageStatsCoreAsync();
        }
        finally
        {
            IsRefreshingStats = false;
        }
    }

    private void ResetStatsLoadingState()
    {
        LogSessionSummary = "正在读取...";
        LogTotalSummary = "正在读取...";
        LogStorageSummary = "正在读取...";
        LogPathSummary = "正在读取...";
        ExceptionSummary = "正在读取...";
        ExceptionEmptyMessage = "正在读取...";
        UsageLaunchSummary = "正在读取...";
        UsageRuntimeSummary = "正在读取...";
        UsageMostUsedSummary = "正在读取...";
        UsageLeastUsedSummary = "正在读取...";
        UsageMostUsedEmptyMessage = "正在读取...";
        UsageLeastUsedEmptyMessage = "正在读取...";
        CloudKnowledgeSyncSummary = "正在读取...";
        CloudConversationSyncSummary = "正在读取...";
        CloudUpdateCheckSummary = "正在读取...";
        CloudKnowledgeVersionSummary = "正在读取...";
        CloudActionSummary = "正在读取...";
        MostUsedPhraseItems.Clear();
        LeastUsedPhraseItems.Clear();
        RecentExceptionItems.Clear();
        OnPropertyChanged(nameof(HasMostUsedPhraseItems));
        OnPropertyChanged(nameof(HasLeastUsedPhraseItems));
        OnPropertyChanged(nameof(HasRecentExceptionItems));
    }

    private void RefreshCloudStatusSummary()
    {
        var settings = _viewModel.Settings;
        var serverBaseUrl = settings.ServerBaseUrl;
        var syncSwitchText = settings.EnableCloudSync ? "已开启" : "未开启";
        var updateSwitchText = settings.EnableUpdateChecks ? "已开启" : "未开启";

        CloudKnowledgeSyncSummary = $"知识库：{FormatTimestamp(settings.LastKnowledgeSyncAt, "从未同步")}（云同步{syncSwitchText}）";
        CloudConversationSyncSummary = $"问题日志：{FormatTimestamp(settings.LastConversationSyncAt, "从未上传")}（云同步{syncSwitchText}）";
        CloudUpdateCheckSummary = $"更新检查：{FormatTimestamp(settings.LastUpdateCheckAt, "从未检查")}（更新检查{updateSwitchText}）";
        CloudKnowledgeVersionSummary = string.IsNullOrWhiteSpace(settings.LastKnownCloudKnowledgeVersion)
            ? "最近没有记录到云端知识版本"
            : $"最近已确认知识版本：{settings.LastKnownCloudKnowledgeVersion}";
        CloudActionSummary = $"当前服务地址：{SettingsInputValidator.ResolveOperationalServerBaseUrl(serverBaseUrl)}";
    }

    private async Task RefreshLogStatsCoreAsync()
    {
        try
        {
            var stats = await _viewModel.GetConversationLogStatsAsync();
            _logDatabasePath = stats.DatabasePath;
            _logCaptureFolderPath = stats.CaptureFolderPath;
            LogSessionSummary = $"本次启动已记录 {stats.SessionLogCount} 条";
            LogTotalSummary = $"累计已有 {stats.TotalLogCount} 条日志";
            LogStorageSummary = $"截图 {stats.CaptureFileCount} 个，约 {FormatBytes(stats.CaptureFolderSizeBytes)}";
            LogPathSummary = $"数据库：{stats.DatabasePath}{Environment.NewLine}截图目录：{stats.CaptureFolderPath}";
            _exceptionLogFolderPath = _viewModel.ExceptionLogRootPath;

            var exceptionItems = await _viewModel.GetRecentExceptionEntriesAsync();
            ExceptionSummary = exceptionItems.Count == 0 ? "最近没有异常记录" : $"最近异常 {exceptionItems.Count} 条";
            ExceptionEmptyMessage = "最近没有异常记录";
            ReplaceExceptionItems(exceptionItems);
        }
        catch (Exception ex)
        {
            _logDatabasePath = string.Empty;
            _logCaptureFolderPath = string.Empty;
            _exceptionLogFolderPath = string.Empty;
            LogSessionSummary = "读取失败";
            LogTotalSummary = "读取失败";
            LogStorageSummary = ex.Message;
            LogPathSummary = "无法读取日志状态。";
            ExceptionSummary = "读取失败";
            ExceptionEmptyMessage = "无法读取异常日志。";
            RecentExceptionItems.Clear();
            OnPropertyChanged(nameof(HasRecentExceptionItems));
        }
    }

    private async Task RefreshUsageStatsCoreAsync()
    {
        try
        {
            var stats = await _viewModel.GetUsageDashboardStatsAsync();
            UsageLaunchSummary = $"累计启动 {stats.LaunchCount} 次";
            UsageRuntimeSummary = $"累计运行 {FormatDuration(stats.TotalRuntimeSeconds)}";
            UsageMostUsedSummary = $"高频短语 {stats.MostUsedPhrases.Count} 条";
            UsageLeastUsedSummary = $"低频短语 {stats.LeastUsedPhrases.Count} 条";
            UsageMostUsedEmptyMessage = "还没有发送记录";
            UsageLeastUsedEmptyMessage = "当前还没有可统计的短语";
            ReplaceUsageItems(MostUsedPhraseItems, BuildUsageDisplayItems(stats.MostUsedPhrases));
            ReplaceUsageItems(LeastUsedPhraseItems, BuildUsageDisplayItems(stats.LeastUsedPhrases));
            OnPropertyChanged(nameof(HasMostUsedPhraseItems));
            OnPropertyChanged(nameof(HasLeastUsedPhraseItems));
        }
        catch (Exception ex)
        {
            UsageLaunchSummary = "读取失败";
            UsageRuntimeSummary = "读取失败";
            UsageMostUsedSummary = ex.Message;
            UsageLeastUsedSummary = "无法读取短语统计。";
            UsageMostUsedEmptyMessage = ex.Message;
            UsageLeastUsedEmptyMessage = "无法读取短语统计。";
            MostUsedPhraseItems.Clear();
            LeastUsedPhraseItems.Clear();
            OnPropertyChanged(nameof(HasMostUsedPhraseItems));
            OnPropertyChanged(nameof(HasLeastUsedPhraseItems));
        }
    }

    private async void RunCloudSyncNowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (IsRunningCloudAction)
        {
            return;
        }

        IsRunningCloudAction = true;
        CloudActionSummary = "正在执行云同步...";

        try
        {
            var app = App.Instance;
            if (app is null)
            {
                throw new InvalidOperationException("应用程序实例不可用。");
            }

            var settingsSnapshot = CreatePendingSettingsSnapshot();
            var result = await app.RunCloudSyncNowAsync(settingsSnapshot, force: true);
            ApplyCloudStatusSnapshot(settingsSnapshot);
            RefreshCloudStatusSummary();

            CloudActionSummary = result.Message;
            if (result.Success)
            {
                var detail = $"知识分类 {result.CategoryCount} 条，短语 {result.PhraseCount} 条，快捷标签 {result.QuickTagCount} 条，素材 {result.FileItemCount} 条。{Environment.NewLine}问题日志 {result.ConversationLogCount} 条，推荐事件 {result.RecommendationEventCount} 条，识别纠正 {result.RecognitionCorrectionCount} 条。";
                System.Windows.MessageBox.Show(this, detail, "云同步", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show(this, result.Message, "云同步", MessageBoxButton.OK, result.Skipped ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            CloudActionSummary = $"云同步失败：{ex.Message}";
            System.Windows.MessageBox.Show(this, ex.Message, "云同步", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsRunningCloudAction = false;
        }
    }

    private async void CheckForUpdatesNowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (IsRunningCloudAction)
        {
            return;
        }

        IsRunningCloudAction = true;
        CloudActionSummary = "正在检查更新...";

        try
        {
            var app = App.Instance;
            if (app is null)
            {
                throw new InvalidOperationException("应用程序实例不可用。");
            }

            var settingsSnapshot = CreatePendingSettingsSnapshot();
            var result = await app.CheckForUpdatesNowAsync(settingsSnapshot, force: true, showUpdatePrompt: true);
            ApplyCloudStatusSnapshot(settingsSnapshot);
            RefreshCloudStatusSummary();

            CloudActionSummary = result.Message;
            if (!result.Success)
            {
                System.Windows.MessageBox.Show(this, result.Message, "更新检查", MessageBoxButton.OK, result.Skipped ? MessageBoxImage.Information : MessageBoxImage.Warning);
                return;
            }

            if (!result.HasAppUpdate && !result.HasKnowledgeUpdate)
            {
                System.Windows.MessageBox.Show(this, "当前没有检测到新版本或新的知识分析结果。", "更新检查", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            CloudActionSummary = $"更新检查失败：{ex.Message}";
            System.Windows.MessageBox.Show(this, ex.Message, "更新检查", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsRunningCloudAction = false;
        }
    }

    private AppSettings CreatePendingSettingsSnapshot()
    {
        var settingsSnapshot = _viewModel.CreateSettingsSnapshot();
        ApplySelectionsTo(settingsSnapshot);
        return settingsSnapshot;
    }

    private void ApplyCloudStatusSnapshot(AppSettings settingsSnapshot)
    {
        _viewModel.Settings.LastKnowledgeSyncAt = settingsSnapshot.LastKnowledgeSyncAt;
        _viewModel.Settings.LastConversationSyncAt = settingsSnapshot.LastConversationSyncAt;
        _viewModel.Settings.LastUpdateCheckAt = settingsSnapshot.LastUpdateCheckAt;
        _viewModel.Settings.LastKnownCloudKnowledgeVersion = settingsSnapshot.LastKnownCloudKnowledgeVersion;
    }

    private void OpenKnowledgeReviewWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new KnowledgeReviewWindow(SelectedServerBaseUrl, SelectedSyncApiToken)
        {
            Owner = this
        };

        window.Show();
        window.Activate();
    }

    private void ManageFilesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new FileLibraryWindow(_viewModel)
        {
            Owner = this
        };

        window.ShowDialog();
    }

    private void SelectFileLibraryFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TrySelectFolder(
                "选择一个资料文件夹，保存后以及每次启动时都会自动同步里面的文件。",
                HasSelectedFileLibraryFolder ? SelectedFileLibraryFolderPath : string.Empty,
                out var selectedPath))
        {
            return;
        }

        SelectedFileLibraryFolderPath = selectedPath;
    }

    private void ClearFileLibraryFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedFileLibraryFolderPath = string.Empty;
    }

    private void OpenFileLibraryFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var targetPath = GetEffectiveFileLibraryFolderPath();

        try
        {
            Directory.CreateDirectory(targetPath);
            if (!HasSelectedFileLibraryFolder)
            {
                SelectedFileLibraryFolderPath = targetPath;
            }
        }
        catch
        {
        }

        OpenDirectory(targetPath);
    }

    private void SelectPhraseImageFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TrySelectFolder(
                "选择一个短语图片文件夹。程序会把拖入或粘贴到快捷短语里的图片副本保存到这里，后续发送时直接从这里读取。",
                PhraseImageFolderDisplayPath,
                out var selectedPath))
        {
            return;
        }

        SelectedPhraseImageFolderPath = selectedPath;
    }

    private void UseDefaultPhraseImageFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedPhraseImageFolderPath = string.Empty;
    }

    private void OpenPhraseImageFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(PhraseImageFolderDisplayPath);
        }
        catch
        {
        }

        OpenDirectory(PhraseImageFolderDisplayPath);
    }

    private void SelectScreenshotToolSaveDirectoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TrySelectFolder(
                "选择截图工具默认保存目录。手动保存截图时会优先落到这里。",
                ScreenshotToolSaveDirectoryDisplayPath,
                out var selectedPath))
        {
            return;
        }

        SelectedScreenshotToolSaveDirectory = selectedPath;
    }

    private void UseDefaultScreenshotToolSaveDirectoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedScreenshotToolSaveDirectory = string.Empty;
    }

    private void ChangeScreenshotToolHotkeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ScreenshotHotkeyDialog(SelectedScreenshotToolHotkey)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedHotkey))
        {
            return;
        }

        SelectedScreenshotToolHotkey = dialog.SelectedHotkey;
        RefreshValidationState();
    }

    private void ResetScreenshotToolHotkeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedScreenshotToolHotkey = ScreenshotHotkeyBinding.DefaultHotkey;
        RefreshValidationState();
    }

    private void OpenScreenshotToolSaveDirectoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ScreenshotToolSaveDirectoryDisplayPath);
        }
        catch
        {
        }

        OpenDirectory(ScreenshotToolSaveDirectoryDisplayPath);
    }

    private void SelectIssueReportDirectoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TrySelectFolder(
                "选择问题反馈文件保存目录。托盘菜单里的问题记录会把反馈文件生成到这里。",
                IssueReportDirectoryDisplayPath,
                out var selectedPath))
        {
            return;
        }

        SelectedIssueReportDirectory = selectedPath;
    }

    private bool TrySelectFolder(string description, string initialPath, out string selectedPath)
    {
        var result = _folderSelectionDialogService.SelectFolder(description, initialPath);
        if (result.HasError)
        {
            System.Windows.MessageBox.Show(
                this,
                $"{result.ErrorMessage}{Environment.NewLine}{Environment.NewLine}你也可以直接在输入框里粘贴一个本地目录路径后再保存。",
                "无法打开文件夹选择器",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        selectedPath = result.SelectedPath;
        return result.Confirmed;
    }

    private void UseDefaultIssueReportDirectoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedIssueReportDirectory = string.Empty;
    }

    private void OpenIssueReportDirectoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(IssueReportDirectoryDisplayPath);
        }
        catch
        {
        }

        OpenDirectory(IssueReportDirectoryDisplayPath);
    }

    private async void ExportPhrasesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出快捷短语",
            Filter = "Excel 97-2003 工作簿|*.xls|CSV 文件|*.csv",
            DefaultExt = ".xls",
            FileName = $"快捷短语-{DateTime.Now:yyyyMMdd-HHmmss}.xls"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var result = await _viewModel.ExportPhrasesToXlsAsync(dialog.FileName);
        System.Windows.MessageBox.Show(
            this,
            result.Message,
            result.Success ? "导出完成" : "导出失败",
            MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void ImportPhrasesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入快捷短语",
            Filter = "快捷短语文件|*.xls;*.csv|Excel 97-2003 工作簿|*.xls|CSV 文件|*.csv",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var progressWindow = new ImportProgressWindow
        {
            Owner = this
        };
        progressWindow.UpdateProgress(new PhraseImportProgress(0, "准备导入..."));
        progressWindow.Show();

        var progress = new Progress<PhraseImportProgress>(progressWindow.UpdateProgress);
        var result = await _viewModel.ImportPhrasesFromXlsAsync(dialog.FileName, progress);
        progressWindow.Close();

        System.Windows.MessageBox.Show(
            this,
            result.Message,
            result.Success ? "导入完成" : "导入失败",
            MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

        if (result.Success)
        {
            await RefreshDashboardAsync();
        }
    }

    private async void CleanupPhrasesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var cleanupWindow = new PhraseCleanupWindow(_viewModel)
        {
            Owner = this
        };

        cleanupWindow.ShowDialog();
        await RefreshDashboardAsync();
    }

    private async void ExportUsageReportButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出使用统计报表",
            Filter = "Excel 97-2003 工作簿|*.xls",
            DefaultExt = ".xls",
            FileName = $"使用统计报表-{DateTime.Now:yyyyMMdd-HHmmss}.xls"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var result = await _viewModel.ExportUsageDashboardReportAsync(dialog.FileName);
        System.Windows.MessageBox.Show(
            this,
            result.Message,
            result.Success ? "导出完成" : "导出失败",
            MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void ExportPersonalConfigButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出个人配置",
            Filter = "QuickReply 配置文件|*.qrconfig.json|JSON 文件|*.json",
            DefaultExt = ".json",
            FileName = $"个人配置-{DateTime.Now:yyyyMMdd-HHmmss}.qrconfig.json"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var result = await _viewModel.ExportPersonalConfigAsync(dialog.FileName);
        System.Windows.MessageBox.Show(
            this,
            result.Message,
            result.Success ? "导出完成" : "导出失败",
            MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void ImportPersonalConfigButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入个人配置",
            Filter = "QuickReply 配置文件|*.qrconfig.json;*.json",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var result = await _viewModel.ImportPersonalConfigAsync(dialog.FileName);
        if (result.Success)
        {
            SyncSelectionsFromViewModel();
            await LoadCustomThemesAsync();
            await RefreshDashboardAsync();
        }

        System.Windows.MessageBox.Show(
            this,
            result.Message,
            result.Success ? "导入完成" : "导入失败",
            MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OpenUserDataFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenDirectory(_viewModel.UserDataRootPath);
    }

    private async void ResetPhraseDataButton_OnClick(object sender, RoutedEventArgs e)
    {
        var confirmResult = System.Windows.MessageBox.Show(
            this,
            "这会清空当前所有分类、快捷短语和短语使用统计，并恢复内置的示例分类和快捷短语。确定要继续吗？",
            "确认恢复默认示例短语",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.ResetPhraseDataToDefaultsAsync();
        await RefreshDashboardAsync();
        System.Windows.MessageBox.Show(this, "已恢复默认示例短语数据。", "数据管理", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void ClearFileLibraryConfigurationButton_OnClick(object sender, RoutedEventArgs e)
    {
        var confirmResult = System.Windows.MessageBox.Show(
            this,
            "这会清空资料文件夹路径、手动加入的文件和文件使用统计，但不会删除你电脑里的原始文件。确定要继续吗？",
            "确认清空文件库配置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.ClearFileLibraryConfigurationAsync();
        SelectedFileLibraryFolderPath = string.Empty;
        await RefreshDashboardAsync();
        System.Windows.MessageBox.Show(this, "已清空文件库配置。", "数据管理", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void ResetPersonalizationButton_OnClick(object sender, RoutedEventArgs e)
    {
        var confirmResult = System.Windows.MessageBox.Show(
            this,
            "这会恢复热键、颜色、透明度、延迟、搜索模式等个性化配置为默认值。文件库路径会保留。确定要继续吗？",
            "确认恢复个性化配置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.ResetPersonalizationSettingsAsync();
        SyncSelectionsFromViewModel();

        await RefreshDashboardAsync();
        System.Windows.MessageBox.Show(this, "已恢复默认个性化配置。", "数据管理", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void ApplySelectionsTo(AppSettings settings)
    {
        SettingsWindowSelectionMapper.ApplySelectionsToSettings(this, settings);
    }

    private void RefreshValidationState()
    {
        var errors = BuildValidationErrors();
        ValidationSummary = errors.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, errors.Select(error => $"• {error}"));

        OnPropertyChanged(nameof(HasValidationErrors));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanRunCloudSyncActions));
        OnPropertyChanged(nameof(CanRunUpdateActions));
        OnPropertyChanged(nameof(CloudActionAvailabilitySummary));
        OnPropertyChanged(nameof(CloudActionAvailabilityBrush));
    }

    private IReadOnlyList<string> BuildValidationErrors()
    {
        var draft = new AppSettings();
        ApplySelectionsTo(draft);
        return SettingsInputValidator.ValidateSettings(draft);
    }

    private void SyncSelectionsFromViewModel()
    {
        SettingsWindowSelectionMapper.ApplySettingsToSelections(_viewModel.Settings, this);
        RefreshValidationState();
        OnPropertyChanged(string.Empty);
    }

    private async void RefreshLogStatsButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshLogStatsCoreAsync();
    }

    private void OpenLogFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenDirectory(_logDatabasePath);
    }

    private void OpenCaptureFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenDirectory(_logCaptureFolderPath);
    }

    private void OpenExceptionFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenDirectory(_exceptionLogFolderPath);
    }

    private async void RunLogCleanupButton_OnClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.RunConversationLogMaintenanceAsync();
        await RefreshLogStatsCoreAsync();

        System.Windows.MessageBox.Show(
            this,
            "已按当前策略执行一次日志截图清理。",
            "日志管理",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void ClearExceptionLogsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var confirmResult = System.Windows.MessageBox.Show(
            this,
            "这会清空最近异常记录文件。确定要继续吗？",
            "确认清空异常记录",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        var removedCount = await _viewModel.ClearExceptionLogsAsync();
        await RefreshLogStatsCoreAsync();

        System.Windows.MessageBox.Show(
            this,
            removedCount == 0 ? "当前没有可清空的异常记录。" : $"已清空 {removedCount} 个异常日志文件。",
            "日志管理",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void ClearAllCapturesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var confirmResult = System.Windows.MessageBox.Show(
            this,
            "这会删除当前保存的所有日志截图，但不会删除文本日志。确定要继续吗？",
            "确认清空截图",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        var removedCount = await _viewModel.ClearConversationLogCapturesAsync();
        await RefreshLogStatsCoreAsync();

        System.Windows.MessageBox.Show(
            this,
            removedCount == 0 ? "当前没有可清空的日志截图。" : $"已清空 {removedCount} 个日志截图。",
            "日志管理",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        RefreshValidationState();
        if (HasValidationErrors)
        {
            System.Windows.MessageBox.Show(
                this,
                "请先修正当前设置里的高亮问题，再保存。",
                "设置校验",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        WasConfirmed = true;
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        WasConfirmed = false;
        Close();
    }

    private void ReplaceUsageItems(ObservableCollection<PhraseUsageDisplayItem> target, IReadOnlyList<PhraseUsageDisplayItem> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private void ReplaceExceptionItems(IReadOnlyList<ExceptionLogEntry> items)
    {
        RecentExceptionItems.Clear();

        foreach (var item in items)
        {
            RecentExceptionItems.Add(new ExceptionDisplayItem
            {
                OccurredAtText = item.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                SourceText = string.IsNullOrWhiteSpace(item.Source) ? "未知来源" : item.Source,
                ExceptionType = string.IsNullOrWhiteSpace(item.ExceptionType) ? "异常" : item.ExceptionType,
                Message = string.IsNullOrWhiteSpace(item.Message) ? "没有异常消息" : item.Message,
                ContextText = string.IsNullOrWhiteSpace(item.Context) ? string.Empty : item.Context,
                IsTerminating = item.IsTerminating
            });
        }

        OnPropertyChanged(nameof(HasRecentExceptionItems));
    }

    private static IReadOnlyList<PhraseUsageDisplayItem> BuildUsageDisplayItems(IReadOnlyList<PhraseUsageDashboardItem> items)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var maxCount = Math.Max(1, items.Max(item => item.SendCount));
        return items
            .Select((item, index) => new PhraseUsageDisplayItem
            {
                Rank = index + 1,
                DisplayName = FormatPhraseName(item),
                ContentPreview = string.IsNullOrWhiteSpace(item.ContentPreview) ? "无内容预览" : item.ContentPreview.Trim(),
                CountText = item.SendCount <= 0 ? "未发送" : $"已发送 {item.SendCount} 次",
                LastUsedText = FormatLastSent(item.LastSentAt),
                HeatPercent = Math.Max(12, item.SendCount <= 0 ? 12 : (item.SendCount / (double)maxCount) * 100)
            })
            .ToArray();
    }

    private static string FormatPhraseUsageList(IReadOnlyList<PhraseUsageDashboardItem> items, string emptyText)
    {
        if (items.Count == 0)
        {
            return emptyText;
        }

        return string.Join(
            Environment.NewLine,
            items.Select((item, index) =>
                $"{index + 1}. {FormatPhraseName(item)} | 已发送 {item.SendCount} 次| {FormatLastSent(item.LastSentAt)}"));
    }

    private static string FormatPhraseName(PhraseUsageDashboardItem item)
    {
        var title = item.Title?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        var content = item.ContentPreview?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return "未命名短语";
        }

        return Shorten(content, 26);
    }

    private static string FormatLastSent(DateTimeOffset? lastSentAt)
    {
        if (lastSentAt is null)
        {
            return "从未发送";
        }

        var span = DateTimeOffset.Now - lastSentAt.Value;
        if (span.TotalDays >= 1)
        {
            return $"{Math.Floor(span.TotalDays)} 天前发送";
        }

        if (span.TotalHours >= 1)
        {
            return $"{Math.Floor(span.TotalHours)} 小时前发送";
        }

        if (span.TotalMinutes >= 1)
        {
            return $"{Math.Floor(span.TotalMinutes)} 分钟前发送";
        }

        return "刚刚发送";
    }

    private static string FormatDuration(long totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "0 分钟";
        }

        var span = TimeSpan.FromSeconds(totalSeconds);
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays} 天 {span.Hours} 小时";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours} 小时 {span.Minutes} 分钟";
        }

        if (span.TotalMinutes >= 1)
        {
            return $"{(int)span.TotalMinutes} 分钟";
        }

        return $"{Math.Max(1, (int)span.TotalSeconds)} 秒";
    }

    private static string FormatTimestamp(DateTimeOffset? value, string fallback)
    {
        return value.HasValue
            ? value.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            : fallback;
    }

    private static string Shorten(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return $"{value[..Math.Max(0, maxLength - 1)]}…";
    }

    private static string FormatBytes(long byteCount)
    {
        const double kilobyte = 1024d;
        const double megabyte = kilobyte * 1024d;
        const double gigabyte = megabyte * 1024d;

        if (byteCount >= gigabyte)
        {
            return $"{byteCount / gigabyte:0.0} GB";
        }

        if (byteCount >= megabyte)
        {
            return $"{byteCount / megabyte:0.0} MB";
        }

        if (byteCount >= kilobyte)
        {
            return $"{byteCount / kilobyte:0.0} KB";
        }

        return $"{byteCount} B";
    }

    private static void OpenDirectory(string path)
    {
        var targetPath = path;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        if (File.Exists(targetPath))
        {
            targetPath = Path.GetDirectoryName(targetPath) ?? targetPath;
        }

        if (!Directory.Exists(targetPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = targetPath,
            UseShellExecute = true
        });
    }

    private string GetEffectiveFileLibraryFolderPath()
    {
        return HasSelectedFileLibraryFolder
            ? SelectedFileLibraryFolderPath
            : Path.Combine(_viewModel.UserDataRootPath, "file-library");
    }

    private void ScrollToSection(string sectionKey)
    {
        var target = GetSectionElement(sectionKey);
        if (target is null)
        {
            return;
        }

        _isNavigatingToSection = true;
        SelectedSectionKey = sectionKey;
        UpdateNavigationVisualState();

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var position = target.TranslatePoint(new System.Windows.Point(0, 0), ContentStack);
                SettingsScrollViewer.ScrollToVerticalOffset(Math.Max(0, position.Y));
            }
            finally
            {
                _isNavigatingToSection = false;
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateSelectedSectionFromScroll()
    {
        var sections = new (string Key, FrameworkElement Element)[]
        {
            (GeneralSectionKey, GeneralSection),
            (SearchSectionKey, StabilitySection),
            (QuickActionsSectionKey, QuickActionsSection),
            (DelaySectionKey, DelaySection),
            (AppearanceSectionKey, AppearanceSection),
            (FilesSectionKey, FilesSection)
        };

        const double activationOffset = 24d;
        var currentKey = sections[0].Key;

        foreach (var section in sections)
        {
            var position = section.Element.TranslatePoint(new System.Windows.Point(0, 0), ContentStack);
            if (position.Y - activationOffset <= SettingsScrollViewer.VerticalOffset)
            {
                currentKey = section.Key;
                continue;
            }

            break;
        }

        if (currentKey == SelectedSectionKey)
        {
            return;
        }

        SelectedSectionKey = currentKey;
        UpdateNavigationVisualState();
    }

    private FrameworkElement? GetSectionElement(string sectionKey)
    {
        return sectionKey switch
        {
            GeneralSectionKey => GeneralSection,
            SearchSectionKey => StabilitySection,
            QuickActionsSectionKey => QuickActionsSection,
            DelaySectionKey => DelaySection,
            AppearanceSectionKey => AppearanceSection,
            FilesSectionKey => FilesSection,
            _ => null
        };
    }

    private void UpdateNavigationVisualState()
    {
        ApplyNavigationButtonStyle(GeneralNavButton, GeneralSectionKey);
        ApplyNavigationButtonStyle(StabilityNavButton, SearchSectionKey);
        ApplyNavigationButtonStyle(QuickActionsNavButton, QuickActionsSectionKey);
        ApplyNavigationButtonStyle(DelayNavButton, DelaySectionKey);
        ApplyNavigationButtonStyle(AppearanceNavButton, AppearanceSectionKey);
        ApplyNavigationButtonStyle(FilesNavButton, FilesSectionKey);
    }

    private void DelayTextBox_OnPreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void DelayTextBox_OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(System.Windows.DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var pastedText = e.DataObject.GetData(System.Windows.DataFormats.Text) as string ?? string.Empty;
        if (!pastedText.All(char.IsDigit))
        {
            e.CancelCommand();
        }
    }

    private void DelayTextBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox)
        {
            return;
        }

        if (!int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            value = 50;
        }

        textBox.Text = NormalizeDelaySelection(value, 50).ToString(CultureInfo.InvariantCulture);
    }

    private void ApplyNavigationButtonStyle(System.Windows.Controls.Button button, string sectionKey)
    {
        var isSelected = string.Equals(SelectedSectionKey, sectionKey, StringComparison.Ordinal);
        button.Background = isSelected
            ? CreateBrush("#DBEAFE")
            : System.Windows.Media.Brushes.Transparent;
        button.BorderBrush = isSelected
            ? CreateBrush("#93C5FD")
            : System.Windows.Media.Brushes.Transparent;
        button.Foreground = isSelected
            ? CreateBrush("#1D4ED8")
            : CreateBrush("#334155");
    }

    private static SolidColorBrush CreateBrush(string colorHex)
    {
        return (SolidColorBrush)new BrushConverter().ConvertFromString(colorHex)!;
    }

    private static SolidColorBrush CreateBrush(System.Windows.Media.Color color)
    {
        return new SolidColorBrush(color);
    }

    private static System.Windows.Media.Brush BuildPreviewBrush(string colorHex, int opacityPercent, string fallbackHex)
    {
        return BuildPreviewBrush(ParseColor(colorHex, fallbackHex), opacityPercent);
    }

    private static System.Windows.Media.Brush BuildPreviewBrush(System.Windows.Media.Color color, int opacityPercent)
    {
        var previewColor = color;
        previewColor.A = (byte)Math.Round(255d * Math.Clamp(opacityPercent, 0, 100) / 100d, MidpointRounding.AwayFromZero);
        return new SolidColorBrush(previewColor);
    }

    private static System.Windows.Media.Color ParseColor(string colorHex, string fallbackHex)
    {
        try
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex)!;
        }
        catch
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fallbackHex)!;
        }
    }

    private static System.Windows.Media.Color LightenColor(System.Windows.Media.Color color, double factor)
    {
        factor = Math.Clamp(factor, 0d, 1d);
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(color.R + (255 - color.R) * factor, MidpointRounding.AwayFromZero),
            (byte)Math.Round(color.G + (255 - color.G) * factor, MidpointRounding.AwayFromZero),
            (byte)Math.Round(color.B + (255 - color.B) * factor, MidpointRounding.AwayFromZero));
    }

    private static System.Windows.Media.Color DarkenColor(System.Windows.Media.Color color, double factor)
    {
        factor = Math.Clamp(factor, 0d, 1d);
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(color.R * (1d - factor), MidpointRounding.AwayFromZero),
            (byte)Math.Round(color.G * (1d - factor), MidpointRounding.AwayFromZero),
            (byte)Math.Round(color.B * (1d - factor), MidpointRounding.AwayFromZero));
    }

    private static System.Windows.Media.Color GetReadableForeground(System.Windows.Media.Color background)
    {
        var luminance = (0.299 * background.R) + (0.587 * background.G) + (0.114 * background.B);
        return luminance >= 168
            ? (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0F172A")!
            : Colors.White;
    }

    private static string NormalizeColorHex(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizeScreenshotHotkey(string? value)
    {
        return ScreenshotHotkeyBinding.NormalizeOrDefault(value);
    }

    private static string NormalizeServerBaseUrl(string? value)
    {
        return QuickReplyServiceEndpointDefaults.NormalizeConfiguredServiceBaseUrl(value);
    }

    private static string GetDefaultScreenshotToolSaveDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            return desktop;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private string GetDefaultIssueReportDirectory()
    {
        return Path.Combine(_viewModel.UserDataRootPath, "feedback");
    }

    private static int NormalizeDelaySelection(int value, int fallback)
    {
        if (value <= 0)
        {
            return fallback;
        }

        return Math.Clamp(value, 50, 2000);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        if (propertyName is nameof(SelectedSearchOverlayWidthPercent))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchPreviewWidth)));
        }

        if (propertyName is nameof(SelectedSearchInputBoxHeight))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchPreviewInputHeight)));
        }

        if (propertyName is nameof(SelectedSearchOverlayColorHex) or
            nameof(SelectedSearchOverlayOpacityPercent) or
            nameof(SelectedSearchInputColorHex) or
            nameof(SelectedSearchInputOpacityPercent) or
            nameof(SelectedTagColorHex) or
            nameof(SelectedTagOpacityPercent))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSearchOverlayColorPreviewBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSearchInputColorPreviewBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTagColorPreviewBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchPreviewBackgroundBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchPreviewBorderBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchPreviewInputBackgroundBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchPreviewInputBorderBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchPreviewTextBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchPreviewMutedBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchPreviewTagBackgroundBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchPreviewTagBorderBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchPreviewTagTextBrush)));
        }

        if (propertyName is nameof(SelectedCategoryColorHex) or
            nameof(SelectedCategoryOpacityPercent) or
            nameof(SelectedTagColorHex) or
            nameof(SelectedTagOpacityPercent) or
            nameof(SelectedPhraseColorHex) or
            nameof(SelectedPhraseOpacityPercent) or
            nameof(SelectedSidebarColorHex) or
            nameof(SelectedSidebarOpacityPercent))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCategoryColorPreviewBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTagColorPreviewBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedPhraseColorPreviewBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSidebarColorPreviewBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarPreviewBackgroundBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarPreviewBorderBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarPreviewCategoryBackgroundBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarPreviewCategoryBorderBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarPreviewCategoryTextBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarPreviewPhraseBackgroundBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarPreviewPhraseBorderBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarPreviewPhraseTextBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarPreviewTagBackgroundBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarPreviewTagBorderBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarPreviewTagTextBrush)));
        }

        if (propertyName is nameof(SelectedPhraseRowSpacing))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarPreviewPhraseBottomSpacing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarPreviewPhraseMargin)));
        }
    }
}
