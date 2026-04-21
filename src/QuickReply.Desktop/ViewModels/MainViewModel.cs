using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using QuickReply.Desktop.Commands;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject, IRecommendationPhraseCatalog
{
    private readonly SqliteAppDataStore _dataStore;
    private readonly IMessageComposeService _messageComposeService;
    private readonly PhraseExcelService _phraseExcelService;
    private readonly UsageReportExcelService _usageReportExcelService;
    private readonly PersonalConfigService _personalConfigService;
    private readonly PhraseImageStore _phraseImageStore;
    private readonly FileLibraryCatalogService _fileLibraryCatalogService;
    private readonly AppearanceThemeCatalogService _appearanceThemeCatalogService;
    private readonly StartupRegistrationService _startupRegistrationService;
    private readonly IExternalWorkbenchService _windowTracker;
    private readonly WindowDockService _windowDockService;
    private readonly ConversationLoggingService _conversationLoggingService;
    private readonly ExceptionLogService _exceptionLogService;
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<Guid> _dirtyCategoryIds = [];
    private readonly HashSet<Guid> _dirtyPhraseIds = [];
    private readonly Dictionary<Guid, PhraseUsageStat> _phraseUsageStats = [];
    private readonly Dictionary<Guid, FileUsageStat> _fileUsageStats = [];
    private readonly HashSet<string> _dismissedPlainTextSuggestionKeys = [];
    private readonly HashSet<string> _pendingPlainTextSuggestionKeys = [];

    private AppData _data = new();
    private string _searchText = string.Empty;
    private ReplyCategory? _selectedCategory;
    private ReplyPhrase? _selectedPhrase;
    private string _statusMessage = "鍑嗗灏辩华";
    private DateTimeOffset _sessionStartedAt;
    private bool _isInitialized;
    private bool _isPreviewingSettings;
    private bool _isSidebarSettingsPreviewActive;
    private bool _isCompactSidebarExpanded;
    private bool _savePending;
    private bool _settingsDirty;
    private bool _suppressPersistenceNotifications;
    private bool _suppressStartupConfigurationNotifications;
    private bool _appliedLaunchAtStartup;
    private bool _appliedRunAsAdministrator;

    public MainViewModel(
        SqliteAppDataStore dataStore,
        IMessageComposeService messageComposeService,
        PhraseExcelService phraseExcelService,
        UsageReportExcelService usageReportExcelService,
        PersonalConfigService personalConfigService,
        IExternalWorkbenchService windowTracker,
        WindowDockService windowDockService,
        ConversationLoggingService conversationLoggingService,
        ExceptionLogService exceptionLogService)
    {
        _dataStore = dataStore;
        _messageComposeService = messageComposeService;
        _phraseExcelService = phraseExcelService;
        _usageReportExcelService = usageReportExcelService;
        _personalConfigService = personalConfigService;
        _phraseImageStore = new PhraseImageStore(dataStore.DataRootPath);
        _fileLibraryCatalogService = new FileLibraryCatalogService(dataStore);
        _appearanceThemeCatalogService = new AppearanceThemeCatalogService(dataStore);
        _startupRegistrationService = new StartupRegistrationService();
        _windowTracker = windowTracker;
        _windowDockService = windowDockService;
        _conversationLoggingService = conversationLoggingService;
        _exceptionLogService = exceptionLogService;
        _windowDockService.CompactSidebarExpandedChanged += OnCompactSidebarExpandedChanged;

        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _saveTimer.Tick += async (_, _) => await FlushPendingSaveAsync();

        FilteredPhrases = [];
        SendHotkeyModes = Enum.GetValues<SendHotkeyMode>();
        SendDelayOptions = [80, 120, 160, 220, 300, 450];

        AddCategoryCommand = new AsyncRelayCommand(AddCategoryAsync);
        DeleteCategoryCommand = new AsyncRelayCommand(DeleteCategoryAsync, () => SelectedCategory is not null);
        AddPhraseCommand = new AsyncRelayCommand(AddPhraseAsync);
        DeletePhraseCommand = new AsyncRelayCommand(DeletePhraseAsync, () => SelectedPhrase is not null);
        AddQuickTagCommand = new AsyncRelayCommand(AddQuickTagAsync, CanAddQuickTag);
        CopyPhraseCommand = new AsyncRelayCommand(CopySelectedPhraseAsync, CanUseSelectedPhrase);
        InputToCurrentWindowCommand = new AsyncRelayCommand(() => InputSelectedPhraseAsync(), CanUseSelectedPhrase);
        SendPhraseCommand = new AsyncRelayCommand(() => SendSelectedPhraseAsync(), CanUseSelectedPhrase);
        SendQuickTagCommand = new AsyncRelayCommand<QuickTag>(SendQuickTagAsync, tag => tag is not null);
        DeleteQuickTagCommand = new AsyncRelayCommand<QuickTag>(DeleteQuickTagAsync, tag => tag is not null);
        MoveQuickTagLeftCommand = new AsyncRelayCommand<QuickTag>(tag => MoveQuickTagAsync(tag, -1), tag => CanMoveQuickTag(tag, -1));
        MoveQuickTagRightCommand = new AsyncRelayCommand<QuickTag>(tag => MoveQuickTagAsync(tag, 1), tag => CanMoveQuickTag(tag, 1));
        SaveNowCommand = new AsyncRelayCommand(() => SaveNowAsync(true));
    }

    public ObservableCollection<ReplyCategory> Categories => _data.Categories;

    public ObservableCollection<QuickTag> QuickTags => _data.QuickTags;

    public ObservableCollection<LibraryFileItem> FileItems => _data.FileItems;

    public ObservableCollection<ReplyPhrase> FilteredPhrases { get; }

    public AppSettings Settings => _data.Settings;

    public SendHotkeyMode[] SendHotkeyModes { get; }

    public int[] SendDelayOptions { get; }

    public AsyncRelayCommand AddCategoryCommand { get; }

    public AsyncRelayCommand DeleteCategoryCommand { get; }

    public AsyncRelayCommand AddPhraseCommand { get; }

    public AsyncRelayCommand DeletePhraseCommand { get; }

    public AsyncRelayCommand AddQuickTagCommand { get; }

    public AsyncRelayCommand CopyPhraseCommand { get; }

    public AsyncRelayCommand InputToCurrentWindowCommand { get; }

    public AsyncRelayCommand SendPhraseCommand { get; }

    public AsyncRelayCommand<QuickTag> SendQuickTagCommand { get; }

    public AsyncRelayCommand<QuickTag> DeleteQuickTagCommand { get; }

    public AsyncRelayCommand<QuickTag> MoveQuickTagLeftCommand { get; }

    public AsyncRelayCommand<QuickTag> MoveQuickTagRightCommand { get; }

    public AsyncRelayCommand SaveNowCommand { get; }

    public event EventHandler<FrequentReplyPhraseSuggestion>? FrequentReplyPhraseSuggested;

    public bool IsCompactPhraseList => true;

    public Visibility PhraseContentVisibility => Visibility.Visible;

    public Thickness PhraseRowMargin => new Thickness(0, 0, 0, Settings.PhraseRowSpacing);

    public Thickness PhraseCardPadding
    {
        get
        {
            var verticalPadding = Math.Clamp(Settings.PhraseRowSpacing + 2, 2, 8);
            return new Thickness(6, verticalPadding, 6, verticalPadding);
        }
    }

    public Thickness PhraseArrowMargin => new Thickness(2, 0, 0, 0);

    public double PhraseArrowButtonSize => Math.Clamp(Settings.PhraseFontSize + 8, 18, 28);

    public double PhraseArrowFontSize => Math.Clamp(Settings.PhraseFontSize, 11, 16);

    public double PhraseTitleFontSize => Settings.PhraseFontSize;

    public double CategoryChipFontSize => Settings.CategoryFontSize;

    public Thickness PhraseTagsMargin => IsCompactPhraseList ? new Thickness(0, 2, 0, 0) : new Thickness(0, 4, 0, 0);

    public Thickness PhraseContentMargin => new Thickness(6, 0, 0, 0);

    public Thickness PhraseCategoryPadding => IsCompactPhraseList ? new Thickness(5, 1, 5, 1) : new Thickness(6, 2, 6, 2);

    public double PhraseCategoryFontSize => IsCompactPhraseList ? 10 : 11;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshFilteredPhrases();
                OnPropertyChanged(nameof(IsSearchActive));
                OnPropertyChanged(nameof(PhraseListTitle));
            }
        }
    }

    public ReplyCategory? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                OnPropertyChanged(nameof(HasSelectedCategory));
                OnPropertyChanged(nameof(PhraseListTitle));
                RefreshFilteredPhrases();
                UpdateCommandStates();
            }
        }
    }

    public ReplyPhrase? SelectedPhrase
    {
        get => _selectedPhrase;
        set
        {
            if (SetProperty(ref _selectedPhrase, value))
            {
                OnPropertyChanged(nameof(HasSelectedPhrase));
                UpdateCommandStates();
            }
        }
    }

    public bool HasSelectedPhrase => SelectedPhrase is not null;

    public bool HasSelectedCategory => SelectedCategory is not null;

    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchText);

    public string PhraseListTitle => IsSearchActive
        ? "鎼滅储缁撴灉"
        : SelectedCategory?.Name is { Length: > 0 } categoryName
            ? $"鐭 - {categoryName}"
            : "鐭";

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string StoragePath => _dataStore.DatabasePath;

    public string UserDataRootPath => _dataStore.DataRootPath;

    public string DefaultPhraseImageFolderPath => PhraseImageStore.GetDefaultImagesRootPath(_dataStore.DataRootPath);

    public string CurrentPhraseImageFolderPath => _phraseImageStore.ImagesRootPath;

    public string ExceptionLogRootPath => _exceptionLogService.RootPath;

    public bool IsCompactSidebarExpanded
    {
        get => _isCompactSidebarExpanded;
        private set
        {
            if (!SetProperty(ref _isCompactSidebarExpanded, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CompactLauncherVisibility));
            OnPropertyChanged(nameof(SidebarWorkspaceVisibility));
        }
    }

    public Visibility CompactLauncherVisibility => Visibility.Collapsed;

    public Visibility SidebarWorkspaceVisibility => Settings.SidebarWorkMode == SidebarWorkMode.Compact && !_isSidebarSettingsPreviewActive
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string? GetPhraseImageAbsolutePath(ReplyPhrase phrase)
    {
        return ResolvePhraseImagePaths(phrase).FirstOrDefault();
    }

    public IReadOnlyList<string> GetPhraseImageAbsolutePaths(ReplyPhrase phrase)
    {
        return ResolvePhraseImagePaths(phrase);
    }

    public IReadOnlyList<ReplyPhrase> SearchPhrases(string keyword, int maxResults = 9)
    {
        var normalizedKeyword = keyword.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKeyword))
        {
            return [];
        }

        return Categories
            .SelectMany(category => category.Phrases)
            .Where(phrase => PhraseMatchesKeyword(phrase, normalizedKeyword))
            .OrderBy(phrase => !phrase.Title.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase))
            .ThenBy(phrase => !phrase.Tags.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(phrase => GetPhraseSendCount(phrase.Id))
            .ThenByDescending(phrase => GetPhraseLastSentAt(phrase.Id) ?? DateTimeOffset.MinValue)
            .ThenBy(phrase => phrase.CategoryName)
            .ThenBy(phrase => phrase.SortOrder)
            .ThenBy(phrase => phrase.CreatedAt)
            .Take(maxResults)
            .ToList();
    }

    public IReadOnlyList<LibraryFileItem> SearchFileItems(string keyword, int maxResults = 9)
    {
        var normalizedKeyword = keyword.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKeyword))
        {
            return [];
        }

        return FileItems
            .Where(fileItem => FileItemMatchesKeyword(fileItem, normalizedKeyword))
            .OrderBy(fileItem => !fileItem.DisplayName.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase))
            .ThenBy(fileItem => !fileItem.FileName.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(fileItem => GetFileUseCount(fileItem.Id))
            .ThenByDescending(fileItem => GetFileLastUsedAt(fileItem.Id) ?? DateTimeOffset.MinValue)
            .ThenBy(fileItem => fileItem.DisplayName)
            .ThenBy(fileItem => fileItem.CreatedAt)
            .Take(maxResults)
            .ToList();
    }

    public IReadOnlyList<ReplyPhrase> GetFrequentPhrases(int maxResults, IEnumerable<Guid>? excludedPhraseIds = null)
    {
        var excludedIds = excludedPhraseIds?.ToHashSet() ?? [];

        return Categories
            .SelectMany(category => category.Phrases)
            .Where(phrase => !excludedIds.Contains(phrase.Id))
            .OrderByDescending(phrase => GetPhraseSendCount(phrase.Id))
            .ThenByDescending(phrase => GetPhraseLastSentAt(phrase.Id) ?? DateTimeOffset.MinValue)
            .ThenBy(phrase => phrase.CategoryName)
            .ThenBy(phrase => phrase.SortOrder)
            .ThenBy(phrase => phrase.CreatedAt)
            .Take(maxResults)
            .ToList();
    }

    public async Task SyncFileLibraryAsync(bool updateStatus = true)
    {
        var syncResult = await _fileLibraryCatalogService.SyncAsync(FileItems.ToArray(), Settings.FileLibraryFolderPath);
        var removedItems = syncResult.RemovedItems;
        var addedItems = syncResult.AddedItems;

        if (removedItems.Count > 0)
        {
            await MutateCollectionsAsync(() =>
            {
                foreach (var removedItem in removedItems)
                {
                    FileItems.Remove(removedItem);
                }

                return Task.CompletedTask;
            });
        }

        if (addedItems.Count > 0)
        {
            await MutateCollectionsAsync(() =>
            {
                foreach (var fileItem in addedItems)
                {
                    FileItems.Add(fileItem);
                }

                return Task.CompletedTask;
            });
        }

        if (!updateStatus)
        {
            return;
        }

        StatusMessage = removedItems.Count == 0 && addedItems.Count == 0
            ? "文件库已检查，没有发现需要更新的文件。"
            : $"文件库已同步：移除 {removedItems.Count} 个失效文件，新增 {addedItems.Count} 个资料文件。";
    }

    public async Task InitializeAsync(Window window)
    {
        StartupTrace.Mark("main_vm.initialize.start");
        _data = await _dataStore.LoadAsync();
        StartupTrace.Mark("main_vm.initialize.data_loaded");
        RetiredFeatureSettingsPolicy.Apply(_data.Settings);
        _phraseImageStore.UpdateRootPath(_data.Settings.PhraseImageFolderPath);
        await MigrateLegacyPhraseImageLayoutAsync();
        StartupTrace.Mark("main_vm.initialize.image_layout_ready");
        LoadPhraseUsageStats(await _dataStore.LoadPhraseUsageStatsAsync());
        LoadFileUsageStats(await _dataStore.LoadFileUsageStatsAsync());
        _conversationLoggingService.ApplySettings(_data.Settings);
        _conversationLoggingService.RequestMaintenance();
        RememberAppliedStartupConfiguration(Settings.LaunchAtStartup, Settings.RunAsAdministrator);
        WireData();
        SyncPhraseCategoryNames();
        SyncQuickTagsFromPhrases();
        await SyncFileLibraryAsync(false);
        StartupTrace.Mark("main_vm.initialize.collections_ready");

        OnPropertyChanged(nameof(Categories));
        OnPropertyChanged(nameof(QuickTags));
        OnPropertyChanged(nameof(FileItems));
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(StoragePath));
        OnPropertyChanged(nameof(PhraseListTitle));

        SelectedCategory = Categories.FirstOrDefault();
        RefreshFilteredPhrases();

        _windowTracker.Start(window);
        StartupTrace.Mark("main_vm.initialize.window_tracker_started");
        _windowDockService.ApplySettings(Settings);
        _windowDockService.Start(window);
        StartupTrace.Mark("main_vm.initialize.window_dock_started");
        ApplyStartupConfiguration(updateStatusMessage: false);
        StartupTrace.Mark("main_vm.initialize.startup_config_applied");
        _sessionStartedAt = DateTimeOffset.Now;
        await _dataStore.RecordAppLaunchAsync();
        StartupTrace.Mark("main_vm.initialize.launch_recorded");
        _isInitialized = true;
        StatusMessage = $"已从 SQLite 加载 {Categories.Sum(category => category.Phrases.Count)} 条短语，分类 {Categories.Count} 个。";
        StartupTrace.Mark("main_vm.initialize.completed");
    }

    public async Task ShutdownAsync()
    {
        _windowTracker.Stop();
        _windowDockService.Stop();
        var runtimeSeconds = Math.Max(0, (long)(DateTimeOffset.Now - _sessionStartedAt).TotalSeconds);
        await _dataStore.AddRuntimeSecondsAsync(runtimeSeconds);
        await SaveNowAsync(false);
    }

    public async Task InputSelectedPhraseAsync(string triggerSource = "selected_phrase_input", string? searchKeyword = null)
    {
        if (!TryGetSelectedPhraseContent(out var content))
        {
            return;
        }

        try
        {
            var targetWindowHandle = _windowTracker.GetLastExternalWindowHandle();
            var result = await _messageComposeService.ExecuteAsync(content, Settings, ComposeActionMode.Input, ResolvePhraseImagePaths(SelectedPhrase));
            LogSelectedPhraseUsage(triggerSource, searchKeyword, ComposeActionMode.Input, result, targetWindowHandle);
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            LogSelectedPhraseUsage(
                triggerSource,
                searchKeyword,
                ComposeActionMode.Send,
                new ReplyActionResult(false, $"发送失败：{ex.Message}"),
                _windowTracker.GetLastExternalWindowHandle());
            LogSelectedPhraseUsage(
                triggerSource,
                searchKeyword,
                ComposeActionMode.Input,
                new ReplyActionResult(false, $"输入失败：{ex.Message}"),
                _windowTracker.GetLastExternalWindowHandle());
            StatusMessage = $"输入失败：{ex.Message}";
        }
    }

    public async Task SendSelectedPhraseAsync(string triggerSource = "selected_phrase_send", string? searchKeyword = null)
    {
        if (!TryGetSelectedPhraseContent(out var content))
        {
            return;
        }

        try
        {
            var targetWindowHandle = _windowTracker.GetLastExternalWindowHandle();
            var result = await _messageComposeService.ExecuteAsync(content, Settings, ComposeActionMode.Send, ResolvePhraseImagePaths(SelectedPhrase));
            if (result.Success && SelectedPhrase is not null)
            {
                RecordPhraseSend(SelectedPhrase.Id);
            }
            LogSelectedPhraseUsage(triggerSource, searchKeyword, ComposeActionMode.Send, result, targetWindowHandle);
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            StatusMessage = $"发送失败：{ex.Message}";
        }
    }

    public async Task InputPhraseAsync(ReplyPhrase phrase, string triggerSource = "phrase_list_input", string? searchKeyword = null)
    {
        SelectedPhrase = phrase;
        await InputSelectedPhraseAsync(triggerSource, searchKeyword);
    }

    public async Task SendPhraseAsync(ReplyPhrase phrase, string triggerSource = "phrase_list_send", string? searchKeyword = null)
    {
        await SendPhraseDirectAsync(phrase, triggerSource, searchKeyword);
    }

    public async Task<ReplyActionResult> CopyPhraseDirectAsync(
        ReplyPhrase phrase,
        string triggerSource = "phrase_direct_copy",
        string? searchKeyword = null)
    {
        SelectedPhrase = phrase;

        if (string.IsNullOrWhiteSpace(phrase.Content) && !phrase.HasImage)
        {
            var emptyResult = new ReplyActionResult(false, "当前短语内容为空，无法复制。");
            StatusMessage = emptyResult.Message;
            return emptyResult;
        }

        try
        {
            var targetWindowHandle = _windowTracker.GetLastExternalWindowHandle();
            var result = await _messageComposeService.CopyToClipboardAsync(phrase.Content, ResolvePhraseImagePaths(phrase));
            LogPhraseUsage(phrase, triggerSource, searchKeyword, ComposeActionMode.Copy, result, targetWindowHandle);
            StatusMessage = $"{GetPhraseDisplayName(phrase)}：{result.Message}";
            return result;
        }
        catch (Exception ex)
        {
            var failedResult = new ReplyActionResult(false, $"复制失败：{ex.Message}");
            LogPhraseUsage(phrase, triggerSource, searchKeyword, ComposeActionMode.Copy, failedResult, _windowTracker.GetLastExternalWindowHandle());
            StatusMessage = failedResult.Message;
            return failedResult;
        }
    }

    public async Task<ReplyActionResult> CopyPhraseImagesDirectAsync(
        ReplyPhrase phrase,
        string triggerSource = "phrase_direct_copy_images",
        string? searchKeyword = null)
    {
        SelectedPhrase = phrase;

        var imagePaths = ResolvePhraseImagePaths(phrase);
        if (imagePaths.Count == 0)
        {
            var emptyResult = new ReplyActionResult(false, "当前短语没有可复制的图片。");
            StatusMessage = emptyResult.Message;
            return emptyResult;
        }

        try
        {
            var targetWindowHandle = _windowTracker.GetLastExternalWindowHandle();
            var result = await _messageComposeService.CopyFilesToClipboardAsync(imagePaths);
            LogPhraseUsage(phrase, triggerSource, searchKeyword, ComposeActionMode.Copy, result, targetWindowHandle);
            StatusMessage = $"{GetPhraseDisplayName(phrase)}：{result.Message}";
            return result;
        }
        catch (Exception ex)
        {
            var failedResult = new ReplyActionResult(false, $"复制图片失败：{ex.Message}");
            LogPhraseUsage(phrase, triggerSource, searchKeyword, ComposeActionMode.Copy, failedResult, _windowTracker.GetLastExternalWindowHandle());
            StatusMessage = failedResult.Message;
            return failedResult;
        }
    }

    public async Task<ReplyActionResult> InputPhraseDirectAsync(
        ReplyPhrase phrase,
        string triggerSource = "phrase_direct_input",
        string? searchKeyword = null,
        ConversationRecommendationSnapshot? recognitionOverride = null)
    {
        SelectedPhrase = phrase;

        if (string.IsNullOrWhiteSpace(phrase.Content) && !phrase.HasImage)
        {
            var emptyResult = new ReplyActionResult(false, "当前短语内容为空，无法输入。");
            StatusMessage = emptyResult.Message;
            return emptyResult;
        }

        try
        {
            var targetWindowHandle = _windowTracker.GetLastExternalWindowHandle();
            var result = await _messageComposeService.ExecuteAsync(phrase.Content, Settings, ComposeActionMode.Input, ResolvePhraseImagePaths(phrase));
            LogPhraseUsage(phrase, triggerSource, searchKeyword, ComposeActionMode.Input, result, targetWindowHandle, recognitionOverride);
            StatusMessage = $"{GetPhraseDisplayName(phrase)}：{result.Message}";
            return result;
        }
        catch (Exception ex)
        {
            var failedResult = new ReplyActionResult(false, $"输入失败：{ex.Message}");
            LogPhraseUsage(phrase, triggerSource, searchKeyword, ComposeActionMode.Input, failedResult, _windowTracker.GetLastExternalWindowHandle(), recognitionOverride);
            StatusMessage = failedResult.Message;
            return failedResult;
        }
    }

    public async Task<ReplyActionResult> SendPlainTextAsync(
        string content,
        string triggerSource = "plain_text_send",
        string? searchKeyword = null,
        ConversationRecommendationSnapshot? recognitionOverride = null)
    {
        var normalizedContent = NormalizePlainTextContent(content);
        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            var emptyResult = new ReplyActionResult(false, "请输入要发送的原文内容。");
            StatusMessage = emptyResult.Message;
            return emptyResult;
        }

        try
        {
            var targetWindowHandle = _windowTracker.GetLastExternalWindowHandle();
            var result = await _messageComposeService.ExecuteAsync(normalizedContent, Settings, ComposeActionMode.Send);
            _conversationLoggingService.LogPlainTextUsage(
                normalizedContent,
                triggerSource,
                ComposeActionMode.Send,
                searchKeyword,
                result.Success,
                result.Message,
                targetWindowHandle,
                result.ComposeTelemetry,
                recognitionOverride);
            StatusMessage = result.Message;

            if (result.Success)
            {
                await EvaluateFrequentPlainTextSuggestionAsync(normalizedContent);
            }

            return result;
        }
        catch (Exception ex)
        {
            var failedResult = new ReplyActionResult(false, $"发送原文失败：{ex.Message}");
            _conversationLoggingService.LogPlainTextUsage(
                normalizedContent,
                triggerSource,
                ComposeActionMode.Send,
                searchKeyword,
                failedResult.Success,
                failedResult.Message,
                _windowTracker.GetLastExternalWindowHandle(),
                failedResult.ComposeTelemetry,
                recognitionOverride);
            StatusMessage = failedResult.Message;
            return failedResult;
        }
    }

    public async Task<ReplyActionResult> InputPlainTextAsync(
        string content,
        string triggerSource = "plain_text_input",
        string? searchKeyword = null,
        ConversationRecommendationSnapshot? recognitionOverride = null)
    {
        var normalizedContent = NormalizePlainTextContent(content);
        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            var emptyResult = new ReplyActionResult(false, "请输入要输入的原文内容。");
            StatusMessage = emptyResult.Message;
            return emptyResult;
        }

        try
        {
            var targetWindowHandle = _windowTracker.GetLastExternalWindowHandle();
            var result = await _messageComposeService.ExecuteAsync(normalizedContent, Settings, ComposeActionMode.Input);
            _conversationLoggingService.LogPlainTextUsage(
                normalizedContent,
                triggerSource,
                ComposeActionMode.Input,
                searchKeyword,
                result.Success,
                result.Message,
                targetWindowHandle,
                result.ComposeTelemetry,
                recognitionOverride);
            StatusMessage = result.Message;
            return result;
        }
        catch (Exception ex)
        {
            var failedResult = new ReplyActionResult(false, $"输入原文失败：{ex.Message}");
            _conversationLoggingService.LogPlainTextUsage(
                normalizedContent,
                triggerSource,
                ComposeActionMode.Input,
                searchKeyword,
                failedResult.Success,
                failedResult.Message,
                _windowTracker.GetLastExternalWindowHandle(),
                failedResult.ComposeTelemetry,
                recognitionOverride);
            StatusMessage = failedResult.Message;
            return failedResult;
        }
    }

    public async Task<ReplyActionResult> SavePlainTextAsPhraseAsync(string content)
    {
        var normalizedContent = NormalizePlainTextContent(content);
        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            var emptyResult = new ReplyActionResult(false, "请输入要保存的原文内容。");
            StatusMessage = emptyResult.Message;
            return emptyResult;
        }

        var existingPhrase = FindExactPhraseByContent(normalizedContent);
        if (existingPhrase is not null)
        {
            SelectPhrase(existingPhrase);
            var existingResult = new ReplyActionResult(true, $"已存在快捷短语：{existingPhrase.Title}");
            StatusMessage = existingResult.Message;
            MarkPlainTextSuggestionHandled(normalizedContent, dismissed: true);
            return existingResult;
        }

        var category = await ResolvePhraseSaveCategoryAsync();
        if (category is null)
        {
            var failedResult = new ReplyActionResult(false, "没有可用分类，无法保存快捷短语。");
            StatusMessage = failedResult.Message;
            return failedResult;
        }

        await InsertPhraseCoreAsync(
            category,
            null,
            BuildPlainTextPhraseTitle(normalizedContent),
            string.Empty,
            normalizedContent);

        MarkPlainTextSuggestionHandled(normalizedContent, dismissed: true);
        var result = new ReplyActionResult(true, "已保存快捷短语。");
        StatusMessage = result.Message;
        return result;
    }

    public async Task<ReplyPhrase?> CreatePhraseAsync(string title, string tags, string content, IReadOnlyCollection<string>? imageSourcePaths = null)
    {
        var category = await EnsureSelectedCategoryAsync();
        if (category is null)
        {
            StatusMessage = "请先创建或选择一个分类。";
            return null;
        }

        return await InsertPhraseCoreAsync(category, null, title, tags, content, imageSourcePaths);
    }

    public async Task<ReplyActionResult> AcceptFrequentReplyPhraseSuggestionAsync(FrequentReplyPhraseSuggestion suggestion)
    {
        var result = await SavePlainTextAsPhraseAsync(suggestion.PayloadText);
        await _conversationLoggingService.LogOperationEventAsync(
            "frequent_reply_suggestion",
            "accepted",
            result.Success,
            skipped: false,
            durationMs: 0,
            message: result.Message,
            details: new
            {
                payloadText = suggestion.PayloadText,
                useCount = suggestion.UseCount,
                suggestedTitle = suggestion.SuggestedTitle
            });
        MarkPlainTextSuggestionHandled(suggestion.PayloadText, dismissed: true);
        return result;
    }

    public void DismissFrequentReplyPhraseSuggestion(FrequentReplyPhraseSuggestion suggestion)
    {
        _ = _conversationLoggingService.LogOperationEventAsync(
            "frequent_reply_suggestion",
            "dismissed",
            success: true,
            skipped: false,
            durationMs: 0,
            message: "用户暂时忽略了高频原文保存建议。",
            details: new
            {
                payloadText = suggestion.PayloadText,
                useCount = suggestion.UseCount,
                suggestedTitle = suggestion.SuggestedTitle
            });
        MarkPlainTextSuggestionHandled(suggestion.PayloadText, dismissed: true);
    }

    public async Task<ReplyPhrase?> InsertPhraseAfterAsync(ReplyPhrase anchorPhrase, string title, string tags, string content, IReadOnlyCollection<string>? imageSourcePaths = null)
    {
        var category = Categories.FirstOrDefault(item => item.Id == anchorPhrase.CategoryId);
        if (category is null)
        {
            StatusMessage = "未找到当前短语所属分类。";
            return null;
        }

        return await InsertPhraseCoreAsync(category, anchorPhrase, title, tags, content, imageSourcePaths);
    }

    public async Task UpdatePhraseAsync(ReplyPhrase phrase, string title, string tags, string content, IReadOnlyCollection<string>? imageSourcePaths = null, bool removeImage = false)
    {
        SelectedPhrase = phrase;
        SearchOverlayDiagnostics.Log(
            "phrase_images",
            "update_phrase_requested",
            new Dictionary<string, object?>
            {
                ["phraseId"] = phrase.Id.ToString("N"),
                ["title"] = title,
                ["removeImage"] = removeImage,
                ["imageSourceCount"] = imageSourcePaths?.Count ?? 0,
                ["imageSourcePaths"] = imageSourcePaths is null ? string.Empty : string.Join("|", imageSourcePaths)
            });
        phrase.Title = NormalizePhraseTitle(title);
        phrase.Tags = NormalizePhraseTags(tags);
        phrase.Content = content.Trim();
        if (removeImage)
        {
            DeletePhraseImages(phrase);
        }
        else if (imageSourcePaths is not null && imageSourcePaths.Count > 0)
        {
            var savedImagePaths = await _phraseImageStore.SavePhraseImagesAsync(phrase.Id, imageSourcePaths);
            if (savedImagePaths.Count > 0)
            {
                DeleteTransientImagesIfNeeded(imageSourcePaths);
                phrase.SetImageRelativePaths(savedImagePaths);
            }
        }
        SearchOverlayDiagnostics.Log(
            "phrase_images",
            "update_phrase_completed",
            new Dictionary<string, object?>
            {
                ["phraseId"] = phrase.Id.ToString("N"),
                ["storedImageRelativePath"] = phrase.ImageRelativePath,
                ["storedImageCount"] = phrase.ImageCount
            });
        _dirtyPhraseIds.Add(phrase.Id);
        ScheduleSave();
        StatusMessage = $"已更新短语：{phrase.Title}";
    }

    public async Task DeletePhraseAsync(ReplyPhrase phrase)
    {
        SelectedPhrase = phrase;
        await DeletePhraseAsync();
    }

    public async Task<int> DeletePhrasesAsync(IEnumerable<ReplyPhrase> phrases)
    {
        var targets = phrases
            .Where(phrase => phrase is not null)
            .GroupBy(phrase => phrase.Id)
            .Select(group => group.First())
            .ToList();
        if (targets.Count == 0)
        {
            return 0;
        }

        foreach (var phrase in targets)
        {
            DeletePhraseImages(phrase);
            await _dataStore.DeletePhraseAsync(phrase.Id);
            _dirtyPhraseIds.Remove(phrase.Id);
            _phraseUsageStats.Remove(phrase.Id);
        }

        await MutateCollectionsAsync(() =>
        {
            foreach (var phrase in targets)
            {
                var ownerCategory = Categories.FirstOrDefault(category => category.Id == phrase.CategoryId);
                ownerCategory?.Phrases.Remove(phrase);
                RemoveQuickTagsForPhrase(phrase.Id);
            }

            if (SelectedPhrase is not null && targets.Any(phrase => phrase.Id == SelectedPhrase.Id))
            {
                SelectedPhrase = null;
            }

            RefreshFilteredPhrases();
            return Task.CompletedTask;
        });

        await NormalizeAndPersistQuickTagsAsync();
        StatusMessage = $"已删除 {targets.Count} 条快捷短语。";
        return targets.Count;
    }

    public IReadOnlyList<ReplyPhrase> GetMultiSelectedPhrases()
    {
        return Categories
            .SelectMany(category => category.Phrases)
            .Where(phrase => phrase.IsMultiSelected)
            .DistinctBy(phrase => phrase.Id)
            .ToList();
    }

    public void ClearPhraseMultiSelection()
    {
        foreach (var phrase in Categories.SelectMany(category => category.Phrases))
        {
            phrase.IsMultiSelected = false;
        }
    }

    public async Task<int> MovePhrasesToCategoryAsync(IEnumerable<ReplyPhrase> phrases, ReplyCategory targetCategory)
    {
        var targets = phrases
            .Where(phrase => phrase is not null)
            .GroupBy(phrase => phrase.Id)
            .Select(group => group.First())
            .ToList();
        if (targets.Count == 0)
        {
            return 0;
        }

        var affectedCategories = new HashSet<ReplyCategory>();

        await MutateCollectionsAsync(() =>
        {
            foreach (var phrase in targets)
            {
                var sourceCategory = Categories.FirstOrDefault(category => category.Id == phrase.CategoryId);
                if (sourceCategory is not null)
                {
                    sourceCategory.Phrases.Remove(phrase);
                    affectedCategories.Add(sourceCategory);
                }
            }

            foreach (var phrase in targets)
            {
                phrase.CategoryId = targetCategory.Id;
                phrase.CategoryName = targetCategory.Name;
                phrase.IsMultiSelected = false;
                targetCategory.Phrases.Add(phrase);
                SyncQuickTagFromPhrase(phrase);
            }

            affectedCategories.Add(targetCategory);

            foreach (var category in affectedCategories)
            {
                NormalizePhraseOrder(category);
            }

            if (SelectedPhrase is not null && targets.Any(phrase => phrase.Id == SelectedPhrase.Id))
            {
                SelectedPhrase = targets.LastOrDefault();
            }

            RefreshFilteredPhrases();
            return Task.CompletedTask;
        });

        foreach (var category in affectedCategories)
        {
            await PersistPhraseOrderAsync(category.Phrases.ToArray());
        }

        await NormalizeAndPersistQuickTagsAsync();
        StatusMessage = $"已移动 {targets.Count} 条快捷短语到 {targetCategory.Name}。";
        return targets.Count;
    }

    public Task UpdatePhraseTagsAsync(ReplyPhrase phrase, string tags)
    {
        SelectedPhrase = phrase;
        phrase.Tags = NormalizePhraseTags(tags);
        _dirtyPhraseIds.Add(phrase.Id);
        ScheduleSave();
        StatusMessage = $"已更新标签：{phrase.Title}";
        return Task.CompletedTask;
    }

    public async Task<ReplyCategory> CreateCategoryAsync(string name)
    {
        var now = DateTimeOffset.Now;
        var category = new ReplyCategory
        {
            Id = Guid.NewGuid(),
            Name = NormalizeCategoryName(name, Categories.Count + 1),
            SortOrder = Categories.Count + 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _dataStore.InsertCategoryAsync(category);

        await MutateCollectionsAsync(() =>
        {
            Categories.Add(category);
            SelectedCategory = category;
            return Task.CompletedTask;
        });

        StatusMessage = $"已新增分类：{category.Name}";
        return category;
    }

    public Task UpdateCategoryAsync(ReplyCategory category, string name)
    {
        category.Name = NormalizeCategoryName(name, category.SortOrder);
        SelectedCategory = category;
        _dirtyCategoryIds.Add(category.Id);
        ScheduleSave();
        StatusMessage = $"已更新分类：{category.Name}";
        return Task.CompletedTask;
    }

    public async Task DeleteCategoryWithPhrasesAsync(ReplyCategory category)
    {
        await _dataStore.DeleteCategoryWithPhrasesAsync(category.Id);
        _dirtyCategoryIds.Remove(category.Id);

        await MutateCollectionsAsync(() =>
        {
            foreach (var phrase in category.Phrases.ToList())
            {
                DeletePhraseImages(phrase);
                _dirtyPhraseIds.Remove(phrase.Id);
                RemoveQuickTagsForPhrase(phrase.Id);
            }

            Categories.Remove(category);
            NormalizeCategoryOrder();

            foreach (var remainingCategory in Categories)
            {
                _dirtyCategoryIds.Add(remainingCategory.Id);
            }

            SelectedCategory = Categories.FirstOrDefault();
            RefreshFilteredPhrases();
            return Task.CompletedTask;
        });

        ScheduleSave();
        StatusMessage = $"已删除分类：{category.Name}";
    }

    public async Task<int> MergeCategoryIntoAsync(ReplyCategory sourceCategory, ReplyCategory targetCategory)
    {
        if (sourceCategory.Id == targetCategory.Id)
        {
            return 0;
        }

        var movedPhrases = sourceCategory.Phrases.ToList();
        var affectedCategories = new HashSet<ReplyCategory> { targetCategory };
        if (movedPhrases.Count > 0)
        {
            affectedCategories.Add(sourceCategory);
        }

        await MutateCollectionsAsync(() =>
        {
            foreach (var phrase in movedPhrases)
            {
                sourceCategory.Phrases.Remove(phrase);
                phrase.CategoryId = targetCategory.Id;
                phrase.CategoryName = targetCategory.Name;
                targetCategory.Phrases.Add(phrase);
                SyncQuickTagFromPhrase(phrase);
            }

            NormalizePhraseOrder(targetCategory);
            Categories.Remove(sourceCategory);
            NormalizeCategoryOrder();

            foreach (var category in Categories)
            {
                _dirtyCategoryIds.Add(category.Id);
            }

            foreach (var phrase in targetCategory.Phrases)
            {
                _dirtyPhraseIds.Add(phrase.Id);
            }

            if (ReferenceEquals(SelectedCategory, sourceCategory))
            {
                SelectedCategory = targetCategory;
            }

            RefreshFilteredPhrases();
            return Task.CompletedTask;
        });

        foreach (var phrase in targetCategory.Phrases)
        {
            await _dataStore.UpdatePhraseAsync(phrase);
            _dirtyPhraseIds.Remove(phrase.Id);
        }

        foreach (var category in Categories)
        {
            await _dataStore.UpdateCategoryAsync(category);
            _dirtyCategoryIds.Remove(category.Id);
        }

        await _dataStore.DeleteCategoryAsync(sourceCategory.Id);
        await NormalizeAndPersistQuickTagsAsync();
        StatusMessage = movedPhrases.Count == 0
            ? $"已删除空分类，并合并到 {targetCategory.Name}。"
            : $"已将 {movedPhrases.Count} 条快捷短语从 {sourceCategory.Name} 合并到 {targetCategory.Name}。";
        return movedPhrases.Count;
    }

    public Task MoveCategoryAsync(ReplyCategory sourceCategory, ReplyCategory targetCategory)
    {
        if (sourceCategory.Id == targetCategory.Id)
        {
            return Task.CompletedTask;
        }

        var sourceIndex = Categories.IndexOf(sourceCategory);
        var targetIndex = Categories.IndexOf(targetCategory);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            return Task.CompletedTask;
        }

        Categories.Move(sourceIndex, targetIndex);
        NormalizeCategoryOrder();

        foreach (var category in Categories)
        {
            _dirtyCategoryIds.Add(category.Id);
        }

        SelectedCategory = sourceCategory;
        ScheduleSave();
        StatusMessage = $"已调整分类顺序：{sourceCategory.Name}";
        return Task.CompletedTask;
    }

    public async Task<ReplyActionResult> SendPhraseDirectAsync(
        ReplyPhrase phrase,
        string triggerSource = "phrase_direct_send",
        string? searchKeyword = null,
        ConversationRecommendationSnapshot? recognitionOverride = null)
    {
        SelectedPhrase = phrase;

        if (string.IsNullOrWhiteSpace(phrase.Content) && !phrase.HasImage)
        {
            var emptyResult = new ReplyActionResult(false, "当前短语内容为空，无法发送。");
            StatusMessage = emptyResult.Message;
            return emptyResult;
        }

        try
        {
            var targetWindowHandle = _windowTracker.GetLastExternalWindowHandle();
            var result = await _messageComposeService.ExecuteAsync(phrase.Content, Settings, ComposeActionMode.Send, ResolvePhraseImagePaths(phrase));
            LogPhraseUsage(phrase, triggerSource, searchKeyword, ComposeActionMode.Send, result, targetWindowHandle, recognitionOverride);
            RecordPhraseSend(phrase.Id);
            StatusMessage = $"{phrase.Title}：{result.Message}";
            return result;
        }
        catch (Exception ex)
        {
            var failedResult = new ReplyActionResult(false, $"发送失败：{ex.Message}");
            LogPhraseUsage(phrase, triggerSource, searchKeyword, ComposeActionMode.Send, failedResult, _windowTracker.GetLastExternalWindowHandle(), recognitionOverride);
            StatusMessage = failedResult.Message;
            return failedResult;
        }
    }

    public async Task<IReadOnlyList<LibraryFileItem>> AddFileItemsAsync(IEnumerable<string> filePaths)
    {
        var addedItems = await _fileLibraryCatalogService.AddItemsAsync(filePaths, FileItems.ToArray());
        if (addedItems.Count > 0)
        {
            await MutateCollectionsAsync(() =>
            {
                foreach (var fileItem in addedItems)
                {
                    FileItems.Add(fileItem);
                }

                return Task.CompletedTask;
            });
        }

        StatusMessage = addedItems.Count == 0
            ? "没有新增文件，可能是重复选择或文件已不存在。"
            : $"已加入 {addedItems.Count} 个文件到文件库。";
        return addedItems;
    }

    public async Task DeleteFileItemAsync(LibraryFileItem fileItem)
    {
        await _fileLibraryCatalogService.DeleteItemAsync(fileItem.Id);
        await MutateCollectionsAsync(() =>
        {
            FileItems.Remove(fileItem);
            return Task.CompletedTask;
        });

        StatusMessage = $"已移除文件：{fileItem.DisplayName}";
    }

    public async Task<ReplyActionResult> SendFileItemDirectAsync(
        LibraryFileItem fileItem,
        string triggerSource = "file_direct_send",
        string? searchKeyword = null)
    {
        if (!File.Exists(fileItem.FullPath))
        {
            var missingResult = new ReplyActionResult(false, $"文件不存在：{fileItem.FullPath}");
            StatusMessage = missingResult.Message;
            return missingResult;
        }

        try
        {
            var targetWindowHandle = _windowTracker.GetLastExternalWindowHandle();
            var result = await _messageComposeService.ExecuteFilesAsync([fileItem.FullPath], Settings, ComposeActionMode.Send);
            LogFileUsage(fileItem, triggerSource, searchKeyword, ComposeActionMode.Send, result, targetWindowHandle);
            RecordFileUse(fileItem.Id);
            StatusMessage = $"{fileItem.DisplayName}：{result.Message}";
            return result;
        }
        catch (Exception ex)
        {
            var failedResult = new ReplyActionResult(false, $"发送文件失败：{ex.Message}");
            StatusMessage = failedResult.Message;
            return failedResult;
        }
    }

    public async Task<ReplyActionResult> CopyFileItemDirectAsync(
        LibraryFileItem fileItem,
        string triggerSource = "file_direct_copy",
        string? searchKeyword = null)
    {
        if (!File.Exists(fileItem.FullPath))
        {
            var missingResult = new ReplyActionResult(false, $"文件不存在：{fileItem.FullPath}");
            StatusMessage = missingResult.Message;
            return missingResult;
        }

        try
        {
            var targetWindowHandle = _windowTracker.GetLastExternalWindowHandle();
            var result = await _messageComposeService.CopyFilesToClipboardAsync([fileItem.FullPath]);
            LogFileUsage(fileItem, triggerSource, searchKeyword, ComposeActionMode.Copy, result, targetWindowHandle);
            RecordFileUse(fileItem.Id);
            StatusMessage = $"{fileItem.DisplayName}：{result.Message}";
            return result;
        }
        catch (Exception ex)
        {
            var failedResult = new ReplyActionResult(false, $"复制文件失败：{ex.Message}");
            LogFileUsage(fileItem, triggerSource, searchKeyword, ComposeActionMode.Copy, failedResult, _windowTracker.GetLastExternalWindowHandle());
            StatusMessage = failedResult.Message;
            return failedResult;
        }
    }

    public async Task<ReplyActionResult> InputFileItemDirectAsync(
        LibraryFileItem fileItem,
        string triggerSource = "file_direct_input",
        string? searchKeyword = null)
    {
        if (!File.Exists(fileItem.FullPath))
        {
            var missingResult = new ReplyActionResult(false, $"文件不存在：{fileItem.FullPath}");
            StatusMessage = missingResult.Message;
            return missingResult;
        }

        try
        {
            var targetWindowHandle = _windowTracker.GetLastExternalWindowHandle();
            var result = await _messageComposeService.ExecuteFilesAsync([fileItem.FullPath], Settings, ComposeActionMode.Input);
            LogFileUsage(fileItem, triggerSource, searchKeyword, ComposeActionMode.Input, result, targetWindowHandle);
            RecordFileUse(fileItem.Id);
            StatusMessage = $"{fileItem.DisplayName}：{result.Message}";
            return result;
        }
        catch (Exception ex)
        {
            var failedResult = new ReplyActionResult(false, $"输入文件失败：{ex.Message}");
            LogFileUsage(fileItem, triggerSource, searchKeyword, ComposeActionMode.Input, failedResult, _windowTracker.GetLastExternalWindowHandle());
            StatusMessage = failedResult.Message;
            return failedResult;
        }
    }

    public Task<ReplyActionResult> ExportPhrasesToXlsAsync(string filePath)
    {
        try
        {
            _phraseExcelService.Export(filePath, Categories, ResolvePhraseImagePaths);
            var result = new ReplyActionResult(true, $"已导出 {Categories.Sum(category => category.Phrases.Count)} 条短语到：{filePath}");
            StatusMessage = result.Message;
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            var result = new ReplyActionResult(false, $"导出短语失败：{ex.Message}");
            StatusMessage = result.Message;
            return Task.FromResult(result);
        }
    }

    public Task<ConversationLogStats> GetConversationLogStatsAsync()
    {
        return _conversationLoggingService.GetStatsAsync();
    }

    public Task RunConversationLogMaintenanceAsync()
    {
        return _conversationLoggingService.RunMaintenanceAsync();
    }

    public async Task<int> ClearConversationLogCapturesAsync()
    {
        var removedCount = await _conversationLoggingService.DeleteAllCapturesAsync();
        StatusMessage = removedCount == 0
            ? "当前没有可清理的日志截图。"
            : $"已清空 {removedCount} 个日志截图。";
        return removedCount;
    }

    public Task<UsageDashboardStats> GetUsageDashboardStatsAsync()
    {
        return _dataStore.GetUsageDashboardStatsAsync();
    }

    public async Task<ReplyActionResult> ExportUsageDashboardReportAsync(string filePath)
    {
        try
        {
            var dashboardStats = await _dataStore.GetUsageDashboardStatsAsync(10, 10);
            var items = await _dataStore.GetPhraseUsageReportItemsAsync();
            _usageReportExcelService.ExportToXls(filePath, dashboardStats, items);

            var result = new ReplyActionResult(true, $"已导出使用统计报表到：{filePath}");
            StatusMessage = result.Message;
            return result;
        }
        catch (Exception ex)
        {
            var result = new ReplyActionResult(false, $"导出使用统计报表失败：{ex.Message}");
            StatusMessage = result.Message;
            return result;
        }
    }

    public async Task<ReplyActionResult> ExportPersonalConfigAsync(string filePath)
    {
        try
        {
            var bundle = new PersonalConfigBundle
            {
                AppVersion = typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? string.Empty,
                ExportedAt = DateTimeOffset.Now,
                Settings = AppSettingsSnapshotService.Clone(Settings),
                AppearanceThemes = (await _appearanceThemeCatalogService.LoadAsync()).ToList()
            };

            await _personalConfigService.ExportAsync(filePath, bundle);
            var result = new ReplyActionResult(true, $"已导出个人配置到：{filePath}");
            StatusMessage = result.Message;
            return result;
        }
        catch (Exception ex)
        {
            var result = new ReplyActionResult(false, $"导出个人配置失败：{ex.Message}");
            StatusMessage = result.Message;
            return result;
        }
    }

    public async Task<ReplyActionResult> ImportPersonalConfigAsync(string filePath)
    {
        var originalSettings = CreateSettingsSnapshot();
        var settingsApplied = false;
        var originalPhraseImageFolderPath = originalSettings.PhraseImageFolderPath;

        try
        {
            var bundle = await _personalConfigService.ImportAsync(filePath);
            var startupConfigurationResult = ApplyCommittedSettings(bundle.Settings, updateStatusMessage: false);
            if (!startupConfigurationResult.Success)
            {
                ApplyCommittedSettings(originalSettings);
                var failedResult = new ReplyActionResult(false, startupConfigurationResult.Message);
                StatusMessage = failedResult.Message;
                return failedResult;
            }

            settingsApplied = true;
            await _dataStore.SaveAppearanceThemesAsync(bundle.AppearanceThemes.Select(AppearanceThemeCatalogService.Clone).ToArray());
            await PersistSettingsAsync();
            await ApplyPhraseImageFolderChangeAsync(originalPhraseImageFolderPath);
            await SyncFileLibraryAsync();

            var result = new ReplyActionResult(true, "个人配置已导入并应用。");
            StatusMessage = result.Message;
            return result;
        }
        catch (Exception ex)
        {
            if (settingsApplied)
            {
                ApplyCommittedSettings(originalSettings);
            }

            var result = new ReplyActionResult(false, $"导入个人配置失败：{ex.Message}");
            StatusMessage = result.Message;
            return result;
        }
    }

    public Task<IReadOnlyList<ExceptionLogEntry>> GetRecentExceptionEntriesAsync(int maxCount = 12)
    {
        return _exceptionLogService.GetRecentEntriesAsync(maxCount);
    }

    public Task<int> ClearExceptionLogsAsync()
    {
        return _exceptionLogService.ClearAsync();
    }

    public Task<IReadOnlyList<AppearanceThemePreset>> LoadAppearanceThemesAsync()
    {
        return _appearanceThemeCatalogService.LoadAsync();
    }

    public async Task SaveAppearanceThemeAsync(AppearanceThemePreset theme)
    {
        await _appearanceThemeCatalogService.SaveAsync(theme);
    }

    public async Task DeleteAppearanceThemeAsync(string themeName)
    {
        await _appearanceThemeCatalogService.DeleteAsync(themeName);
    }

    public async Task<bool> ShouldShowFirstRunGuideAsync()
    {
        if (Settings.HasCompletedFirstRunGuide)
        {
            return false;
        }

        return await _dataStore.GetLaunchCountAsync() <= 1;
    }

    public async Task CompleteFirstRunGuideAsync(
        SearchFocusHotkeyMode searchHotkeyMode,
        SendHotkeyMode sendHotkeyMode,
        string? fileLibraryFolderPath,
        bool completed)
    {
        Settings.SearchFocusHotkeyMode = searchHotkeyMode;
        Settings.SendHotkeyMode = sendHotkeyMode;
        Settings.FileLibraryFolderPath = fileLibraryFolderPath?.Trim() ?? string.Empty;
        Settings.HasCompletedFirstRunGuide = completed;

        await PersistSettingsAsync();
        await SyncFileLibraryAsync(false);
    }

    public void ExpandCompactSidebar()
    {
        if (Settings.SidebarWorkMode != SidebarWorkMode.Compact)
        {
            return;
        }

        IsCompactSidebarExpanded = true;
        _windowDockService.SetCompactSidebarExpanded(true);
    }

    public async Task ToggleSidebarWorkModeAsync()
    {
        Settings.SidebarWorkMode = Settings.SidebarWorkMode == SidebarWorkMode.Standard
            ? SidebarWorkMode.Compact
            : SidebarWorkMode.Standard;

        if (Settings.SidebarWorkMode != SidebarWorkMode.Compact)
        {
            IsCompactSidebarExpanded = false;
            _windowDockService.SetCompactSidebarExpanded(false);
        }

        await PersistSettingsAsync();
    }

    public void BeginSettingsPreview()
    {
        _isPreviewingSettings = true;
    }

    public void EndSettingsPreview()
    {
        _isPreviewingSettings = false;
    }

    public async Task ApplyPhraseImageFolderChangeAsync(string? previousConfiguredFolderPath)
    {
        var copiedFileCount = await _phraseImageStore.MigrateLibraryAsync(previousConfiguredFolderPath, Settings.PhraseImageFolderPath);
        OnPropertyChanged(nameof(CurrentPhraseImageFolderPath));

        if (copiedFileCount > 0)
        {
            StatusMessage = $"已同步 {copiedFileCount} 个短语图片文件到新的图片目录。";
        }
    }

    public void BeginSidebarSettingsPreview(double left, double top, double width, double height)
    {
        _isSidebarSettingsPreviewActive = true;
        OnPropertyChanged(nameof(CompactLauncherVisibility));
        OnPropertyChanged(nameof(SidebarWorkspaceVisibility));
        _windowDockService.EnterPreview(left, top, width, height);
    }

    public void EndSidebarSettingsPreview()
    {
        _isSidebarSettingsPreviewActive = false;
        OnPropertyChanged(nameof(CompactLauncherVisibility));
        OnPropertyChanged(nameof(SidebarWorkspaceVisibility));
        _windowDockService.ExitPreview();
    }

    public AppSettings CreateSettingsSnapshot()
    {
        return AppSettingsSnapshotService.Clone(Settings);
    }

    public void ApplyPreviewSettings(AppSettings source)
    {
        var previousSuppression = _suppressStartupConfigurationNotifications;
        _suppressStartupConfigurationNotifications = true;

        try
        {
            Settings.SidebarWorkMode = source.SidebarWorkMode;
            Settings.SidebarWidth = source.SidebarWidth;
            Settings.SidebarHeightPercent = source.SidebarHeightPercent;
            Settings.CategoryTabsPerRow = source.CategoryTabsPerRow;
            Settings.CategoryFontSize = source.CategoryFontSize;
            Settings.PhraseFontSize = source.PhraseFontSize;
            Settings.PhraseRowSpacing = source.PhraseRowSpacing;
            Settings.PhraseHoverPreviewDelayMs = source.PhraseHoverPreviewDelayMs;
            Settings.CategoryColorHex = source.CategoryColorHex;
            Settings.CategoryOpacityPercent = source.CategoryOpacityPercent;
            Settings.TagColorHex = source.TagColorHex;
            Settings.TagOpacityPercent = source.TagOpacityPercent;
            Settings.PhraseColorHex = source.PhraseColorHex;
            Settings.PhraseOpacityPercent = source.PhraseOpacityPercent;
            Settings.SidebarColorHex = source.SidebarColorHex;
            Settings.SidebarOpacityPercent = source.SidebarOpacityPercent;
        }
        finally
        {
            _suppressStartupConfigurationNotifications = previousSuppression;
        }
    }

    public StartupConfigurationResult ApplyCommittedSettings(AppSettings source, bool updateStatusMessage = false)
    {
        var shouldApplyStartupConfiguration = HasPendingStartupConfigurationChange(source);
        ApplySettingsSnapshot(source, suppressStartupConfigurationNotifications: true);
        return _isInitialized && shouldApplyStartupConfiguration
            ? ApplyStartupConfiguration(updateStatusMessage)
            : new StartupConfigurationResult(true, string.Empty);
    }

    public async Task PersistSettingsAsync()
    {
        _saveTimer.Stop();

        try
        {
            _suppressPersistenceNotifications = true;
            await _dataStore.UpdateSettingsAsync(Settings);
            _settingsDirty = false;
            _savePending = false;
        }
        finally
        {
            _suppressPersistenceNotifications = false;
        }
    }

    private StartupConfigurationResult ApplyStartupConfiguration(bool updateStatusMessage)
    {
        var launchAtStartup = Settings.LaunchAtStartup;
        var runAsAdministrator = Settings.RunAsAdministrator;
        var result = _startupRegistrationService.ApplyConfiguration(launchAtStartup, runAsAdministrator);
        if (result.Success)
        {
            RememberAppliedStartupConfiguration(launchAtStartup, runAsAdministrator);
        }

        if (updateStatusMessage && !string.IsNullOrWhiteSpace(result.Message))
        {
            StatusMessage = result.Message;
        }

        return result;
    }

    private bool HasPendingStartupConfigurationChange(AppSettings source)
    {
        return !_isInitialized ||
               source.LaunchAtStartup != _appliedLaunchAtStartup ||
               source.RunAsAdministrator != _appliedRunAsAdministrator;
    }

    private void RememberAppliedStartupConfiguration(bool launchAtStartup, bool runAsAdministrator)
    {
        _appliedLaunchAtStartup = launchAtStartup;
        _appliedRunAsAdministrator = runAsAdministrator;
    }

    private void RestoreAppliedStartupConfiguration()
    {
        _suppressPersistenceNotifications = true;

        try
        {
            Settings.LaunchAtStartup = _appliedLaunchAtStartup;
            Settings.RunAsAdministrator = _appliedRunAsAdministrator;
        }
        finally
        {
            _suppressPersistenceNotifications = false;
        }
    }

    public async Task<ReplyActionResult> ImportPhrasesFromXlsAsync(string filePath, IProgress<PhraseImportProgress>? progress = null)
    {
        try
        {
            progress?.Report(new PhraseImportProgress(5, "正在读取导入文件..."));
            var rows = await Task.Run(() => _phraseExcelService.Import(filePath));
            if (rows.Count == 0)
            {
                return new ReplyActionResult(false, "导入文件中没有可用的快捷短语。");
            }

            progress?.Report(new PhraseImportProgress(12, $"正在准备导入... 0/{rows.Count}"));

            var now = DateTimeOffset.Now;
            var existingCategoryLookup = Categories.ToDictionary(category => category.Name, StringComparer.OrdinalIgnoreCase);
            var categoryOrderLookup = Categories.ToDictionary(category => category.Id, category => category.Phrases.Count);
            var categoriesToInsert = new List<ReplyCategory>();
            var phrasesToInsert = new List<ReplyPhrase>();
            var maxSortOrder = Categories.Count == 0 ? 0 : Categories.Max(category => category.SortOrder);

            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                if (!existingCategoryLookup.TryGetValue(row.CategoryName, out var category))
                {
                    category = new ReplyCategory
                    {
                        Id = Guid.NewGuid(),
                        Name = NormalizeCategoryName(row.CategoryName, maxSortOrder + 1),
                        SortOrder = ++maxSortOrder,
                        CreatedAt = now,
                        UpdatedAt = now
                    };

                    existingCategoryLookup[category.Name] = category;
                    categoryOrderLookup[category.Id] = 0;
                    categoriesToInsert.Add(category);
                }

                var phrase = new ReplyPhrase
                {
                    Id = Guid.NewGuid(),
                    CategoryId = category.Id,
                    CategoryName = category.Name,
                    Title = NormalizePhraseTitle(row.Remark),
                    Tags = NormalizePhraseTags(row.Remark),
                    Content = row.Content.Trim(),
                    SortOrder = ++categoryOrderLookup[category.Id],
                    CreatedAt = now,
                    UpdatedAt = now
                };

                var savedImagePaths = await _phraseImageStore.SavePhraseImagesAsync(phrase.Id, row.ImagePaths);
                if (savedImagePaths.Count > 0)
                {
                    phrase.SetImageRelativePaths(savedImagePaths);
                }

                phrasesToInsert.Add(phrase);
                var percent = 12 + (int)Math.Round(((index + 1d) / rows.Count) * 28d);
                progress?.Report(new PhraseImportProgress(percent, $"正在准备导入... {index + 1}/{rows.Count}"));
            }

            var storeProgress = progress is null
                ? null
                : new Progress<int>(percent =>
                {
                    var normalized = 40 + (int)Math.Round(percent * 0.6d);
                    progress.Report(new PhraseImportProgress(normalized, $"正在导入... {normalized}%"));
                });

            await _dataStore.BulkImportPhrasesAsync(categoriesToInsert, phrasesToInsert, storeProgress);
            await ReloadDataFromStoreAsync(categoriesToInsert.LastOrDefault()?.Id ?? SelectedCategory?.Id);

            progress?.Report(new PhraseImportProgress(100, $"导入成功，一共 {phrasesToInsert.Count} 条快捷短语"));
            return new ReplyActionResult(true, $"导入成功，一共 {phrasesToInsert.Count} 条快捷短语");
        }
        catch (Exception ex)
        {
            return new ReplyActionResult(false, $"导入失败：{ex.Message}");
        }
    }

    public async Task ClearFileLibraryConfigurationAsync()
    {
        await _dataStore.ClearFileLibraryConfigurationAsync();
        await ReloadDataFromStoreAsync(SelectedCategory?.Id);
        StatusMessage = "已清空文件库配置。";
    }

    public async Task ResetPersonalizationSettingsAsync()
    {
        var defaults = new AppSettings
        {
            FileLibraryFolderPath = Settings.FileLibraryFolderPath,
            PhraseImageFolderPath = Settings.PhraseImageFolderPath
        };
        var originalSettings = CreateSettingsSnapshot();

        var startupConfigurationResult = ApplyCommittedSettings(defaults, updateStatusMessage: false);
        if (!startupConfigurationResult.Success)
        {
            ApplyCommittedSettings(originalSettings);
            StatusMessage = startupConfigurationResult.Message;
            return;
        }

        try
        {
            await PersistSettingsAsync();
            _windowDockService.ApplySettings(Settings);
            _windowDockService.RefreshNow();
            _conversationLoggingService.ApplySettings(Settings);
            _conversationLoggingService.RequestMaintenance();
            StatusMessage = "已恢复默认个性化配置。";
        }
        catch
        {
            ApplyCommittedSettings(originalSettings);
            throw;
        }
    }

    public async Task ResetPhraseDataToDefaultsAsync()
    {
        await _dataStore.ResetPhraseDataToDefaultsAsync();
        _phraseImageStore.ClearAll();
        await ReloadDataFromStoreAsync(null);
        StatusMessage = "已恢复默认示例短语数据。";
    }






    public async Task SendQuickTagDirectAsync(QuickTag quickTag)
    {
        await SendQuickTagAsync(quickTag);
    }

    private async Task CopySelectedPhraseAsync()
    {
        if (!TryGetSelectedPhraseContent(out var content))
        {
            return;
        }

        try
        {
            var targetWindowHandle = _windowTracker.GetLastExternalWindowHandle();
            var result = await _messageComposeService.CopyToClipboardAsync(content, ResolvePhraseImagePaths(SelectedPhrase));
            LogSelectedPhraseUsage("selected_phrase_copy", null, ComposeActionMode.Copy, result, targetWindowHandle);
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            StatusMessage = $"复制失败：{ex.Message}";
        }
    }

    private async Task AddCategoryAsync()
    {
        var now = DateTimeOffset.Now;
        var category = new ReplyCategory
        {
            Id = Guid.NewGuid(),
            Name = $"新分类 {Categories.Count + 1}",
            SortOrder = Categories.Count + 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _dataStore.InsertCategoryAsync(category);

        await MutateCollectionsAsync(() =>
        {
            Categories.Add(category);
            SelectedCategory = category;
            return Task.CompletedTask;
        });

        StatusMessage = $"已新增分类：{category.Name}";
    }

    private async Task DeleteCategoryAsync()
    {
        if (SelectedCategory is null)
        {
            StatusMessage = "请选择要删除的分类。";
            return;
        }

        var current = SelectedCategory;
        if (current.Phrases.Count > 0 || await _dataStore.CategoryHasPhrasesAsync(current.Id))
        {
            StatusMessage = "该分类下仍有短语，暂不允许直接删除。请先删除或转移短语。";
            return;
        }

        await _dataStore.DeleteCategoryAsync(current.Id);
        _dirtyCategoryIds.Remove(current.Id);

        await MutateCollectionsAsync(() =>
        {
            Categories.Remove(current);
            SelectedCategory = Categories.FirstOrDefault();
            return Task.CompletedTask;
        });

        StatusMessage = $"已删除分类：{current.Name}";
    }

    private async Task AddPhraseAsync()
    {
        if (SelectedCategory is null)
        {
            await AddCategoryAsync();
        }

        if (SelectedCategory is null)
        {
            StatusMessage = "请先选择分类。";
            return;
        }

        var now = DateTimeOffset.Now;
        var phrase = new ReplyPhrase
        {
            Id = Guid.NewGuid(),
            CategoryId = SelectedCategory.Id,
            CategoryName = SelectedCategory.Name,
            Title = $"新短语 {SelectedCategory.Phrases.Count + 1}",
            Tags = "常用",
            Content = "请在这里填写要快速发送的回复内容。",
            SortOrder = SelectedCategory.Phrases.Count + 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _dataStore.InsertPhraseAsync(phrase);

        await MutateCollectionsAsync(() =>
        {
            SelectedCategory.Phrases.Add(phrase);
            RefreshFilteredPhrases();
            SelectedPhrase = phrase;
            return Task.CompletedTask;
        });

        StatusMessage = $"已新增短语：{phrase.Title}";
    }

    private async Task DeletePhraseAsync()
    {
        if (SelectedPhrase is null)
        {
            StatusMessage = "请选择要删除的短语。";
            return;
        }

        var removedPhrase = SelectedPhrase;
        DeletePhraseImages(removedPhrase);
        await _dataStore.DeletePhraseAsync(removedPhrase.Id);
        _dirtyPhraseIds.Remove(removedPhrase.Id);

        await MutateCollectionsAsync(() =>
        {
            var ownerCategory = Categories.FirstOrDefault(category => category.Id == removedPhrase.CategoryId);
            ownerCategory?.Phrases.Remove(removedPhrase);
            RemoveQuickTagsForPhrase(removedPhrase.Id);
            RefreshFilteredPhrases();
            return Task.CompletedTask;
        });

        await NormalizeAndPersistQuickTagsAsync();
        StatusMessage = $"已删除短语：{removedPhrase.Title}";
    }

    private async Task AddQuickTagAsync()
    {
        if (SelectedPhrase is null)
        {
            StatusMessage = "请先选择一条短语，再加入快捷标签。";
            return;
        }

        if (QuickTags.Any(tag => tag.PhraseId == SelectedPhrase.Id))
        {
            StatusMessage = "当前短语已经在快捷标签中。";
            return;
        }

        var quickTag = new QuickTag
        {
            Id = Guid.NewGuid(),
            PhraseId = SelectedPhrase.Id,
            PhraseTitle = SelectedPhrase.Title,
            PhraseContent = SelectedPhrase.Content,
            PhraseCategoryName = SelectedPhrase.CategoryName,
            SortOrder = QuickTags.Count + 1,
            CreatedAt = DateTimeOffset.Now
        };

        await _dataStore.InsertQuickTagAsync(quickTag);

        await MutateCollectionsAsync(() =>
        {
            QuickTags.Add(quickTag);
            ReorderQuickTagsInMemory();
            return Task.CompletedTask;
        });

        StatusMessage = $"已加入快捷标签：{quickTag.PhraseTitle}";
    }

    private async Task<ReplyCategory?> EnsureSelectedCategoryAsync()
    {
        if (SelectedCategory is not null)
        {
            return SelectedCategory;
        }

        await AddCategoryAsync();
        return SelectedCategory;
    }

    private async Task<ReplyPhrase> InsertPhraseCoreAsync(
        ReplyCategory category,
        ReplyPhrase? anchorPhrase,
        string title,
        string tags,
        string content,
        IReadOnlyCollection<string>? imageSourcePaths = null)
    {
        var now = DateTimeOffset.Now;
        var insertIndex = ResolvePhraseInsertIndex(category, anchorPhrase);
        var phrase = new ReplyPhrase
        {
            Id = Guid.NewGuid(),
            CategoryId = category.Id,
            CategoryName = category.Name,
            Title = NormalizePhraseTitle(title),
            Tags = NormalizePhraseTags(tags),
            Content = content.Trim(),
            SortOrder = insertIndex + 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (imageSourcePaths is not null && imageSourcePaths.Count > 0)
        {
            var savedImagePaths = await _phraseImageStore.SavePhraseImagesAsync(phrase.Id, imageSourcePaths);
            if (savedImagePaths.Count > 0)
            {
                DeleteTransientImagesIfNeeded(imageSourcePaths);
                phrase.SetImageRelativePaths(savedImagePaths);
            }
        }

        SearchOverlayDiagnostics.Log(
            "phrase_images",
            "create_phrase_completed",
            new Dictionary<string, object?>
            {
                ["phraseId"] = phrase.Id.ToString("N"),
                ["title"] = phrase.Title,
                ["storedImageRelativePath"] = phrase.ImageRelativePath,
                ["storedImageCount"] = phrase.ImageCount
            });

        await _dataStore.InsertPhraseAsync(phrase);
        IReadOnlyList<ReplyPhrase> phrasesNeedingOrderPersistence = [];

        await MutateCollectionsAsync(() =>
        {
            phrasesNeedingOrderPersistence = ApplyInsertedPhraseOrderChanges(category, phrase, insertIndex);
            SelectedCategory = category;
            SelectedPhrase = phrase;
            RefreshFilteredPhrases();
            return Task.CompletedTask;
        });

        await PersistPhraseOrderAsync(phrasesNeedingOrderPersistence);
        StatusMessage = anchorPhrase is null
            ? $"已新增短语：{phrase.Title}"
            : $"已插入短语：{phrase.Title}";

        return phrase;
    }

    private async Task SendQuickTagAsync(QuickTag? quickTag)
    {
        if (!TryGetQuickTagContent(quickTag, out var content))
        {
            return;
        }

        try
        {
            var result = await _messageComposeService.ExecuteAsync(content, Settings, ComposeActionMode.Send);
            StatusMessage = $"快捷标签“{quickTag!.PhraseTitle}”：{result.Message}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"快捷标签发送失败：{ex.Message}";
        }
    }

    private async Task DeleteQuickTagAsync(QuickTag? quickTag)
    {
        if (quickTag is null)
        {
            StatusMessage = "请选择要删除的快捷标签。";
            return;
        }

        await _dataStore.DeleteQuickTagAsync(quickTag.Id);

        await MutateCollectionsAsync(() =>
        {
            QuickTags.Remove(quickTag);
            return Task.CompletedTask;
        });

        await NormalizeAndPersistQuickTagsAsync();
        StatusMessage = $"已删除快捷标签：{quickTag.PhraseTitle}";
    }

    private async Task MoveQuickTagAsync(QuickTag? quickTag, int direction)
    {
        if (quickTag is null)
        {
            StatusMessage = "请选择要排序的快捷标签。";
            return;
        }

        var orderedTags = QuickTags.OrderBy(tag => tag.SortOrder).ThenBy(tag => tag.CreatedAt).ToList();
        var currentIndex = orderedTags.FindIndex(tag => tag.Id == quickTag.Id);
        if (currentIndex < 0)
        {
            return;
        }

        var targetIndex = currentIndex + direction;
        if (targetIndex < 0 || targetIndex >= orderedTags.Count)
        {
            return;
        }

        (orderedTags[currentIndex], orderedTags[targetIndex]) = (orderedTags[targetIndex], orderedTags[currentIndex]);

        for (var index = 0; index < orderedTags.Count; index++)
        {
            orderedTags[index].SortOrder = index + 1;
        }

        await MutateCollectionsAsync(() =>
        {
            ReplaceQuickTags(orderedTags);
            return Task.CompletedTask;
        });

        await _dataStore.UpdateQuickTagOrderAsync(orderedTags);
        StatusMessage = $"已调整快捷标签顺序：{quickTag.PhraseTitle}";
    }

    private bool TryGetSelectedPhraseContent(out string content)
    {
        content = string.Empty;

        if (SelectedPhrase is null)
        {
            StatusMessage = "请先选择一条短语。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SelectedPhrase.Content) && !SelectedPhrase.HasImage)
        {
            StatusMessage = "当前短语内容为空。";
            return false;
        }

        content = SelectedPhrase.Content;
        return true;
    }

    private bool TryGetQuickTagContent(QuickTag? quickTag, out string content)
    {
        content = string.Empty;

        if (quickTag is null)
        {
            StatusMessage = "未找到快捷标签。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(quickTag.PhraseContent))
        {
            StatusMessage = "当前快捷标签对应的短语内容为空。";
            return false;
        }

        content = quickTag.PhraseContent;
        return true;
    }

    private bool CanUseSelectedPhrase()
    {
        return SelectedPhrase is not null && !string.IsNullOrWhiteSpace(SelectedPhrase.Content);
    }

    private bool CanAddQuickTag()
    {
        return SelectedPhrase is not null &&
               !string.IsNullOrWhiteSpace(SelectedPhrase.Content) &&
               QuickTags.All(tag => tag.PhraseId != SelectedPhrase.Id);
    }

    private bool CanMoveQuickTag(QuickTag? quickTag, int direction)
    {
        if (quickTag is null)
        {
            return false;
        }

        var orderedTags = QuickTags.OrderBy(tag => tag.SortOrder).ThenBy(tag => tag.CreatedAt).ToList();
        var currentIndex = orderedTags.FindIndex(tag => tag.Id == quickTag.Id);
        if (currentIndex < 0)
        {
            return false;
        }

        var targetIndex = currentIndex + direction;
        return targetIndex >= 0 && targetIndex < orderedTags.Count;
    }

    private void RefreshFilteredPhrases()
    {
        var previousSelection = SelectedPhrase;
        var keyword = SearchText.Trim();

        IEnumerable<ReplyPhrase> source = IsSearchActive
            ? Categories.SelectMany(category => category.Phrases)
            : SelectedCategory?.Phrases ?? [];

        var resultsQuery = source.Where(phrase =>
            string.IsNullOrWhiteSpace(keyword) ||
            PhraseMatchesKeyword(phrase, keyword));

        var results = IsSearchActive
            ? resultsQuery
                .OrderBy(phrase => !phrase.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ThenBy(phrase => !phrase.Tags.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(phrase => GetPhraseSendCount(phrase.Id))
                .ThenByDescending(phrase => GetPhraseLastSentAt(phrase.Id) ?? DateTimeOffset.MinValue)
                .ThenBy(phrase => phrase.CategoryName)
                .ThenBy(phrase => phrase.SortOrder)
                .ThenBy(phrase => phrase.CreatedAt)
                .ToList()
            : resultsQuery
                .OrderBy(phrase => phrase.SortOrder)
                .ThenBy(phrase => phrase.CreatedAt)
                .ToList();

        FilteredPhrases.Clear();
        foreach (var phrase in results)
        {
            FilteredPhrases.Add(phrase);
        }

        SelectedPhrase = previousSelection is not null && results.Contains(previousSelection)
            ? previousSelection
            : results.FirstOrDefault();
    }

    private static string NormalizePhraseTitle(string title)
    {
        var value = title.Trim();
        return string.IsNullOrWhiteSpace(value) ? "未命名短语" : value;
    }

    private async Task<ReplyCategory?> ResolvePhraseSaveCategoryAsync()
    {
        if (SelectedCategory is not null)
        {
            return SelectedCategory;
        }

        if (Categories.FirstOrDefault() is { } firstCategory)
        {
            SelectedCategory = firstCategory;
            return firstCategory;
        }

        await AddCategoryAsync();
        return SelectedCategory;
    }

    private ReplyPhrase? FindExactPhraseByContent(string content)
    {
        var normalizedContent = NormalizePlainTextContent(content);
        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            return null;
        }

        return Categories
            .SelectMany(category => category.Phrases)
            .FirstOrDefault(phrase => string.Equals(
                NormalizePlainTextContent(phrase.Content),
                normalizedContent,
                StringComparison.Ordinal));
    }

    private void SelectPhrase(ReplyPhrase phrase)
    {
        var category = Categories.FirstOrDefault(item => item.Id == phrase.CategoryId);
        if (category is not null)
        {
            SelectedCategory = category;
        }

        SelectedPhrase = phrase;
        RefreshFilteredPhrases();
    }

    private async Task EvaluateFrequentPlainTextSuggestionAsync(string content)
    {
        var normalizedContent = NormalizePlainTextContent(content);
        if (string.IsNullOrWhiteSpace(normalizedContent) || FindExactPhraseByContent(normalizedContent) is not null)
        {
            return;
        }

        var suggestionKey = BuildPlainTextSuggestionKey(normalizedContent);
        if (_dismissedPlainTextSuggestionKeys.Contains(suggestionKey) || _pendingPlainTextSuggestionKeys.Contains(suggestionKey))
        {
            return;
        }

        var suggestion = await _conversationLoggingService.GetFrequentPlainTextSuggestionAsync(normalizedContent, 1);
        if (suggestion is null)
        {
            return;
        }

        var normalizedSuggestion = new FrequentReplyPhraseSuggestion
        {
            PayloadText = normalizedContent,
            UseCount = suggestion.UseCount,
            CustomerText = suggestion.CustomerText,
            SuggestedTitle = BuildPlainTextPhraseTitle(normalizedContent),
            SuggestedTags = string.Empty
        };

        _pendingPlainTextSuggestionKeys.Add(suggestionKey);
        await _conversationLoggingService.LogOperationEventAsync(
            "frequent_reply_suggestion",
            "shown",
            success: true,
            skipped: false,
            durationMs: 0,
            message: "已弹出高频原文保存建议。",
            details: new
            {
                payloadText = normalizedSuggestion.PayloadText,
                useCount = normalizedSuggestion.UseCount,
                suggestedTitle = normalizedSuggestion.SuggestedTitle,
                hasCustomerContext = !string.IsNullOrWhiteSpace(normalizedSuggestion.CustomerText)
            });
        FrequentReplyPhraseSuggested?.Invoke(this, normalizedSuggestion);
    }

    private void MarkPlainTextSuggestionHandled(string content, bool dismissed)
    {
        var suggestionKey = BuildPlainTextSuggestionKey(content);
        if (string.IsNullOrWhiteSpace(suggestionKey))
        {
            return;
        }

        _pendingPlainTextSuggestionKeys.Remove(suggestionKey);
        if (dismissed)
        {
            _dismissedPlainTextSuggestionKeys.Add(suggestionKey);
        }
    }

    private static string BuildPlainTextSuggestionKey(string content)
    {
        return NormalizePlainTextContent(content);
    }

    private static string NormalizePlainTextContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();
    }

    private static string BuildPlainTextPhraseTitle(string content)
    {
        var firstLine = NormalizePlainTextContent(content)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return "未命名短语";
        }

        return NormalizePhraseTitle(firstLine.Length <= 18 ? firstLine : $"{firstLine[..18]}...");
    }

    private static string NormalizePhraseTags(string tags)
    {
        return tags.Trim();
    }

    private static string NormalizeCategoryName(string name, int fallbackIndex)
    {
        var value = name.Trim();
        return string.IsNullOrWhiteSpace(value) ? $"鍒嗙被 {fallbackIndex}" : value;
    }

    private void NormalizeCategoryOrder()
    {
        for (var index = 0; index < Categories.Count; index++)
        {
            Categories[index].SortOrder = index + 1;
        }
    }

    private static void NormalizePhraseOrder(ReplyCategory category)
    {
        for (var index = 0; index < category.Phrases.Count; index++)
        {
            category.Phrases[index].SortOrder = index + 1;
        }
    }

    internal static int ResolvePhraseInsertIndex(ReplyCategory category, ReplyPhrase? anchorPhrase)
    {
        if (anchorPhrase is null)
        {
            return category.Phrases.Count;
        }

        var currentIndex = category.Phrases.IndexOf(anchorPhrase);
        return currentIndex >= 0
            ? currentIndex + 1
            : category.Phrases.Count;
    }

    internal static IReadOnlyList<ReplyPhrase> ApplyInsertedPhraseOrderChanges(
        ReplyCategory category,
        ReplyPhrase insertedPhrase,
        int insertIndex)
    {
        category.Phrases.Insert(insertIndex, insertedPhrase);

        var phrasesNeedingPersistence = new List<ReplyPhrase>();
        for (var index = insertIndex; index < category.Phrases.Count; index++)
        {
            var phrase = category.Phrases[index];
            var expectedSortOrder = index + 1;
            if (phrase.SortOrder == expectedSortOrder)
            {
                continue;
            }

            phrase.SortOrder = expectedSortOrder;
            if (phrase.Id != insertedPhrase.Id)
            {
                phrasesNeedingPersistence.Add(phrase);
            }
        }

        return phrasesNeedingPersistence;
    }

    private async Task PersistPhraseOrderAsync(IReadOnlyCollection<ReplyPhrase> phrases)
    {
        if (phrases.Count == 0)
        {
            return;
        }

        await _dataStore.UpdatePhraseOrderAsync(phrases);
        foreach (var phrase in phrases)
        {
            _dirtyPhraseIds.Remove(phrase.Id);
        }
    }

    private void NotifyPhraseAppearanceChanged()
    {
        OnPropertyChanged(nameof(IsCompactPhraseList));
        OnPropertyChanged(nameof(PhraseContentVisibility));
        OnPropertyChanged(nameof(PhraseRowMargin));
        OnPropertyChanged(nameof(PhraseCardPadding));
        OnPropertyChanged(nameof(PhraseArrowMargin));
        OnPropertyChanged(nameof(PhraseArrowButtonSize));
        OnPropertyChanged(nameof(PhraseArrowFontSize));
        OnPropertyChanged(nameof(PhraseTitleFontSize));
        OnPropertyChanged(nameof(PhraseTagsMargin));
        OnPropertyChanged(nameof(PhraseContentMargin));
        OnPropertyChanged(nameof(PhraseCategoryPadding));
        OnPropertyChanged(nameof(PhraseCategoryFontSize));
        OnPropertyChanged(nameof(CategoryChipFontSize));
    }

    private void WireData()
    {
        _data.Categories.CollectionChanged += OnCategoriesChanged;
        _data.Settings.PropertyChanged += OnSettingsChanged;

        foreach (var category in _data.Categories)
        {
            AttachCategory(category);
        }
    }

    private void UnwireData()
    {
        _data.Categories.CollectionChanged -= OnCategoriesChanged;
        _data.Settings.PropertyChanged -= OnSettingsChanged;

        foreach (var category in _data.Categories)
        {
            DetachCategory(category);
        }
    }

    private async Task ReloadDataFromStoreAsync(Guid? preferredCategoryId)
    {
        UnwireData();
        _data = await _dataStore.LoadAsync();
        LoadPhraseUsageStats(await _dataStore.LoadPhraseUsageStatsAsync());
        LoadFileUsageStats(await _dataStore.LoadFileUsageStatsAsync());
        WireData();
        SyncPhraseCategoryNames();
        SyncQuickTagsFromPhrases();

        OnPropertyChanged(nameof(Categories));
        OnPropertyChanged(nameof(QuickTags));
        OnPropertyChanged(nameof(FileItems));
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(StoragePath));
        OnPropertyChanged(nameof(PhraseListTitle));

        SelectedCategory = preferredCategoryId is Guid categoryId
            ? Categories.FirstOrDefault(item => item.Id == categoryId) ?? Categories.FirstOrDefault()
            : Categories.FirstOrDefault();

        RefreshFilteredPhrases();
        UpdateCommandStates();
    }

    private void AttachCategory(ReplyCategory category)
    {
        category.PropertyChanged += OnCategoryChanged;
        category.Phrases.CollectionChanged += OnPhrasesChanged;

        foreach (var phrase in category.Phrases)
        {
            AttachPhrase(phrase);
            phrase.CategoryName = category.Name;
        }
    }

    private void DetachCategory(ReplyCategory category)
    {
        category.PropertyChanged -= OnCategoryChanged;
        category.Phrases.CollectionChanged -= OnPhrasesChanged;

        foreach (var phrase in category.Phrases)
        {
            DetachPhrase(phrase);
        }
    }

    private void AttachPhrase(ReplyPhrase phrase)
    {
        phrase.PropertyChanged += OnPhraseChanged;
    }

    private void DetachPhrase(ReplyPhrase phrase)
    {
        phrase.PropertyChanged -= OnPhraseChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isInitialized || _suppressPersistenceNotifications)
        {
            return;
        }

        if (e.PropertyName is nameof(AppSettings.PhraseListDensity) or
            nameof(AppSettings.CategoryTabsPerRow) or
            nameof(AppSettings.PhraseFontSize) or
            nameof(AppSettings.PhraseRowSpacing) or
            nameof(AppSettings.CategoryFontSize))
        {
            NotifyPhraseAppearanceChanged();
        }

        if (e.PropertyName is nameof(AppSettings.LogScreenshotRetentionDays) or nameof(AppSettings.LogScreenshotMaxSizeMb))
        {
            _conversationLoggingService.ApplySettings(Settings);
            _conversationLoggingService.RequestMaintenance();
        }

        if (e.PropertyName == nameof(AppSettings.PhraseImageFolderPath))
        {
            _phraseImageStore.UpdateRootPath(Settings.PhraseImageFolderPath);
            OnPropertyChanged(nameof(CurrentPhraseImageFolderPath));
        }

        if (e.PropertyName is nameof(AppSettings.SidebarWidth) or nameof(AppSettings.SidebarHeightPercent) or nameof(AppSettings.SidebarWorkMode))
        {
            _windowDockService.ApplySettings(Settings);
            _windowDockService.RefreshNow();
        }

        if (e.PropertyName is nameof(AppSettings.LaunchAtStartup) or nameof(AppSettings.RunAsAdministrator))
        {
            if (!_isPreviewingSettings && !_suppressStartupConfigurationNotifications)
            {
                var startupConfigurationResult = ApplyStartupConfiguration(updateStatusMessage: true);
                if (!startupConfigurationResult.Success)
                {
                    RestoreAppliedStartupConfiguration();
                    return;
                }
            }
        }

        if (e.PropertyName == nameof(AppSettings.SidebarWorkMode))
        {
            if (Settings.SidebarWorkMode != SidebarWorkMode.Compact)
            {
                IsCompactSidebarExpanded = false;
            }

            OnPropertyChanged(nameof(CompactLauncherVisibility));
            OnPropertyChanged(nameof(SidebarWorkspaceVisibility));
        }

        if (_isPreviewingSettings)
        {
            return;
        }

        _settingsDirty = true;
        ScheduleSave();
    }

    private void OnCompactSidebarExpandedChanged(bool isExpanded)
    {
        IsCompactSidebarExpanded = isExpanded;
    }

    private void OnCategoryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ReplyCategory category || !_isInitialized || _suppressPersistenceNotifications)
        {
            return;
        }

        if (e.PropertyName is nameof(ReplyCategory.Name))
        {
            foreach (var phrase in category.Phrases)
            {
                phrase.CategoryName = category.Name;
                SyncQuickTagFromPhrase(phrase);
            }

            _dirtyCategoryIds.Add(category.Id);
            RefreshFilteredPhrases();
            OnPropertyChanged(nameof(PhraseListTitle));
            ScheduleSave();
        }
    }

    private void OnPhraseChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ReplyPhrase phrase)
        {
            return;
        }

        if (e.PropertyName == nameof(ReplyPhrase.IsMultiSelected))
        {
            return;
        }

        SyncQuickTagFromPhrase(phrase);
        RefreshFilteredPhrases();

        if (!_isInitialized || _suppressPersistenceNotifications)
        {
            return;
        }

        if (e.PropertyName is nameof(ReplyPhrase.Title) or nameof(ReplyPhrase.Tags) or nameof(ReplyPhrase.Content))
        {
            _dirtyPhraseIds.Add(phrase.Id);
            ScheduleSave();
        }
    }

    private void OnCategoriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ReplyCategory category in e.OldItems)
            {
                DetachCategory(category);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ReplyCategory category in e.NewItems)
            {
                AttachCategory(category);
            }
        }

        if (SelectedCategory is not null && !Categories.Contains(SelectedCategory))
        {
            SelectedCategory = Categories.FirstOrDefault();
        }

        RefreshFilteredPhrases();
        OnPropertyChanged(nameof(PhraseListTitle));
    }

    private void OnPhrasesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ReplyPhrase phrase in e.OldItems)
            {
                DetachPhrase(phrase);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ReplyPhrase phrase in e.NewItems)
            {
                AttachPhrase(phrase);
                SyncQuickTagFromPhrase(phrase);
            }
        }

        RefreshFilteredPhrases();
    }

    private void ScheduleSave()
    {
        _savePending = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task FlushPendingSaveAsync()
    {
        _saveTimer.Stop();
        if (!_savePending)
        {
            return;
        }

        await SaveNowAsync(false);
    }

    private async Task SaveNowAsync(bool updateStatus)
    {
        _saveTimer.Stop();

        try
        {
            _suppressPersistenceNotifications = true;
            var hadDirtySettings = _settingsDirty;

            var categoriesToSave = Categories
                .Where(category => _dirtyCategoryIds.Contains(category.Id))
                .ToList();

            var phrasesToSave = Categories
                .SelectMany(category => category.Phrases)
                .Where(phrase => _dirtyPhraseIds.Contains(phrase.Id))
                .ToList();

            foreach (var category in categoriesToSave)
            {
                await _dataStore.UpdateCategoryAsync(category);
            }

            foreach (var phrase in phrasesToSave)
            {
                await _dataStore.UpdatePhraseAsync(phrase);
            }

            if (_settingsDirty)
            {
                await _dataStore.UpdateSettingsAsync(Settings);
            }

            _dirtyCategoryIds.Clear();
            _dirtyPhraseIds.Clear();
            _settingsDirty = false;
            _savePending = false;

            if (updateStatus)
            {
                StatusMessage = categoriesToSave.Count == 0 && phrasesToSave.Count == 0 && !hadDirtySettings
                    ? "本地数据和配置已经是最新状态。"
                    : "数据与配置已同步到本地数据库。";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"本地保存失败：{ex.Message}";
        }
        finally
        {
            _suppressPersistenceNotifications = false;
        }
    }

    private async Task MutateCollectionsAsync(Func<Task> action)
    {
        _suppressPersistenceNotifications = true;

        try
        {
            await action();
        }
        finally
        {
            _suppressPersistenceNotifications = false;
            UpdateCommandStates();
        }
    }

    private async Task NormalizeAndPersistQuickTagsAsync()
    {
        var orderedTags = QuickTags.OrderBy(tag => tag.SortOrder).ThenBy(tag => tag.CreatedAt).ToList();

        for (var index = 0; index < orderedTags.Count; index++)
        {
            orderedTags[index].SortOrder = index + 1;
        }

        await MutateCollectionsAsync(() =>
        {
            ReplaceQuickTags(orderedTags);
            return Task.CompletedTask;
        });

        await _dataStore.UpdateQuickTagOrderAsync(orderedTags);
    }

    private void SyncPhraseCategoryNames()
    {
        foreach (var category in Categories)
        {
            foreach (var phrase in category.Phrases)
            {
                phrase.CategoryName = category.Name;
            }
        }
    }

    private void SyncQuickTagsFromPhrases()
    {
        foreach (var phrase in Categories.SelectMany(category => category.Phrases))
        {
            SyncQuickTagFromPhrase(phrase);
        }

        RemoveOrphanQuickTags();
        ReorderQuickTagsInMemory();
    }

    private void SyncQuickTagFromPhrase(ReplyPhrase phrase)
    {
        var matchedTags = QuickTags.Where(tag => tag.PhraseId == phrase.Id).ToList();
        foreach (var quickTag in matchedTags)
        {
            quickTag.PhraseTitle = phrase.Title;
            quickTag.PhraseContent = phrase.Content;
            quickTag.PhraseCategoryName = phrase.CategoryName;
        }
    }

    private void RemoveQuickTagsForPhrase(Guid phraseId)
    {
        var matchedTags = QuickTags.Where(tag => tag.PhraseId == phraseId).ToList();
        foreach (var quickTag in matchedTags)
        {
            QuickTags.Remove(quickTag);
        }
    }

    private void RemoveOrphanQuickTags()
    {
        var validPhraseIds = Categories
            .SelectMany(category => category.Phrases)
            .Select(phrase => phrase.Id)
            .ToHashSet();

        var orphans = QuickTags.Where(tag => !validPhraseIds.Contains(tag.PhraseId)).ToList();
        foreach (var quickTag in orphans)
        {
            QuickTags.Remove(quickTag);
        }
    }

    private void ReorderQuickTagsInMemory()
    {
        ReplaceQuickTags(QuickTags.OrderBy(tag => tag.SortOrder).ThenBy(tag => tag.CreatedAt).ToList());
    }

    private void ReplaceQuickTags(IReadOnlyCollection<QuickTag> orderedTags)
    {
        QuickTags.Clear();
        foreach (var quickTag in orderedTags)
        {
            QuickTags.Add(quickTag);
        }
    }

    private void LoadPhraseUsageStats(IReadOnlyDictionary<Guid, PhraseUsageStat> stats)
    {
        _phraseUsageStats.Clear();
        foreach (var pair in stats)
        {
            _phraseUsageStats[pair.Key] = pair.Value;
        }
    }

    private void LoadFileUsageStats(IReadOnlyDictionary<Guid, FileUsageStat> stats)
    {
        _fileUsageStats.Clear();
        foreach (var pair in stats)
        {
            _fileUsageStats[pair.Key] = pair.Value;
        }
    }

    internal int GetPhraseSendCount(Guid phraseId)
    {
        return _phraseUsageStats.TryGetValue(phraseId, out var stat) ? stat.SendCount : 0;
    }

    internal DateTimeOffset? GetPhraseLastSentAt(Guid phraseId)
    {
        return _phraseUsageStats.TryGetValue(phraseId, out var stat) ? stat.LastSentAt : null;
    }

    IReadOnlyList<ReplyPhrase> IRecommendationPhraseCatalog.GetAllPhrases()
    {
        return Categories
            .SelectMany(category => category.Phrases)
            .GroupBy(phrase => phrase.Id)
            .Select(group => group.First())
            .ToList();
    }

    int IRecommendationPhraseCatalog.GetPhraseSendCount(Guid phraseId)
    {
        return GetPhraseSendCount(phraseId);
    }

    DateTimeOffset? IRecommendationPhraseCatalog.GetPhraseLastSentAt(Guid phraseId)
    {
        return GetPhraseLastSentAt(phraseId);
    }

    internal int GetFileUseCount(Guid fileItemId)
    {
        return _fileUsageStats.TryGetValue(fileItemId, out var stat) ? stat.UseCount : 0;
    }

    internal DateTimeOffset? GetFileLastUsedAt(Guid fileItemId)
    {
        return _fileUsageStats.TryGetValue(fileItemId, out var stat) ? stat.LastUsedAt : null;
    }

    private void RecordPhraseSend(Guid phraseId)
    {
        var now = DateTimeOffset.Now;
        if (!_phraseUsageStats.TryGetValue(phraseId, out var stat))
        {
            stat = new PhraseUsageStat
            {
                PhraseId = phraseId,
                SendCount = 0,
                LastSentAt = null
            };
            _phraseUsageStats[phraseId] = stat;
        }

        stat.SendCount++;
        stat.LastSentAt = now;

        if (IsSearchActive)
        {
            RefreshFilteredPhrases();
        }

        _ = _dataStore.RecordPhraseSendAsync(phraseId, now);
    }

    private void RecordFileUse(Guid fileItemId)
    {
        var now = DateTimeOffset.Now;
        if (!_fileUsageStats.TryGetValue(fileItemId, out var stat))
        {
            stat = new FileUsageStat
            {
                FileItemId = fileItemId,
                UseCount = 0,
                LastUsedAt = null
            };
            _fileUsageStats[fileItemId] = stat;
        }

        stat.UseCount++;
        stat.LastUsedAt = now;

        _ = _dataStore.RecordFileUseAsync(fileItemId, now);
    }

    private static bool PhraseMatchesKeyword(ReplyPhrase phrase, string keyword)
    {
        return phrase.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               phrase.Tags.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               phrase.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPhraseDisplayName(ReplyPhrase phrase)
    {
        return phrase.HasDisplayTitle
            ? phrase.DisplayTitle
            : phrase.Content.Length <= 18
                ? phrase.Content
                : $"{phrase.Content[..17]}…";
    }

    private static double ComputePhraseRecommendationRecentBoost(DateTimeOffset? lastSentAt)
    {
        if (lastSentAt is null)
        {
            return 0;
        }

        var days = Math.Max(0, (DateTimeOffset.Now - lastSentAt.Value).TotalDays);
        return Math.Max(0, (12 - days) * 0.01d);
    }

    private IReadOnlyList<string> ResolvePhraseImagePaths(ReplyPhrase? phrase)
    {
        return phrase is null ? [] : _phraseImageStore.ResolveAbsolutePaths(phrase.ImageRelativePath);
    }

    private async Task MigrateLegacyPhraseImageLayoutAsync()
    {
        var phrases = Categories
            .SelectMany(category => category.Phrases)
            .GroupBy(phrase => phrase.Id)
            .Select(group => group.First())
            .ToList();

        if (phrases.Count == 0)
        {
            _phraseImageStore.CleanupLegacyPhraseFolders([]);
            return;
        }

        var migratedPhraseCount = 0;
        var repairedPhraseCount = 0;

        foreach (var phrase in phrases)
        {
            var currentRelativePaths = phrase.ImageRelativePaths;
            var migratedRelativePaths = await _phraseImageStore.MigrateLegacyPhraseImagesAsync(phrase.Id, phrase.ImageRelativePath);
            if (currentRelativePaths.SequenceEqual(migratedRelativePaths, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var originalUpdatedAt = phrase.UpdatedAt;
            phrase.SetImageRelativePaths(migratedRelativePaths);
            phrase.UpdatedAt = originalUpdatedAt;
            await _dataStore.UpdatePhraseAsync(phrase);

            if (migratedRelativePaths.Count > 0)
            {
                migratedPhraseCount++;
            }
            else
            {
                repairedPhraseCount++;
            }
        }

        var preservedLegacyPhraseIds = phrases
            .Where(phrase => phrase.ImageRelativePaths.Any(path =>
                path.Contains(Path.DirectorySeparatorChar) || path.Contains(Path.AltDirectorySeparatorChar)))
            .Select(phrase => phrase.Id);

        var cleanedLegacyFolderCount = _phraseImageStore.CleanupLegacyPhraseFolders(preservedLegacyPhraseIds);

        if (migratedPhraseCount == 0 && repairedPhraseCount == 0 && cleanedLegacyFolderCount == 0)
        {
            return;
        }

        SearchOverlayDiagnostics.Log(
            "phrase_images",
            "legacy_layout_migrated",
            new Dictionary<string, object?>
            {
                ["migratedPhraseCount"] = migratedPhraseCount,
                ["repairedPhraseCount"] = repairedPhraseCount,
                ["cleanedLegacyFolderCount"] = cleanedLegacyFolderCount
            });
    }

    private void DeletePhraseImages(ReplyPhrase phrase)
    {
        _phraseImageStore.DeletePhraseImageFolder(phrase.Id);
        phrase.ImageRelativePath = string.Empty;
    }

    private static void DeleteTransientImagesIfNeeded(IEnumerable<string> imageSourcePaths)
    {
        foreach (var imageSourcePath in imageSourcePaths)
        {
            if (string.IsNullOrWhiteSpace(imageSourcePath) || !File.Exists(imageSourcePath))
            {
                continue;
            }

            if (!PhraseEditorWindow.IsManagedTransientImagePath(imageSourcePath))
            {
                continue;
            }

            try
            {
                File.Delete(imageSourcePath);
            }
            catch
            {
            }
        }
    }

    private static bool FileItemMatchesKeyword(LibraryFileItem fileItem, string keyword)
    {
        return fileItem.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               fileItem.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               fileItem.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void LogSelectedPhraseUsage(
        string triggerSource,
        string? searchKeyword,
        ComposeActionMode actionMode,
        ReplyActionResult result,
        IntPtr targetWindowHandle,
        ConversationRecommendationSnapshot? recognitionOverride = null)
    {
        if (SelectedPhrase is null)
        {
            return;
        }

        LogPhraseUsage(SelectedPhrase, triggerSource, searchKeyword, actionMode, result, targetWindowHandle, recognitionOverride);
    }

    private void LogPhraseUsage(
        ReplyPhrase phrase,
        string triggerSource,
        string? searchKeyword,
        ComposeActionMode actionMode,
        ReplyActionResult result,
        IntPtr targetWindowHandle,
        ConversationRecommendationSnapshot? recognitionOverride = null)
    {
        _conversationLoggingService.LogPhraseUsage(
            phrase,
            triggerSource,
            actionMode,
            searchKeyword,
            result.Success,
            result.Message,
            targetWindowHandle,
            result.ComposeTelemetry,
            recognitionOverride);
    }

    private void LogFileUsage(
        LibraryFileItem fileItem,
        string triggerSource,
        string? searchKeyword,
        ComposeActionMode actionMode,
        ReplyActionResult result,
        IntPtr targetWindowHandle)
    {
        _conversationLoggingService.LogFileUsage(
            fileItem,
            triggerSource,
            actionMode,
            searchKeyword,
            result.Success,
            result.Message,
            targetWindowHandle,
            result.ComposeTelemetry);
    }

    private void LogQuickTagUsage(
        QuickTag quickTag,
        string triggerSource,
        ComposeActionMode actionMode,
        ReplyActionResult result,
        IntPtr targetWindowHandle)
    {
        _conversationLoggingService.LogQuickTagUsage(
            quickTag,
            triggerSource,
            actionMode,
            result.Success,
            result.Message,
            targetWindowHandle,
            result.ComposeTelemetry);
    }

    private void ApplySettingsSnapshot(AppSettings source)
    {
        ApplySettingsSnapshot(source, suppressStartupConfigurationNotifications: false);
    }

    private void ApplySettingsSnapshot(AppSettings source, bool suppressStartupConfigurationNotifications)
    {
        var previousSuppression = _suppressStartupConfigurationNotifications;
        _suppressStartupConfigurationNotifications = previousSuppression || suppressStartupConfigurationNotifications;

        try
        {
            AppSettingsSnapshotService.Apply(Settings, source);
        }
        finally
        {
            _suppressStartupConfigurationNotifications = previousSuppression;
        }
    }

    private void UpdateCommandStates()
    {
        AddCategoryCommand.NotifyCanExecuteChanged();
        DeleteCategoryCommand.NotifyCanExecuteChanged();
        AddPhraseCommand.NotifyCanExecuteChanged();
        DeletePhraseCommand.NotifyCanExecuteChanged();
        AddQuickTagCommand.NotifyCanExecuteChanged();
        CopyPhraseCommand.NotifyCanExecuteChanged();
        InputToCurrentWindowCommand.NotifyCanExecuteChanged();
        SendPhraseCommand.NotifyCanExecuteChanged();
        SendQuickTagCommand.NotifyCanExecuteChanged();
        DeleteQuickTagCommand.NotifyCanExecuteChanged();
        MoveQuickTagLeftCommand.NotifyCanExecuteChanged();
        MoveQuickTagRightCommand.NotifyCanExecuteChanged();
        SaveNowCommand.NotifyCanExecuteChanged();
    }
}
