using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.ViewModels;

public sealed class SearchOverlayViewModel : ObservableObject
{
    private const int MinimumRecommendationSampleCount = 3;
    private const int RecommendationContextPreviewMaxLength = 160;
    private readonly MainViewModel _mainViewModel;
    private readonly IRecommendationOrchestrator _recommendationOrchestrator;
    private readonly IRecommendationTrackingService _recommendationTrackingService;
    private IReadOnlyList<SearchOverlayResultItem> _results = Array.Empty<SearchOverlayResultItem>();
    private IReadOnlyList<PhraseRecommendationSuggestion> _recommendedPhrases = Array.Empty<PhraseRecommendationSuggestion>();
    private IReadOnlyList<KnowledgeCardSuggestion> _knowledgeCards = Array.Empty<KnowledgeCardSuggestion>();
    private IReadOnlyList<GeneratedReplyCandidate> _generatedReplies = Array.Empty<GeneratedReplyCandidate>();
    private IReadOnlyList<HistoricalReplyGroupSuggestion> _similarReplyGroups = Array.Empty<HistoricalReplyGroupSuggestion>();
    private ConversationRecommendationSnapshot _recommendationSnapshot = new();
    private AiDraftGenerationStatus _lastAiDraftStatus = AiDraftGenerationStatus.Disabled(string.Empty);
    private Guid _recommendationSessionId;
    private Guid _knowledgeCardSessionId;
    private Guid _aiDraftSessionId;
    private bool _aiDraftInteracted;
    private bool _isEditingRecognition;
    private string _recognitionCorrectionText = string.Empty;
    private string _aiDraftStatusMessage = string.Empty;
    private string _actionStatusMessage = string.Empty;
    private string _searchText = string.Empty;
    private string _statusMessage = "输入关键词后，可按数字 1-9 或 Enter 发送结果；Ctrl+S 仅保存原文，Ctrl+Shift+S 发送并保存。";
    private SearchOverlayResultItem? _selectedResult;
    private SearchOverlayMode _currentMode = SearchOverlayMode.Phrase;
    private bool _isSettingsPreviewMode;
    private bool _isActionStatusWarning;
    private bool _isRefreshingRecommendations;
    private int _recommendationRefreshVersion;

    public SearchOverlayViewModel(
        MainViewModel mainViewModel,
        IRecommendationOrchestrator recommendationOrchestrator,
        IRecommendationTrackingService recommendationTrackingService)
    {
        _mainViewModel = mainViewModel;
        _recommendationOrchestrator = recommendationOrchestrator;
        _recommendationTrackingService = recommendationTrackingService;
        _mainViewModel.Settings.PropertyChanged += Settings_OnPropertyChanged;
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

    public IReadOnlyList<PhraseRecommendationSuggestion> RecommendedPhrases
    {
        get => _recommendedPhrases;
        private set
        {
            if (SetProperty(ref _recommendedPhrases, value))
            {
                OnPropertyChanged(nameof(HasRecommendations));
                OnPropertyChanged(nameof(RecommendationItems));
                NotifyRecommendationContextChanged();
                NotifyRecommendationPanelStateChanged();
                OnPropertyChanged(nameof(RecommendationHintText));
                OnPropertyChanged(nameof(HasRecommendationHintText));
                OnPropertyChanged(nameof(GroundingSummaryText));
                OnPropertyChanged(nameof(HasGroundingSummaryText));
                OnPropertyChanged(nameof(SearchBoxHintText));
                OnPropertyChanged(nameof(ResultsSectionSummary));
                OnPropertyChanged(nameof(HasResultsSectionSummary));
                OnPropertyChanged(nameof(EmptyStateHintText));
            }
        }
    }

    public IReadOnlyList<PhraseRecommendationSuggestion> RecommendationItems => RecommendedPhrases.Take(RecommendationDisplayCount).ToArray();

    public IReadOnlyList<KnowledgeCardSuggestion> KnowledgeCards
    {
        get => _knowledgeCards;
        private set
        {
            if (SetProperty(ref _knowledgeCards, value))
            {
                OnPropertyChanged(nameof(HasKnowledgeCardMatches));
                OnPropertyChanged(nameof(KnowledgeCardItems));
                NotifyRecommendationPanelStateChanged();
                OnPropertyChanged(nameof(RecommendationHintText));
                OnPropertyChanged(nameof(HasRecommendationHintText));
                OnPropertyChanged(nameof(GroundingSummaryText));
                OnPropertyChanged(nameof(HasGroundingSummaryText));
                OnPropertyChanged(nameof(ResultsSectionSummary));
                OnPropertyChanged(nameof(HasResultsSectionSummary));
                OnPropertyChanged(nameof(EmptyStateHintText));
            }
        }
    }

    public IReadOnlyList<GeneratedReplyCandidate> GeneratedReplies
    {
        get => _generatedReplies;
        private set
        {
            if (SetProperty(ref _generatedReplies, value))
            {
                OnPropertyChanged(nameof(AiDraftItems));
                OnPropertyChanged(nameof(HasGeneratedReplies));
                NotifyRecommendationPanelStateChanged();
                OnPropertyChanged(nameof(ResultsSectionSummary));
                OnPropertyChanged(nameof(HasResultsSectionSummary));
                OnPropertyChanged(nameof(EmptyStateHintText));
            }
        }
    }

    public IReadOnlyList<GeneratedReplyCandidate> AiDraftItems => GeneratedReplies;

    public IReadOnlyList<HistoricalReplyGroupSuggestion> SimilarReplyGroups
    {
        get => _similarReplyGroups;
        private set
        {
            if (SetProperty(ref _similarReplyGroups, value))
            {
                OnPropertyChanged(nameof(HasSimilarReplyGroups));
                NotifyRecommendationPanelStateChanged();
                OnPropertyChanged(nameof(ResultsSectionSummary));
                OnPropertyChanged(nameof(HasResultsSectionSummary));
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
            OnPropertyChanged(nameof(ShouldShowRecommendationPanel));
            OnPropertyChanged(nameof(ShouldShowSearchResultsPanel));
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

    public bool HasRecommendations => RecommendedPhrases.Count > 0;

    public bool HasGeneratedReplies => GeneratedReplies.Count > 0;

    public bool HasKnowledgeCardMatches => KnowledgeCards.Count > 0;

    public IReadOnlyList<KnowledgeCardSuggestion> KnowledgeCardItems => KnowledgeCards.Take(RecommendationDisplayCount).ToArray();

    public bool HasRecommendationContext =>
        !string.IsNullOrWhiteSpace(_recommendationSnapshot.RecognizedCustomerText) ||
        _recommendationSnapshot.Product.HasValue;

    public bool HasRecognizedCustomerMessage => !string.IsNullOrWhiteSpace(_recommendationSnapshot.RecognizedCustomerText);

    public string RecognizedCustomerMessageText => string.IsNullOrWhiteSpace(_recommendationSnapshot.RecognizedCustomerText)
        ? string.Empty
        : $"识别内容：{BuildPreviewText(_recommendationSnapshot.RecognizedCustomerText, 120)}";

    public bool HasRecommendationPanelContent =>
        HasRecommendationStatusText ||
        HasRecommendationContext ||
        HasRecommendations ||
        HasKnowledgeCardMatches ||
        HasGeneratedReplies ||
        HasSimilarReplyGroups ||
        !string.IsNullOrWhiteSpace(AiDraftStatusMessage);

    public bool HasManualSearchText => SearchOverlayLayoutModeResolver.HasManualSearchText(SearchText);

    public bool ShouldShowRecommendationPanel =>
        SearchOverlayLayoutModeResolver.ShouldShowRecommendationPanel(SearchText, HasRecommendationPanelContent);

    public bool ShouldShowSearchResultsPanel =>
        SearchOverlayLayoutModeResolver.ShouldShowSearchResultsPanel(SearchText);

    public bool HasSimilarReplyGroups => SimilarReplyGroups.Count > 0;

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

    public int RecommendationDisplayCount => Math.Clamp(Settings.RecommendationDisplayCount, 1, 5);

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

    public string RecommendationHintText => SearchOverlayRecommendationSummaryBuilder.BuildRecommendationHintText(
        RecommendedPhrases,
        KnowledgeCards);

    public bool HasRecommendationHintText => !string.IsNullOrWhiteSpace(RecommendationHintText);

    public string RecommendationStatusText => _isSettingsPreviewMode
        ? "当前为搜索浮层预览，推荐短语与 AI 草稿不会在预览模式下实时刷新。"
        : SearchOverlayPanelStatusBuilder.BuildRecommendationStatusText(
            _recommendationSnapshot,
            RecommendedPhrases,
            KnowledgeCards,
            _lastAiDraftStatus,
            IsRefreshingRecommendations);

    public bool HasRecommendationStatusText => !string.IsNullOrWhiteSpace(RecommendationStatusText);

    public string AiDraftStatusMessage
    {
        get => _aiDraftStatusMessage;
        private set
        {
            if (SetProperty(ref _aiDraftStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasAiDraftStatusMessage));
                NotifyRecommendationPanelStateChanged();
            }
        }
    }

    public bool HasAiDraftStatusMessage => !string.IsNullOrWhiteSpace(AiDraftStatusMessage);

    public string GroundingSummaryText => SearchOverlayRecommendationSummaryBuilder.BuildGroundingSummaryText(
        RecommendedPhrases,
        KnowledgeCards);

    public bool HasGroundingSummaryText => !string.IsNullOrWhiteSpace(GroundingSummaryText);

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

    public string OverlayAssistText => HasActionStatusMessage ? ActionStatusMessage : StatusMessage;

    public bool HasOverlayAssistText =>
        SearchOverlayLayoutModeResolver.ShouldShowOverlayAssistText(SearchText, HasActionStatusMessage) &&
        !string.IsNullOrWhiteSpace(OverlayAssistText);

    public bool IsOverlayAssistWarning => HasActionStatusMessage && IsActionStatusWarning;

    public bool IsRefreshingRecommendations
    {
        get => _isRefreshingRecommendations;
        private set
        {
            if (SetProperty(ref _isRefreshingRecommendations, value))
            {
                NotifyRecommendationPanelStateChanged();
            }
        }
    }

    public string RecommendationContextText
    {
        get
        {
            var parts = new List<string>();
            if (_recommendationSnapshot.Product.HasValue)
            {
                var productSegments = new[]
                {
                    string.IsNullOrWhiteSpace(_recommendationSnapshot.RecognizedProductId)
                        ? string.Empty
                        : $"ID {_recommendationSnapshot.RecognizedProductId}",
                    string.IsNullOrWhiteSpace(_recommendationSnapshot.RecognizedProductName)
                        ? string.Empty
                        : _recommendationSnapshot.RecognizedProductName
                }.Where(segment => !string.IsNullOrWhiteSpace(segment));
                parts.Add($"\u8bc6\u522b\u5230\u7684\u5546\u54c1\uff1a{string.Join(" / ", productSegments)}");
            }

            if (!string.IsNullOrWhiteSpace(_recommendationSnapshot.RecognizedContextText))
            {
                parts.Add($"上下文：{BuildPreviewText(_recommendationSnapshot.RecognizedContextText, RecommendationContextPreviewMaxLength)}");
            }

            return string.Join("  |  ", parts);
        }
    }

    public bool HasRecommendationContextText => !string.IsNullOrWhiteSpace(RecommendationContextText);

    public string SearchBoxHintText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchText) && HasRecommendations && CurrentMode != SearchOverlayMode.File)
            {
                return RecommendationHintText;
            }

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
        Results.Count,
        HasRecommendationPanelContent);

    public bool HasResultsSectionSummary => !string.IsNullOrWhiteSpace(ResultsSectionSummary);

    public bool HasEmptyState => !HasResults;

    public string EmptyStateTitle => SearchOverlayPanelStatusBuilder.BuildEmptyStateTitle(CurrentMode, SearchText);

    public string EmptyStateHintText => SearchOverlayPanelStatusBuilder.BuildEmptyStateHintText(
        CurrentMode,
        SearchText,
        HasRecommendationPanelContent);

    private void NotifyRecommendationPanelStateChanged()
    {
        OnPropertyChanged(nameof(HasRecommendationPanelContent));
        OnPropertyChanged(nameof(ShouldShowRecommendationPanel));
        OnPropertyChanged(nameof(RecommendationStatusText));
        OnPropertyChanged(nameof(HasRecommendationStatusText));
    }

    private void NotifyRecommendationContextChanged()
    {
        OnPropertyChanged(nameof(HasRecommendationContext));
        OnPropertyChanged(nameof(HasRecognizedCustomerMessage));
        OnPropertyChanged(nameof(RecognizedCustomerMessageText));
        OnPropertyChanged(nameof(RecommendationContextText));
        OnPropertyChanged(nameof(HasRecommendationContextText));
    }

    public void PrepareForOpen()
    {
        Interlocked.Increment(ref _recommendationRefreshVersion);
        _isSettingsPreviewMode = false;
        DismissPendingAiDrafts();
        _recommendationSnapshot = new ConversationRecommendationSnapshot();
        _recommendationSessionId = Guid.Empty;
        _knowledgeCardSessionId = Guid.Empty;
        _aiDraftSessionId = Guid.Empty;
        _aiDraftInteracted = false;
        SimilarReplyGroups = Array.Empty<HistoricalReplyGroupSuggestion>();
        GeneratedReplies = Array.Empty<GeneratedReplyCandidate>();
        _lastAiDraftStatus = AiDraftGenerationStatus.Disabled(string.Empty);
        AiDraftStatusMessage = string.Empty;
        ClearActionStatus();
        KnowledgeCards = Array.Empty<KnowledgeCardSuggestion>();
        IsEditingRecognition = false;
        RecognitionCorrectionText = string.Empty;
        NotifyRecommendationContextChanged();
        OnPropertyChanged(nameof(RecommendationStatusText));
        OnPropertyChanged(nameof(HasRecommendationStatusText));
        CurrentMode = Settings.DefaultSearchOverlayMode;
        StatusMessage = CurrentMode switch
        {
            SearchOverlayMode.File => "文件模式，可按数字 1-9 或 Enter 直接发送文件。",
            SearchOverlayMode.Mixed => "混合模式，可按数字 1-9 或 Enter 发送当前选中结果；Ctrl+S 保存原文，Ctrl+Shift+S 发送并保存。",
            _ => "短语模式，可按数字 1-9 或 Enter 直接发送；Ctrl+S 保存原文，Ctrl+Shift+S 发送并保存。"
        };

        if (string.IsNullOrEmpty(SearchText))
        {
            RefreshResults();
        }
        else
        {
            SearchText = string.Empty;
        }
    }

    public Task RefreshRecommendationsForOpenAsync()
    {
        return RefreshRecommendationsAsync();
    }

    public void BeginSettingsPreview(SearchOverlayMode mode)
    {
        _isSettingsPreviewMode = true;
        DismissPendingAiDrafts();
        _recommendationSnapshot = new ConversationRecommendationSnapshot();
        _recommendationSessionId = Guid.Empty;
        _knowledgeCardSessionId = Guid.Empty;
        _aiDraftSessionId = Guid.Empty;
        _aiDraftInteracted = false;
        SimilarReplyGroups = Array.Empty<HistoricalReplyGroupSuggestion>();
        GeneratedReplies = Array.Empty<GeneratedReplyCandidate>();
        _lastAiDraftStatus = AiDraftGenerationStatus.Disabled(string.Empty);
        AiDraftStatusMessage = string.Empty;
        ClearActionStatus();
        KnowledgeCards = Array.Empty<KnowledgeCardSuggestion>();
        IsEditingRecognition = false;
        RecognitionCorrectionText = string.Empty;
        NotifyRecommendationContextChanged();
        OnPropertyChanged(nameof(RecommendationStatusText));
        OnPropertyChanged(nameof(HasRecommendationStatusText));
        RecommendedPhrases = Array.Empty<PhraseRecommendationSuggestion>();
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
        DismissPendingAiDrafts();
        _recommendationSnapshot = new ConversationRecommendationSnapshot();
        _recommendationSessionId = Guid.Empty;
        _knowledgeCardSessionId = Guid.Empty;
        _aiDraftSessionId = Guid.Empty;
        _aiDraftInteracted = false;
        SimilarReplyGroups = Array.Empty<HistoricalReplyGroupSuggestion>();
        GeneratedReplies = Array.Empty<GeneratedReplyCandidate>();
        _lastAiDraftStatus = AiDraftGenerationStatus.Disabled(string.Empty);
        AiDraftStatusMessage = string.Empty;
        ClearActionStatus();
        KnowledgeCards = Array.Empty<KnowledgeCardSuggestion>();
        IsEditingRecognition = false;
        RecognitionCorrectionText = string.Empty;
        NotifyRecommendationContextChanged();
        OnPropertyChanged(nameof(RecommendationStatusText));
        OnPropertyChanged(nameof(HasRecommendationStatusText));
        RecommendedPhrases = Array.Empty<PhraseRecommendationSuggestion>();
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
        StatusMessage = mode switch
        {
            SearchOverlayMode.File => "文件模式，可按数字 1-9 或 Enter 直接发送文件。",
            SearchOverlayMode.Mixed => "混合模式，可按数字 1-9 或 Enter 发送当前选中结果；Ctrl+S 保存原文，Ctrl+Shift+S 发送并保存。",
            _ => "短语模式，可按数字 1-9 或 Enter 直接发送；Ctrl+S 保存原文，Ctrl+Shift+S 发送并保存。"
        };
        RefreshResults();
        _ = RefreshRecommendationsAsync();
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

    public async Task<ReplyActionResult> SendRecommendedPhraseAsync(PhraseRecommendationSuggestion suggestion)
    {
        var result = await _mainViewModel.SendPhraseDirectAsync(
            suggestion.Phrase,
            "search_overlay_recommendation_send",
            SearchText,
            _recommendationSnapshot);

        _recommendationTrackingService.LogRecommendationAccepted(
            suggestion,
            _recommendationSessionId,
            CurrentMode,
            SearchText,
            ComposeActionMode.Send,
            result.Success);

        ShowActionStatus(result);
        return result;
    }

    public async Task<ReplyActionResult> InputRecommendedPhraseAsync(PhraseRecommendationSuggestion suggestion)
    {
        var result = await _mainViewModel.InputPhraseDirectAsync(
            suggestion.Phrase,
            "search_overlay_recommendation_input",
            SearchText,
            _recommendationSnapshot);

        _recommendationTrackingService.LogRecommendationAccepted(
            suggestion,
            _recommendationSessionId,
            CurrentMode,
            SearchText,
            ComposeActionMode.Input,
            result.Success);

        ShowActionStatus(result);
        return result;
    }

    public async Task<ReplyActionResult> SendGeneratedReplyAsync(GeneratedReplyCandidate draft)
    {
        _aiDraftInteracted = true;
        var result = await _mainViewModel.SendPlainTextAsync(
            draft.Text,
            "search_overlay_ai_draft_send",
            SearchText,
            _recommendationSnapshot);
        _recommendationTrackingService.LogAiDraftAction(
            draft,
            _aiDraftSessionId,
            CurrentMode,
            SearchText,
            "sent",
            ComposeActionMode.Send,
            result.Success);
        ShowActionStatus(result);
        return result;
    }

    public async Task<ReplyActionResult> InputGeneratedReplyAsync(GeneratedReplyCandidate draft)
    {
        _aiDraftInteracted = true;
        var result = await _mainViewModel.InputPlainTextAsync(
            draft.Text,
            "search_overlay_ai_draft_input",
            SearchText,
            _recommendationSnapshot);
        _recommendationTrackingService.LogAiDraftAction(
            draft,
            _aiDraftSessionId,
            CurrentMode,
            SearchText,
            "input",
            ComposeActionMode.Input,
            result.Success);
        ShowActionStatus(result);
        return result;
    }

    public async Task<ReplyActionResult> SendKnowledgeCardAsync(KnowledgeCardSuggestion knowledgeCard)
    {
        var result = await _mainViewModel.SendPlainTextAsync(
            knowledgeCard.Card.Answer,
            "search_overlay_knowledge_card_send",
            SearchText,
            _recommendationSnapshot);

        _recommendationTrackingService.LogKnowledgeCardAccepted(
            knowledgeCard,
            _knowledgeCardSessionId,
            CurrentMode,
            SearchText,
            ComposeActionMode.Send,
            result.Success);

        ShowActionStatus(result);
        return result;
    }

    public async Task<ReplyActionResult> InputKnowledgeCardAsync(KnowledgeCardSuggestion knowledgeCard)
    {
        var result = await _mainViewModel.InputPlainTextAsync(
            knowledgeCard.Card.Answer,
            "search_overlay_knowledge_card_input",
            SearchText,
            _recommendationSnapshot);

        _recommendationTrackingService.LogKnowledgeCardAccepted(
            knowledgeCard,
            _knowledgeCardSessionId,
            CurrentMode,
            SearchText,
            ComposeActionMode.Input,
            result.Success);

        ShowActionStatus(result);
        return result;
    }

    public async Task<ReplyActionResult> SendSearchTextAsync()
    {
        var result = await _mainViewModel.SendPlainTextAsync(
            SearchText,
            "search_overlay_plain_text_send",
            SearchText,
            _recommendationSnapshot);

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
            pendingSearchText,
            _recommendationSnapshot);

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

    public void BeginRecognitionCorrection()
    {
        RecognitionCorrectionText = _recommendationSnapshot.RecognizedCustomerText;
        IsEditingRecognition = true;
    }

    public void CancelRecognitionCorrection()
    {
        IsEditingRecognition = false;
        RecognitionCorrectionText = _recommendationSnapshot.RecognizedCustomerText;
    }

    public async Task SaveRecognitionCorrectionAsync()
    {
        var correctedText = RecognitionCorrectionText.Trim();
        if (string.IsNullOrWhiteSpace(correctedText))
        {
            StatusMessage = "修正后的客户消息不能为空。";
            return;
        }

        await _recommendationTrackingService.SaveRecognitionCorrectionAsync(
            _recommendationSessionId,
            _recommendationSnapshot,
            new ConversationRecognitionCorrection
            {
                CustomerText = correctedText,
                ContextText = _recommendationSnapshot.RecognizedContextText,
                ProductId = _recommendationSnapshot.RecognizedProductId,
                ProductName = _recommendationSnapshot.RecognizedProductName,
                Messages = _recommendationSnapshot.Messages
            });
        _recommendationSnapshot = new ConversationRecommendationSnapshot
        {
            PlatformHint = _recommendationSnapshot.PlatformHint,
            WindowTitle = _recommendationSnapshot.WindowTitle,
            WindowClassName = _recommendationSnapshot.WindowClassName,
            ProcessName = _recommendationSnapshot.ProcessName,
            ProcessPath = _recommendationSnapshot.ProcessPath,
            CaptureStatus = _recommendationSnapshot.CaptureStatus,
            CapturedText = _recommendationSnapshot.CapturedText,
            RecognitionStatus = _recommendationSnapshot.RecognitionStatus,
            RecognitionMethod = _recommendationSnapshot.RecognitionMethod,
            RecognitionConfidence = _recommendationSnapshot.RecognitionConfidence,
            RecognizedCustomerText = correctedText,
            RecognizedContextText = _recommendationSnapshot.RecognizedContextText,
            Product = _recommendationSnapshot.Product,
            Messages = _recommendationSnapshot.Messages,
            Keywords = RecommendationKeywordBuilder.Build(
                correctedText,
                _recommendationSnapshot.RecognizedContextText,
                _recommendationSnapshot.RecognizedProductId,
                _recommendationSnapshot.RecognizedProductName)
        };

        IsEditingRecognition = false;
        NotifyRecommendationContextChanged();
        OnPropertyChanged(nameof(RecommendationStatusText));
        OnPropertyChanged(nameof(HasRecommendationStatusText));
        StatusMessage = "已记录这次人工修正，后续会优先用于训练数据。";
    }

    public IReadOnlyList<string> GetPhraseImageAbsolutePaths(ReplyPhrase phrase)
    {
        return _mainViewModel.GetPhraseImageAbsolutePaths(phrase);
    }

    public ConversationRecognitionCorrection CreateRecognitionCorrectionDraft()
    {
        return new ConversationRecognitionCorrection
        {
            CustomerText = _recommendationSnapshot.RecognizedCustomerText,
            ContextText = _recommendationSnapshot.RecognizedContextText,
            ProductId = _recommendationSnapshot.RecognizedProductId,
            ProductName = _recommendationSnapshot.RecognizedProductName,
            Messages = _recommendationSnapshot.Messages
        };
    }

    public async Task SaveRecognitionCorrectionAsync(ConversationRecognitionCorrection correction)
    {
        var normalizedCorrection = NormalizeRecognitionCorrection(correction);
        if (!HasRecognitionCorrectionChanges(normalizedCorrection))
        {
            StatusMessage = "未检测到新的修正内容。";
            return;
        }

        await _recommendationTrackingService.SaveRecognitionCorrectionAsync(
            _recommendationSessionId,
            _recommendationSnapshot,
            normalizedCorrection);

        _recommendationSnapshot = new ConversationRecommendationSnapshot
        {
            PlatformHint = _recommendationSnapshot.PlatformHint,
            WindowTitle = _recommendationSnapshot.WindowTitle,
            WindowClassName = _recommendationSnapshot.WindowClassName,
            ProcessName = _recommendationSnapshot.ProcessName,
            ProcessPath = _recommendationSnapshot.ProcessPath,
            CaptureStatus = _recommendationSnapshot.CaptureStatus,
            CapturedText = _recommendationSnapshot.CapturedText,
            RecognitionStatus = _recommendationSnapshot.RecognitionStatus,
            RecognitionMethod = _recommendationSnapshot.RecognitionMethod,
            RecognitionConfidence = _recommendationSnapshot.RecognitionConfidence,
            RecognizedCustomerText = normalizedCorrection.CustomerText,
            RecognizedContextText = normalizedCorrection.ContextText,
            Product = new ConversationProductContext
            {
                ProductId = normalizedCorrection.ProductId,
                ProductName = normalizedCorrection.ProductName
            },
            Messages = normalizedCorrection.Messages,
            Keywords = RecommendationKeywordBuilder.Build(
                normalizedCorrection.CustomerText,
                normalizedCorrection.ContextText,
                normalizedCorrection.ProductId,
                normalizedCorrection.ProductName)
        };

        RecognitionCorrectionText = normalizedCorrection.CustomerText;
        IsEditingRecognition = false;
        NotifyRecommendationContextChanged();
        OnPropertyChanged(nameof(RecommendationStatusText));
        OnPropertyChanged(nameof(HasRecommendationStatusText));
        StatusMessage = "已记录这次人工修正，后续会优先用于训练数据。";
    }

    private bool HasRecognitionCorrectionChanges(ConversationRecognitionCorrection correction)
    {
        return !string.Equals(correction.CustomerText, _recommendationSnapshot.RecognizedCustomerText, StringComparison.Ordinal) ||
               !string.Equals(correction.ContextText, _recommendationSnapshot.RecognizedContextText, StringComparison.Ordinal) ||
               !string.Equals(correction.ProductId, _recommendationSnapshot.RecognizedProductId, StringComparison.Ordinal) ||
               !string.Equals(correction.ProductName, _recommendationSnapshot.RecognizedProductName, StringComparison.Ordinal) ||
               !AreMessagesEquivalent(correction.Messages, _recommendationSnapshot.Messages);
    }

    private static ConversationRecognitionCorrection NormalizeRecognitionCorrection(ConversationRecognitionCorrection correction)
    {
        var customerLines = SplitCorrectionLines(correction.CustomerText);
        var contextLines = SplitCorrectionLines(correction.ContextText);
        var customerLineSet = customerLines.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contextLineSet = contextLines.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var messages = correction.Messages
            .Where(message => !string.IsNullOrWhiteSpace(message.Text))
            .Select(message => message.Text.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct()
            .Select((text, index) => new ConversationMessageTurn
            {
                Index = index,
                Text = text,
                Score = 0d,
                Reason = "manual_correction",
                SelectedForCustomerText = customerLineSet.Contains(text),
                IncludedInContext = contextLineSet.Contains(text)
            })
            .ToArray();

        return new ConversationRecognitionCorrection
        {
            CustomerText = string.Join(Environment.NewLine, customerLines),
            ContextText = string.Join(Environment.NewLine, contextLines),
            ProductId = correction.ProductId.Trim(),
            ProductName = correction.ProductName.Trim(),
            Messages = messages
        };
    }

    private static IReadOnlyList<string> SplitCorrectionLines(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
    }

    private static bool AreMessagesEquivalent(
        IReadOnlyList<ConversationMessageTurn> left,
        IReadOnlyList<ConversationMessageTurn> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Text, right[index].Text, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
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

            result = await _mainViewModel.SendPhraseDirectAsync(item.Phrase, "search_overlay_phrase_send", SearchText, _recommendationSnapshot);
            TrackRecommendationAcceptance(item, ComposeActionMode.Send, result.Success);
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
            TrackRecommendationAcceptance(item, ComposeActionMode.Copy, result.Success);
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
        TrackRecommendationAcceptance(item, ComposeActionMode.Copy, result.Success);
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
            TrackRecommendationAcceptance(item, ComposeActionMode.Input, result.Success);
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

    private Task RefreshRecommendationsAsync()
    {
        var refreshVersion = Interlocked.Increment(ref _recommendationRefreshVersion);
        IsRefreshingRecommendations = true;
        return RefreshRecommendationsCoreAsync(refreshVersion);
    }

    private async Task RefreshRecommendationsCoreAsync(int refreshVersion)
    {
        try
        {
            if (_isSettingsPreviewMode)
            {
                if (refreshVersion != _recommendationRefreshVersion)
                {
                    return;
                }

                _recommendationSnapshot = new ConversationRecommendationSnapshot();
                _recommendationSessionId = Guid.Empty;
                _knowledgeCardSessionId = Guid.Empty;
                _aiDraftSessionId = Guid.Empty;
                _aiDraftInteracted = false;
                SimilarReplyGroups = Array.Empty<HistoricalReplyGroupSuggestion>();
                GeneratedReplies = Array.Empty<GeneratedReplyCandidate>();
                _lastAiDraftStatus = AiDraftGenerationStatus.Disabled(string.Empty);
                AiDraftStatusMessage = string.Empty;
                IsEditingRecognition = false;
                RecognitionCorrectionText = string.Empty;
                NotifyRecommendationContextChanged();
                OnPropertyChanged(nameof(RecommendationStatusText));
                OnPropertyChanged(nameof(HasRecommendationStatusText));
                RecommendedPhrases = Array.Empty<PhraseRecommendationSuggestion>();
                RefreshResults();
                return;
            }

            var context = await _recommendationOrchestrator.GetRecommendationContextAsync(new RecommendationRequest
            {
                DisplayCount = RecommendationDisplayCount,
                MinimumSampleCount = MinimumRecommendationSampleCount
            });
            if (refreshVersion != _recommendationRefreshVersion || _isSettingsPreviewMode)
            {
                return;
            }

            _recommendationSnapshot = context.Snapshot;
            SimilarReplyGroups = context.Suggestions.Count == 0
                ? Array.Empty<HistoricalReplyGroupSuggestion>()
                : context.SimilarReplyGroups;
            GeneratedReplies = context.GeneratedReplies;
            KnowledgeCards = context.KnowledgeCards;
            _lastAiDraftStatus = context.AiDraftStatus;
            AiDraftStatusMessage = SearchOverlayRecommendationSummaryBuilder.BuildAiDraftStatusMessage(context.AiDraftStatus);
            IsEditingRecognition = false;
            RecognitionCorrectionText = _recommendationSnapshot.RecognizedCustomerText;
            NotifyRecommendationContextChanged();
            OnPropertyChanged(nameof(RecommendationStatusText));
            OnPropertyChanged(nameof(HasRecommendationStatusText));
            RecommendedPhrases = context.Suggestions;
            _recommendationSessionId = RecommendedPhrases.Count == 0
                ? Guid.Empty
                : _recommendationTrackingService.LogRecommendationsShown(
                    context.Snapshot,
                    context.Suggestions,
                    CurrentMode,
                    SearchText);
            _knowledgeCardSessionId = KnowledgeCards.Count == 0
                ? Guid.Empty
                : _recommendationTrackingService.LogKnowledgeCardsShown(
                    context.Snapshot,
                    KnowledgeCards,
                    CurrentMode,
                    SearchText);
            _aiDraftSessionId = GeneratedReplies.Count == 0
                ? Guid.Empty
                : _recommendationTrackingService.LogAiDraftsShown(
                    context.Snapshot,
                    GeneratedReplies,
                    CurrentMode,
                    SearchText);
            _aiDraftInteracted = false;
            if (GeneratedReplies.Count == 0 && context.AiDraftStatus.ShouldSurfaceStatus)
            {
                _ = _recommendationTrackingService.LogAiDraftStatusAsync(
                    context.Snapshot,
                    CurrentMode,
                    SearchText,
                    context.AiDraftStatus);
            }
            RefreshResults();
        }
        finally
        {
            if (refreshVersion == _recommendationRefreshVersion)
            {
                IsRefreshingRecommendations = false;
            }
        }
    }

    public void DismissPendingAiDrafts()
    {
        if (_aiDraftSessionId == Guid.Empty || _aiDraftInteracted || GeneratedReplies.Count == 0)
        {
            return;
        }

        foreach (var draft in GeneratedReplies)
        {
            _recommendationTrackingService.LogAiDraftAction(
                draft,
                _aiDraftSessionId,
                CurrentMode,
                SearchText,
                "dismissed",
                ComposeActionMode.Copy,
                resultSuccess: true);
        }

        _aiDraftInteracted = true;
    }

    private void TrackRecommendationAcceptance(SearchOverlayResultItem item, ComposeActionMode actionMode, bool resultSuccess)
    {
        if (item.Phrase is null || _recommendationSessionId == Guid.Empty)
        {
            return;
        }

        var suggestion = RecommendedPhrases.FirstOrDefault(entry => entry.Phrase.Id == item.Phrase.Id);
        if (suggestion is null)
        {
            return;
        }

        _recommendationTrackingService.LogRecommendationAccepted(
            suggestion,
            _recommendationSessionId,
            CurrentMode,
            SearchText,
            actionMode,
            resultSuccess);
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

        if (e.PropertyName == nameof(AppSettings.RecommendationDisplayCount))
        {
            OnPropertyChanged(nameof(RecommendationDisplayCount));
            OnPropertyChanged(nameof(RecommendationItems));
            _ = RefreshRecommendationsAsync();
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

        var recommendationMap = RecommendedPhrases.ToDictionary(item => item.Phrase.Id, item => item);
        IReadOnlyList<ReplyPhrase> phrases = _mainViewModel.SearchPhrases(keyword, Math.Max(maxResults * 2, 12));

        return phrases
            .OrderByDescending(phrase => recommendationMap.ContainsKey(phrase.Id))
            .ThenByDescending(phrase => recommendationMap.TryGetValue(phrase.Id, out var suggestion) ? suggestion.Confidence : 0d)
            .ThenByDescending(phrase => _mainViewModel.GetPhraseSendCount(phrase.Id))
            .ThenByDescending(phrase => _mainViewModel.GetPhraseLastSentAt(phrase.Id) ?? DateTimeOffset.MinValue)
            .Take(maxResults)
            .Select((phrase, index) => new SearchOverlayResultItem
            {
                Number = index + 1,
                Mode = SearchOverlayMode.Phrase,
                Phrase = phrase,
                IsRecommended = recommendationMap.ContainsKey(phrase.Id),
                RecommendationBadgeText = recommendationMap.TryGetValue(phrase.Id, out var suggestion) ? "推荐" : string.Empty,
                RecommendationDetailText = recommendationMap.TryGetValue(phrase.Id, out suggestion) ? suggestion.ReasonText : string.Empty
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
                    PreviewHasLabel = true,
                    IsRecommended = true,
                    RecommendationBadgeText = "推荐"
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
                    PreviewHasLabel = true,
                    IsRecommended = true,
                    RecommendationBadgeText = "推荐"
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
        var recommendationMap = RecommendedPhrases.ToDictionary(item => item.Phrase.Id, item => item);

        var phraseCandidates = _mainViewModel
            .SearchPhrases(normalizedKeyword, candidateCount)
            .Select((phrase, index) => new
            {
                Score = ComputePhraseMixedScore(phrase, normalizedKeyword, index) +
                        (recommendationMap.TryGetValue(phrase.Id, out var suggestion) ? suggestion.Confidence * 60d : 0d),
                Item = new SearchOverlayResultItem
                {
                    Number = 0,
                    Mode = SearchOverlayMode.Phrase,
                    Phrase = phrase,
                    IsRecommended = recommendationMap.ContainsKey(phrase.Id),
                    RecommendationBadgeText = recommendationMap.TryGetValue(phrase.Id, out suggestion) ? "推荐" : string.Empty,
                    RecommendationDetailText = recommendationMap.TryGetValue(phrase.Id, out suggestion) ? suggestion.ReasonText : string.Empty
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

    private static double ComputeRecentBoost(DateTimeOffset? lastUsedAt)
    {
        if (lastUsedAt is null)
        {
            return 0;
        }

        var days = Math.Max(0, (DateTimeOffset.Now - lastUsedAt.Value).TotalDays);
        return Math.Max(0, 18 - days);
    }

    private static string BuildPreviewText(string text, int maxLength)
    {
        var normalized = text.Trim().Replace("\r", " ").Replace("\n", " ");
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return $"{normalized[..maxLength]}...";
    }
}
