using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.ViewModels;

public sealed class SearchOverlayViewModel : ObservableObject
{
    private static readonly IReadOnlyList<PhraseRecommendationSuggestion> EmptyRecommendedPhrases = Array.Empty<PhraseRecommendationSuggestion>();
    private static readonly IReadOnlyList<HistoricalReplyGroupSuggestion> EmptyReplyGroups = Array.Empty<HistoricalReplyGroupSuggestion>();
    private readonly MainViewModel _mainViewModel;
    private IReadOnlyList<SearchOverlayResultItem> _results = Array.Empty<SearchOverlayResultItem>();
    private string _actionStatusMessage = string.Empty;
    private string _recognitionCorrectionText = string.Empty;
    private string _searchText = string.Empty;
    private string _statusMessage = string.Empty;
    private SearchOverlayResultItem? _selectedResult;
    private SearchOverlayMode _currentMode = SearchOverlayMode.Phrase;
    private bool _isSettingsPreviewMode;
    private bool _isActionStatusWarning;
    private bool _isEditingRecognition;

    public SearchOverlayViewModel(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        _mainViewModel.Settings.PropertyChanged += Settings_OnPropertyChanged;
    }

    public SearchOverlayViewModel(
        MainViewModel mainViewModel,
        object? recommendationOrchestrator,
        object? recommendationTrackingService)
        : this(mainViewModel)
    {
    }

    public IReadOnlyList<SearchOverlayResultItem> Results
    {
        get => _results;
        private set
        {
            if (SetProperty(ref _results, value))
            {
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(ResultsSectionSummary));
                OnPropertyChanged(nameof(HasResultsSectionSummary));
                OnPropertyChanged(nameof(EmptyMessage));
                OnPropertyChanged(nameof(HasEmptyState));
                OnPropertyChanged(nameof(EmptyStateTitle));
                OnPropertyChanged(nameof(EmptyStateHintText));
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ResultsSectionSummary));
            OnPropertyChanged(nameof(HasResultsSectionSummary));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateHintText));
            OnPropertyChanged(nameof(HasManualSearchText));
            OnPropertyChanged(nameof(HasOverlayAssistText));
            RefreshResults();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(OverlayAssistText));
                OnPropertyChanged(nameof(HasOverlayAssistText));
            }
        }
    }

    public SearchOverlayResultItem? SelectedResult
    {
        get => _selectedResult;
        set => SetProperty(ref _selectedResult, value);
    }

    public SearchOverlayMode CurrentMode
    {
        get => _currentMode;
        private set
        {
            if (!SetProperty(ref _currentMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsPhraseMode));
            OnPropertyChanged(nameof(IsFileMode));
            OnPropertyChanged(nameof(IsMixedMode));
            OnPropertyChanged(nameof(CurrentModeToastText));
            OnPropertyChanged(nameof(ModeHintText));
            OnPropertyChanged(nameof(SearchBoxHintText));
            OnPropertyChanged(nameof(CurrentModeDisplayText));
            OnPropertyChanged(nameof(ModeAssistText));
            OnPropertyChanged(nameof(SearchPlaceholderText));
            OnPropertyChanged(nameof(EmptyMessage));
            OnPropertyChanged(nameof(ResultsSectionTitle));
            OnPropertyChanged(nameof(ResultsSectionSummary));
            OnPropertyChanged(nameof(HasResultsSectionSummary));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateHintText));
        }
    }

    public bool HasResults => Results.Count > 0;

    public IReadOnlyList<PhraseRecommendationSuggestion> RecommendedPhrases => EmptyRecommendedPhrases;

    public IReadOnlyList<PhraseRecommendationSuggestion> RecommendationItems => EmptyRecommendedPhrases;



    public IReadOnlyList<HistoricalReplyGroupSuggestion> SimilarReplyGroups => EmptyReplyGroups;

    public bool HasRecommendations => false;


    public bool HasRecommendationContext => false;

    public bool HasRecognizedCustomerMessage => false;

    public string RecognizedCustomerMessageText => string.Empty;

    public bool HasRecommendationPanelContent => false;

    public bool HasManualSearchText => SearchOverlayLayoutModeResolver.HasManualSearchText(SearchText);

    public bool ShouldShowRecommendationPanel => false;

    public bool ShouldShowSearchResultsPanel => true;

    public bool HasSimilarReplyGroups => false;

    public AppSettings Settings => _mainViewModel.Settings;

    public bool IsCompactPhraseList => _mainViewModel.IsCompactPhraseList;

    public Thickness ResultItemMargin => IsCompactPhraseList
        ? new Thickness(0, 0, 0, 4)
        : new Thickness(0, 0, 0, 6);

    public Thickness ResultOuterMargin => IsCompactPhraseList
        ? new Thickness(6, 3, 6, 3)
        : new Thickness(7, 4, 7, 4);

    public double ResultNumberSize => IsCompactPhraseList ? 16 : 18;

    public double ResultTitleFontSize => _mainViewModel.PhraseTitleFontSize;

    public bool IsPhraseMode => CurrentMode == SearchOverlayMode.Phrase;

    public bool IsFileMode => CurrentMode == SearchOverlayMode.File;

    public bool IsMixedMode => CurrentMode == SearchOverlayMode.Mixed;

    public bool IsCompactSidebarMode => Settings.SidebarWorkMode == SidebarWorkMode.Compact;

    public int RecommendationDisplayCount => 0;

    public string CurrentModeDisplayText => CurrentMode switch
    {
        SearchOverlayMode.File => "当前模式：文件搜索",
        SearchOverlayMode.Mixed => "当前模式：混合搜索",
        _ => "当前模式：短语搜索"
    };

    public string CurrentModeToastText => CurrentMode switch
    {
        SearchOverlayMode.File => "已切换到文件搜索",
        SearchOverlayMode.Mixed => "已切换到混合搜索",
        _ => "已切换到短语搜索"
    };

    public string ModeAssistText => CurrentMode switch
    {
        SearchOverlayMode.File => "左右方向键切换模式，当前只搜索文件",
        SearchOverlayMode.Mixed => "左右方向键切换模式，同时搜索短语和文件",
        _ => "左右方向键切换模式，当前只搜索快捷短语"
    };

    public string SearchPlaceholderText => CurrentMode switch
    {
        SearchOverlayMode.File => "当前是文件搜索，输入文件名；按左右方向键切换到短语或混合搜索",
        SearchOverlayMode.Mixed => "当前是混合搜索，输入关键词；按左右方向键切换到短语或文件搜索",
        _ => "当前是短语搜索，输入关键词；按左右方向键切换到文件或混合搜索"
    };

    public string SidebarModeButtonText => Settings.SidebarWorkMode == SidebarWorkMode.Compact ? "精简" : "标准";

    public string SidebarModeButtonToolTip => Settings.SidebarWorkMode == SidebarWorkMode.Compact
        ? "当前是精简模式，不显示侧边快捷短语栏。点击切回标准模式。"
        : "当前是标准模式，会自动显示完整快捷短语栏。点击切到精简模式。";

    public string ModeHintText => CurrentMode switch
    {
        SearchOverlayMode.File => "文件模式  左右切换",
        SearchOverlayMode.Mixed => "混合模式  左右切换",
        _ => "短语模式  左右切换"
    };

    public string RecommendationHintText => string.Empty;

    public bool HasRecommendationHintText => false;

    public string RecommendationStatusText => string.Empty;

    public bool HasRecommendationStatusText => false;

    public string GroundingSummaryText => string.Empty;

    public bool HasGroundingSummaryText => false;

    public string ActionStatusMessage
    {
        get => _actionStatusMessage;
        private set
        {
            if (SetProperty(ref _actionStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasActionStatusMessage));
                OnPropertyChanged(nameof(OverlayAssistText));
                OnPropertyChanged(nameof(HasOverlayAssistText));
                OnPropertyChanged(nameof(IsOverlayAssistWarning));
            }
        }
    }

    public bool HasActionStatusMessage => !string.IsNullOrWhiteSpace(ActionStatusMessage);

    public bool IsActionStatusWarning
    {
        get => _isActionStatusWarning;
        private set
        {
            if (SetProperty(ref _isActionStatusWarning, value))
            {
                OnPropertyChanged(nameof(IsOverlayAssistWarning));
            }
        }
    }

    public string OverlayAssistText => HasActionStatusMessage ? ActionStatusMessage : string.Empty;

    public bool HasOverlayAssistText =>
        SearchOverlayLayoutModeResolver.ShouldShowOverlayAssistText(SearchText, HasActionStatusMessage) &&
        !string.IsNullOrWhiteSpace(OverlayAssistText);

    public bool IsOverlayAssistWarning => HasActionStatusMessage && IsActionStatusWarning;

    public bool IsRefreshingRecommendations => false;

    public bool IsEditingRecognition
    {
        get => _isEditingRecognition;
        private set => SetProperty(ref _isEditingRecognition, value);
    }

    public string RecognitionCorrectionText
    {
        get => _recognitionCorrectionText;
        set => SetProperty(ref _recognitionCorrectionText, value);
    }

    public string RecommendationContextText => string.Empty;

    public bool HasRecommendationContextText => false;

    public string SearchBoxHintText
    {
        get
        {
            return CurrentMode switch
            {
                SearchOverlayMode.File => "文件搜索模式，按左右方向键切换模式",
                SearchOverlayMode.Mixed => "混合搜索模式，同时搜索短语和文件",
                _ => "短语搜索模式，按右方向键切换到文件或混合模式"
            };
        }
    }

    public string EmptyMessage => string.IsNullOrWhiteSpace(SearchText)
        ? string.Empty
        : CurrentMode switch
        {
            SearchOverlayMode.File => "没有匹配的文件。",
            SearchOverlayMode.Mixed => "没有匹配的短语或文件。",
            _ => "没有匹配的短语。"
        };

    public string ResultsSectionTitle => SearchOverlayPanelStatusBuilder.BuildResultsSectionTitle(CurrentMode);

    public string ResultsSectionSummary => SearchOverlayPanelStatusBuilder.BuildResultsSectionSummary(
        CurrentMode,
        SearchText,
        Results.Count);

    public bool HasResultsSectionSummary => !string.IsNullOrWhiteSpace(ResultsSectionSummary);

    public bool HasEmptyState => !HasResults;

    public string EmptyStateTitle => SearchOverlayPanelStatusBuilder.BuildEmptyStateTitle(CurrentMode, SearchText);

    public string EmptyStateHintText => SearchOverlayPanelStatusBuilder.BuildEmptyStateHintText(
        CurrentMode,
        SearchText);

    public void PrepareForOpen()
    {
        _isSettingsPreviewMode = false;
        ClearActionStatus();
        CurrentMode = Settings.DefaultSearchOverlayMode;
        StatusMessage = string.Empty;

        if (string.IsNullOrEmpty(SearchText))
        {
            RefreshResults();
        }
        else
        {
            SearchText = string.Empty;
        }
    }

    public void BeginSettingsPreview(SearchOverlayMode mode)
    {
        _isSettingsPreviewMode = true;
        ClearActionStatus();
        CurrentMode = mode;
        _searchText = mode switch
        {
            SearchOverlayMode.File => "安装包",
            SearchOverlayMode.Mixed => "安装",
            _ => "发货"
        };
        OnPropertyChanged(nameof(SearchText));
        StatusMessage = "当前为真实搜索栏预览，关闭选项或切换其他分类后会自动恢复。";
        RefreshResults();
    }

    public void UpdateSettingsPreview(SearchOverlayMode mode)
    {
        if (!_isSettingsPreviewMode)
        {
            BeginSettingsPreview(mode);
            return;
        }

        CurrentMode = mode;
        _searchText = mode switch
        {
            SearchOverlayMode.File => "安装包",
            SearchOverlayMode.Mixed => "安装",
            _ => "发货"
        };
        OnPropertyChanged(nameof(SearchText));
        RefreshResults();
    }

    public void EndSettingsPreview()
    {
        if (!_isSettingsPreviewMode)
        {
            return;
        }

        _isSettingsPreviewMode = false;
        ClearActionStatus();
        SearchText = string.Empty;
    }

    public void SwitchMode(int direction)
    {
        if (direction == 0)
        {
            return;
        }

        var modes = new[]
        {
            SearchOverlayMode.Phrase,
            SearchOverlayMode.File,
            SearchOverlayMode.Mixed
        };

        var currentIndex = Array.IndexOf(modes, CurrentMode);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var nextIndex = direction > 0
            ? (currentIndex + 1) % modes.Length
            : (currentIndex - 1 + modes.Length) % modes.Length;

        SetMode(modes[nextIndex]);
    }

    public void SetMode(SearchOverlayMode mode)
    {
        if (CurrentMode == mode)
        {
            return;
        }

        CurrentMode = mode;
        StatusMessage = string.Empty;
        RefreshResults();
    }

    public Task ToggleSidebarWorkModeAsync()
    {
        return _mainViewModel.ToggleSidebarWorkModeAsync();
    }

    public void RefreshSidebarModePresentation()
    {
        OnPropertyChanged(nameof(IsCompactSidebarMode));
        OnPropertyChanged(nameof(SidebarModeButtonText));
        OnPropertyChanged(nameof(SidebarModeButtonToolTip));
    }

    public SearchOverlayResultItem? GetResultByNumber(int number)
    {
        return Results.FirstOrDefault(item => item.Number == number);
    }

    public SearchOverlayResultItem? GetSelectedResult()
    {
        return SelectedResult ?? Results.FirstOrDefault();
    }

    public async Task<ReplyActionResult> SendSearchTextAsync()
    {
        var result = await _mainViewModel.SendPlainTextAsync(
            SearchText,
            "search_overlay_plain_text_send",
            SearchText);

        if (result.Success)
        {
            SearchText = string.Empty;
        }

        ShowActionStatus(result);
        return result;
    }

    public async Task<ReplyActionResult> SaveSearchTextAsPhraseAsync()
    {
        var result = await _mainViewModel.SavePlainTextAsPhraseAsync(SearchText);
        ShowActionStatus(result);

        if (result.Success)
        {
            SearchText = string.Empty;
        }

        return result;
    }

    public async Task<(ReplyActionResult SendResult, ReplyActionResult SaveResult)> SendSearchTextAndSaveAsPhraseAsync()
    {
        var pendingSearchText = SearchText;
        var sendResult = await _mainViewModel.SendPlainTextAsync(
            pendingSearchText,
            "search_overlay_plain_text_send_and_save",
            pendingSearchText);

        if (!sendResult.Success)
        {
            ShowActionStatus(sendResult);
            return (sendResult, new ReplyActionResult(false, "发送失败，未执行保存。"));
        }

        var saveResult = await _mainViewModel.SavePlainTextAsPhraseAsync(pendingSearchText);
        var compositeSaveResult = saveResult.Success
            ? saveResult
            : new ReplyActionResult(
                false,
                $"发送成功，但保存快捷短语失败：{saveResult.Message}",
                FailureKind: saveResult.FailureKind,
                UserGuidance: saveResult.UserGuidance);
        ShowActionStatus(compositeSaveResult);

        if (saveResult.Success)
        {
            SearchText = string.Empty;
        }

        return (sendResult, saveResult);
    }

    public void ShowActionStatus(ReplyActionResult result)
    {
        ActionStatusMessage = SearchOverlayActionFeedbackFormatter.BuildMessage(result);
        IsActionStatusWarning = SearchOverlayActionFeedbackFormatter.IsWarning(result);
    }

    public void ClearActionStatus()
    {
        ActionStatusMessage = string.Empty;
        IsActionStatusWarning = false;
    }

    public IReadOnlyList<string> GetPhraseImageAbsolutePaths(ReplyPhrase phrase)
    {
        return _mainViewModel.GetPhraseImageAbsolutePaths(phrase);
    }

    public IReadOnlyList<ReplyCategory> GetCategories()
    {
        return _mainViewModel.Categories.ToList();
    }

    public IReadOnlyList<ReplyPhrase> GetMultiSelectedPhrases()
    {
        return _mainViewModel.GetMultiSelectedPhrases();
    }

    public void ClearPhraseMultiSelection()
    {
        _mainViewModel.ClearPhraseMultiSelection();
    }

    public void MoveSelection(int offset)
    {
        if (Results.Count == 0)
        {
            SelectedResult = null;
            return;
        }

        var currentIndex = SelectedResult is null
            ? -1
            : Results
                .Select((item, index) => new { item, index })
                .FirstOrDefault(entry => ReferenceEquals(entry.item, SelectedResult))
                ?.index ?? -1;

        if (currentIndex < 0)
        {
            SelectedResult = offset >= 0 ? Results[0] : Results[^1];
            return;
        }

        var nextIndex = Math.Clamp(currentIndex + offset, 0, Results.Count - 1);
        SelectedResult = Results[nextIndex];
    }

    public async Task<ReplyActionResult> SendResultAsync(int number)
    {
        var item = GetResultByNumber(number);
        if (item is null)
        {
            return new ReplyActionResult(false, $"未找到编号 {number} 对应的结果。");
        }

        ReplyActionResult result;
        if (item.Mode == SearchOverlayMode.File)
        {
            if (item.FileItem is null)
            {
                return new ReplyActionResult(false, "当前文件结果无效。");
            }

            result = await _mainViewModel.SendFileItemDirectAsync(item.FileItem, "search_overlay_file_send", SearchText);
        }
        else
        {
            if (item.Phrase is null)
            {
                return new ReplyActionResult(false, "当前短语结果无效。");
            }

            result = await _mainViewModel.SendPhraseDirectAsync(item.Phrase, "search_overlay_phrase_send", SearchText);
        }

        StatusMessage = result.Message;
        return result;
    }

    public async Task<ReplyActionResult> CopyResultAsync(int number)
    {
        var item = GetResultByNumber(number);
        if (item is null)
        {
            return new ReplyActionResult(false, $"未找到编号 {number} 对应的结果。");
        }

        ReplyActionResult result;
        if (item.Mode == SearchOverlayMode.File)
        {
            if (item.FileItem is null)
            {
                return new ReplyActionResult(false, "当前文件结果无效。");
            }

            result = await _mainViewModel.CopyFileItemDirectAsync(item.FileItem, "search_overlay_file_copy", SearchText);
        }
        else
        {
            if (item.Phrase is null)
            {
                return new ReplyActionResult(false, "当前短语结果无效。");
            }

            result = await _mainViewModel.CopyPhraseDirectAsync(item.Phrase, "search_overlay_phrase_copy", SearchText);
        }

        StatusMessage = result.Message;
        return result;
    }

    public async Task<ReplyActionResult> CopyResultImagesAsync(int number)
    {
        var item = GetResultByNumber(number);
        if (item is null)
        {
            return new ReplyActionResult(false, $"未找到编号 {number} 对应的结果。");
        }

        if (item.Phrase is null)
        {
            return new ReplyActionResult(false, "当前短语结果没有可复制的图片。");
        }

        var result = await _mainViewModel.CopyPhraseImagesDirectAsync(item.Phrase, "search_overlay_phrase_copy_images", SearchText);
        StatusMessage = result.Message;
        return result;
    }

    public async Task<ReplyActionResult> InputResultAsync(int number)
    {
        var item = GetResultByNumber(number);
        if (item is null)
        {
            return new ReplyActionResult(false, $"未找到编号 {number} 对应的结果。");
        }

        ReplyActionResult result;
        if (item.Mode == SearchOverlayMode.File)
        {
            if (item.FileItem is null)
            {
                return new ReplyActionResult(false, "当前文件结果无效。");
            }

            result = await _mainViewModel.InputFileItemDirectAsync(item.FileItem, "search_overlay_file_input", SearchText);
        }
        else
        {
            if (item.Phrase is null)
            {
                return new ReplyActionResult(false, "当前短语结果无效。");
            }

            result = await _mainViewModel.InputPhraseDirectAsync(item.Phrase, "search_overlay_phrase_input", SearchText);
        }

        StatusMessage = result.Message;
        return result;
    }

    public async Task DeleteResultAsync(int number)
    {
        var item = GetResultByNumber(number);
        if (item is null)
        {
            return;
        }

        if (item.Mode == SearchOverlayMode.File)
        {
            if (item.FileItem is null)
            {
                return;
            }

            await _mainViewModel.DeleteFileItemAsync(item.FileItem);
        }
        else
        {
            if (item.Phrase is null)
            {
                return;
            }

            await _mainViewModel.DeletePhraseAsync(item.Phrase);
        }

        RefreshResults();
    }

    public async Task<int> DeletePhrasesAsync(IEnumerable<ReplyPhrase> phrases)
    {
        var deletedCount = await _mainViewModel.DeletePhrasesAsync(phrases);
        RefreshResults();
        return deletedCount;
    }

    public async Task<int> MovePhrasesToCategoryAsync(IEnumerable<ReplyPhrase> phrases, ReplyCategory targetCategory)
    {
        var movedCount = await _mainViewModel.MovePhrasesToCategoryAsync(phrases, targetCategory);
        RefreshResults();
        return movedCount;
    }

    public async Task UpdatePhraseResultAsync(
        SearchOverlayResultItem item,
        string title,
        string content,
        IReadOnlyCollection<string>? imageSourcePaths = null,
        bool removeImage = false)
    {
        if (item.Phrase is null)
        {
            return;
        }

        await _mainViewModel.UpdatePhraseAsync(item.Phrase, title, title, content, imageSourcePaths, removeImage);
        RefreshResults();
        SelectedResult = Results.FirstOrDefault(result => result.Phrase?.Id == item.Phrase.Id);
    }

    public ReplyActionResult OpenFileResult(SearchOverlayResultItem item)
    {
        if (item.FileItem is null)
        {
            return new ReplyActionResult(false, "当前文件结果无效。");
        }

        if (!File.Exists(item.FileItem.FullPath))
        {
            return new ReplyActionResult(false, $"文件不存在：{item.FileItem.FullPath}");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.FileItem.FullPath,
                UseShellExecute = true
            });

            var result = new ReplyActionResult(true, $"已打开文件：{item.FileItem.DisplayName}");
            StatusMessage = result.Message;
            return result;
        }
        catch (Exception ex)
        {
            var result = new ReplyActionResult(false, $"打开文件失败：{ex.Message}");
            StatusMessage = result.Message;
            return result;
        }
    }

    private void RefreshResults()
    {
        Results = _isSettingsPreviewMode
            ? BuildSettingsPreviewResults(CurrentMode)
            : CurrentMode switch
            {
                SearchOverlayMode.File => BuildFileResults(_mainViewModel.SearchFileItems(SearchText, 9)),
                SearchOverlayMode.Mixed => BuildMixedResults(SearchText, 9),
                _ => BuildPhraseResults(SearchText, 9)
            };

        SelectedResult = Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private void Settings_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.DefaultSearchOverlayMode))
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                CurrentMode = Settings.DefaultSearchOverlayMode;
                RefreshResults();
            }

            return;
        }

        if (e.PropertyName == nameof(AppSettings.SidebarWorkMode))
        {
            OnPropertyChanged(nameof(IsCompactSidebarMode));
            OnPropertyChanged(nameof(SidebarModeButtonText));
            OnPropertyChanged(nameof(SidebarModeButtonToolTip));
            return;
        }

        if (e.PropertyName is not nameof(AppSettings.PhraseListDensity))
        {
            return;
        }

        OnPropertyChanged(nameof(IsCompactPhraseList));
        OnPropertyChanged(nameof(ResultItemMargin));
        OnPropertyChanged(nameof(ResultOuterMargin));
        OnPropertyChanged(nameof(ResultNumberSize));
        OnPropertyChanged(nameof(ResultTitleFontSize));
    }

    private IReadOnlyList<SearchOverlayResultItem> BuildPhraseResults(string keyword, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return [];
        }

        IReadOnlyList<ReplyPhrase> phrases = _mainViewModel.SearchPhrases(keyword, maxResults);

        return phrases
            .Select((phrase, index) => new SearchOverlayResultItem
            {
                Number = index + 1,
                Mode = SearchOverlayMode.Phrase,
                Phrase = phrase
            })
            .ToArray();
    }

    private static IReadOnlyList<SearchOverlayResultItem> BuildFileResults(IReadOnlyList<LibraryFileItem> fileItems)
    {
        return fileItems
            .Select((fileItem, index) => new SearchOverlayResultItem
            {
                Number = index + 1,
                Mode = SearchOverlayMode.File,
                FileItem = fileItem
            })
            .ToArray();
    }

    private static IReadOnlyList<SearchOverlayResultItem> BuildSettingsPreviewResults(SearchOverlayMode mode)
    {
        return mode switch
        {
            SearchOverlayMode.File =>
            [
                new SearchOverlayResultItem
                {
                    Number = 1,
                    Mode = SearchOverlayMode.File,
                    PreviewTitle = "安装教程.mp4",
                    PreviewLabelText = "MP4",
                    PreviewContent = @"C:\客服资料\安装视频 | 18.6M | 2026-03-20 21:18",
                    PreviewHasLabel = true
                },
                new SearchOverlayResultItem
                {
                    Number = 2,
                    Mode = SearchOverlayMode.File,
                    PreviewTitle = "安装步骤图.png",
                    PreviewLabelText = "PNG",
                    PreviewContent = @"C:\客服资料\安装图片 | 0.8M | 2026-03-18 10:42",
                    PreviewHasLabel = true
                },
                new SearchOverlayResultItem
                {
                    Number = 3,
                    Mode = SearchOverlayMode.File,
                    PreviewTitle = "售后安装说明.docx",
                    PreviewLabelText = "DOCX",
                    PreviewContent = @"C:\客服资料\文档 | 0.2M | 2026-03-17 09:05",
                    PreviewHasLabel = true
                }
            ],
            SearchOverlayMode.Mixed =>
            [
                new SearchOverlayResultItem
                {
                    Number = 1,
                    Mode = SearchOverlayMode.Phrase,
                    PreviewLabelText = "安装时效",
                    PreviewContent = "您好，这边先给您发安装教程，您按步骤操作即可。",
                    PreviewHasLabel = true
                },
                new SearchOverlayResultItem
                {
                    Number = 2,
                    Mode = SearchOverlayMode.File,
                    PreviewTitle = "安装教程.mp4",
                    PreviewLabelText = "MP4",
                    PreviewContent = @"C:\客服资料\安装视频 | 18.6M | 2026-03-20 21:18",
                    PreviewHasLabel = true
                },
                new SearchOverlayResultItem
                {
                    Number = 3,
                    Mode = SearchOverlayMode.Phrase,
                    PreviewLabelText = "安装提示",
                    PreviewContent = "如果安装中遇到卡顿，您先退出重进一次，再继续下一步。",
                    PreviewHasLabel = true
                }
            ],
            _ =>
            [
                new SearchOverlayResultItem
                {
                    Number = 1,
                    Mode = SearchOverlayMode.Phrase,
                    PreviewLabelText = "发货时效",
                    PreviewContent = "您好，这边会在 24 小时内为您安排发出，请您留意物流更新。",
                    PreviewHasLabel = true
                },
                new SearchOverlayResultItem
                {
                    Number = 2,
                    Mode = SearchOverlayMode.Phrase,
                    PreviewLabelText = "催付提醒",
                    PreviewContent = "您好，您挑选的商品库存较紧张，如需保留请尽快付款。",
                    PreviewHasLabel = true
                },
                new SearchOverlayResultItem
                {
                    Number = 3,
                    Mode = SearchOverlayMode.Phrase,
                    PreviewLabelText = "发货",
                    PreviewContent = "今天下午五点前的订单正常都会当天安排发出。",
                    PreviewHasLabel = true
                }
            ]
        };
    }

    private IReadOnlyList<SearchOverlayResultItem> BuildMixedResults(string keyword, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return [];
        }

        var candidateCount = Math.Max(maxResults * 2, 12);
        var normalizedKeyword = keyword.Trim();

        var phraseCandidates = _mainViewModel
            .SearchPhrases(normalizedKeyword, candidateCount)
            .Select((phrase, index) => new
            {
                Score = ComputePhraseMixedScore(phrase, normalizedKeyword, index),
                Item = new SearchOverlayResultItem
                {
                    Number = 0,
                    Mode = SearchOverlayMode.Phrase,
                    Phrase = phrase
                }
            });

        var fileCandidates = _mainViewModel
            .SearchFileItems(normalizedKeyword, candidateCount)
            .Select((fileItem, index) => new
            {
                Score = ComputeFileMixedScore(fileItem, normalizedKeyword, index),
                Item = new SearchOverlayResultItem
                {
                    Number = 0,
                    Mode = SearchOverlayMode.File,
                    FileItem = fileItem
                }
            });

        return phraseCandidates
            .Concat(fileCandidates)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Item.Mode != SearchOverlayMode.Phrase)
            .Take(maxResults)
            .Select((entry, index) => new SearchOverlayResultItem
            {
                Number = index + 1,
                Mode = entry.Item.Mode,
                Phrase = entry.Item.Phrase,
                FileItem = entry.Item.FileItem,
                IsRecommended = entry.Item.IsRecommended,
                RecommendationBadgeText = entry.Item.RecommendationBadgeText,
                RecommendationDetailText = entry.Item.RecommendationDetailText
            })
            .ToArray();
    }

    private double ComputePhraseMixedScore(ReplyPhrase phrase, string keyword, int sourceIndex)
    {
        var score = 0d;
        var title = phrase.Title?.Trim() ?? string.Empty;
        var tags = phrase.Tags?.Trim() ?? string.Empty;
        var content = phrase.Content?.Trim() ?? string.Empty;

        if (title.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            score += 140;
        }
        else if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            score += 110;
        }

        if (!string.IsNullOrWhiteSpace(tags))
        {
            if (tags.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            {
                score += 85;
            }
            else if (tags.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                score += 65;
            }
        }

        if (!string.IsNullOrWhiteSpace(content) && content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            score += 28;
        }

        score += Math.Log2(_mainViewModel.GetPhraseSendCount(phrase.Id) + 1) * 22;
        score += ComputeRecentBoost(_mainViewModel.GetPhraseLastSentAt(phrase.Id));
        score -= sourceIndex * 0.8d;
        return score;
    }

    private double ComputeFileMixedScore(LibraryFileItem fileItem, string keyword, int sourceIndex)
    {
        var score = 0d;
        var displayName = fileItem.DisplayName?.Trim() ?? string.Empty;
        var fileName = fileItem.FileName?.Trim() ?? string.Empty;
        var fullPath = fileItem.FullPath?.Trim() ?? string.Empty;

        if (displayName.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            score += 140;
        }
        else if (displayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            score += 112;
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            if (fileName.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            {
                score += 95;
            }
            else if (fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                score += 76;
            }
        }

        if (!string.IsNullOrWhiteSpace(fullPath) && fullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            score += 22;
        }

        score += Math.Log2(_mainViewModel.GetFileUseCount(fileItem.Id) + 1) * 22;
        score += ComputeRecentBoost(_mainViewModel.GetFileLastUsedAt(fileItem.Id));
        score -= sourceIndex * 0.8d;
        return score;
    }

    public Task RefreshRecommendationsForOpenAsync()
    {
        return Task.CompletedTask;
    }

    public async Task<ReplyActionResult> SendRecommendedPhraseAsync(PhraseRecommendationSuggestion suggestion)
    {
        await Task.CompletedTask;
        return BuildRetiredRecommendationResult();
    }

    public async Task<ReplyActionResult> InputRecommendedPhraseAsync(PhraseRecommendationSuggestion suggestion)
    {
        await Task.CompletedTask;
        return BuildRetiredRecommendationResult();
    }



    public void BeginRecognitionCorrection()
    {
        RecognitionCorrectionText = string.Empty;
        IsEditingRecognition = false;
        StatusMessage = "识别修正功能已移除。";
    }

    public void CancelRecognitionCorrection()
    {
        RecognitionCorrectionText = string.Empty;
        IsEditingRecognition = false;
    }

    public async Task SaveRecognitionCorrectionAsync()
    {
        await Task.CompletedTask;
        RecognitionCorrectionText = string.Empty;
        IsEditingRecognition = false;
        StatusMessage = "识别修正功能已移除。";
    }

    public ConversationRecognitionCorrection CreateRecognitionCorrectionDraft()
    {
        return new ConversationRecognitionCorrection
        {
            CustomerText = string.Empty,
            ContextText = string.Empty,
            ProductId = string.Empty,
            ProductName = string.Empty,
            Messages = []
        };
    }

    public async Task SaveRecognitionCorrectionAsync(ConversationRecognitionCorrection correction)
    {
        await Task.CompletedTask;
        RecognitionCorrectionText = string.Empty;
        IsEditingRecognition = false;
        StatusMessage = "识别修正功能已移除。";
    }

    private static double ComputeRecentBoost(DateTimeOffset? lastUsedAt)
    {
        if (lastUsedAt is null)
        {
            return 0;
        }

        var days = Math.Max(0, (DateTimeOffset.Now - lastUsedAt.Value).TotalDays);
        return Math.Max(0, 18 - days);
    }

    private ReplyActionResult BuildRetiredRecommendationResult()
    {
        var result = new ReplyActionResult(false, "推荐功能已移除，请直接搜索并发送短语或文件。");
        ShowActionStatus(result);
        return result;
    }

}
