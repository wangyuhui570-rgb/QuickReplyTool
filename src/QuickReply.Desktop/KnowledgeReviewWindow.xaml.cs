using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop;

public partial class KnowledgeReviewWindow : Window, INotifyPropertyChanged
{
    private static readonly string ReviewerIdentity = $"{Environment.UserName}@{Environment.MachineName}";
    private readonly KnowledgeReviewClientService _clientService;
    private readonly string _serverBaseUrl;
    private readonly string _apiToken;
    private readonly string _reviewSessionId = Guid.NewGuid().ToString("N");

    private bool _isBusy;
    private ReviewCandidateListItem? _selectedCandidate;
    private string _headerSummary = "正在读取待审核知识候选...";
    private string _queueSummary = "待审核候选：0 条";
    private string _endpointSummary = string.Empty;
    private string _detailTitle = "请选择左侧候选";
    private string _detailMetaSummary = "右侧会显示候选答案、标签和来源样本。";
    private string _answerFinalText = string.Empty;
    private string _synonymsText = string.Empty;
    private string _tagsText = string.Empty;
    private string _usageNotesText = string.Empty;
    private string _reviewCommentText = string.Empty;
    private string _versionText = "v1";
    private string _rejectReasonText = string.Empty;
    private string _operationSummary = "可以先选中一条候选，必要时修改答案，再决定通过或拒绝。";
    private string _historySummary = "审核历史：0 条";
    private string _selectedHistoryFilterKey = KnowledgeReviewHistoryFilter.All;
    private readonly HashSet<Guid> _selectedPublishedMemberIds = [];
    private List<KnowledgeReviewHistoryEventRecord> _allHistoryRecords = [];

    public KnowledgeReviewWindow(string serverBaseUrl, string apiToken, KnowledgeReviewClientService? clientService = null)
    {
        InitializeComponent();
        _serverBaseUrl = serverBaseUrl;
        _apiToken = apiToken;
        _clientService = clientService ?? new KnowledgeReviewClientService();
        EndpointSummary = KnowledgeReviewClientService.DescribeEndpointPreference(serverBaseUrl);
        DataContext = this;
        Loaded += KnowledgeReviewWindow_Loaded;
    }

    public ObservableCollection<ReviewCandidateListItem> CandidateItems { get; } = [];
    public ObservableCollection<ReviewSourceListItem> SourceItems { get; } = [];
    public ObservableCollection<ReviewHistoryListItem> HistoryItems { get; } = [];
    public IReadOnlyList<HistoryFilterOption> HistoryFilterOptions { get; } =
    [
        new(KnowledgeReviewHistoryFilter.All, "全部历史"),
        new(KnowledgeReviewHistoryFilter.Approved, "仅已发布"),
        new(KnowledgeReviewHistoryFilter.Rejected, "仅已拒绝"),
        new(KnowledgeReviewHistoryFilter.Unpublished, "仅已下线")
    ];

    public string HeaderSummary
    {
        get => _headerSummary;
        private set => SetField(ref _headerSummary, value);
    }

    public string QueueSummary
    {
        get => _queueSummary;
        private set => SetField(ref _queueSummary, value);
    }

    public string EndpointSummary
    {
        get => _endpointSummary;
        private set => SetField(ref _endpointSummary, value);
    }

    public ReviewCandidateListItem? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (!SetField(ref _selectedCandidate, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelection));
            _ = LoadCandidateDetailsAsync(value);
        }
    }

    public bool HasSelection => SelectedCandidate is not null && !_isBusy;

    public bool CanUnpublishSelection => HasSelection && _selectedPublishedMemberIds.Count > 0;

    public string DetailTitle
    {
        get => _detailTitle;
        private set => SetField(ref _detailTitle, value);
    }

    public string DetailMetaSummary
    {
        get => _detailMetaSummary;
        private set => SetField(ref _detailMetaSummary, value);
    }

    public string AnswerFinalText
    {
        get => _answerFinalText;
        set => SetField(ref _answerFinalText, value);
    }

    public string SynonymsText
    {
        get => _synonymsText;
        set => SetField(ref _synonymsText, value);
    }

    public string TagsText
    {
        get => _tagsText;
        set => SetField(ref _tagsText, value);
    }

    public string UsageNotesText
    {
        get => _usageNotesText;
        set => SetField(ref _usageNotesText, value);
    }

    public string ReviewCommentText
    {
        get => _reviewCommentText;
        set => SetField(ref _reviewCommentText, value);
    }

    public string VersionText
    {
        get => _versionText;
        set => SetField(ref _versionText, value);
    }

    public string RejectReasonText
    {
        get => _rejectReasonText;
        set => SetField(ref _rejectReasonText, value);
    }

    public string OperationSummary
    {
        get => _operationSummary;
        private set => SetField(ref _operationSummary, value);
    }

    public string HistorySummary
    {
        get => _historySummary;
        private set => SetField(ref _historySummary, value);
    }

    public string SelectedHistoryFilterKey
    {
        get => _selectedHistoryFilterKey;
        set
        {
            var normalized = KnowledgeReviewHistoryFilter.NormalizeFilterKey(value);
            if (!SetField(ref _selectedHistoryFilterKey, normalized))
            {
                return;
            }

            ApplyHistoryFilter();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void KnowledgeReviewWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= KnowledgeReviewWindow_Loaded;
        await RefreshQueueAsync();
    }

    private async Task RefreshQueueAsync()
    {
        await RunBusyAsync(
            "正在读取待审核候选...",
            () => RefreshQueueCoreAsync());
    }

    private async Task RefreshQueueCoreAsync(string? completionMessage = null)
    {
        var selectedCandidateId = SelectedCandidate?.Id;
        var queue = await _clientService.GetReviewQueueAsync(_serverBaseUrl, _apiToken, CancellationToken.None);
        var groupedQueue = KnowledgeReviewQueueGrouper.Group(queue);

        CandidateItems.Clear();
        foreach (var group in groupedQueue)
        {
            CandidateItems.Add(new ReviewCandidateListItem(group));
        }

        QueueSummary = $"待审核候选：{CandidateItems.Count} 组（已按平台/商品谨慎合并）";
        HeaderSummary = CandidateItems.Count == 0
            ? "当前没有待审核知识候选。"
            : $"已加载 {CandidateItems.Count} 组待审核知识候选。";
        OperationSummary = completionMessage ?? (CandidateItems.Count == 0
            ? "当前队列为空。后续可先跑导入和综合任务，再回来审核。"
            : "先看右侧来源样本，再决定通过发布或拒绝。");

        if (CandidateItems.Count == 0)
        {
            SelectedCandidate = null;
            SourceItems.Clear();
            HistoryItems.Clear();
            _allHistoryRecords = [];
            _selectedPublishedMemberIds.Clear();
            OnPropertyChanged(nameof(CanUnpublishSelection));
            DetailTitle = "当前没有待审核候选";
            DetailMetaSummary = "等导入和综合任务产出新的 reviewed/draft 候选后，这里会显示。";
            HistorySummary = "审核历史：0 条";
            return;
        }

        var selectedAfterRefresh = selectedCandidateId is not null
            ? CandidateItems.FirstOrDefault(item => item.MemberIds.Contains(selectedCandidateId.Value))
            : null;
        SelectedCandidate = selectedAfterRefresh ?? CandidateItems[0];
    }

    private async Task LoadCandidateDetailsAsync(ReviewCandidateListItem? candidate)
    {
        if (candidate is null)
        {
            SourceItems.Clear();
            HistoryItems.Clear();
            _allHistoryRecords = [];
            _selectedPublishedMemberIds.Clear();
            OnPropertyChanged(nameof(CanUnpublishSelection));
            DetailTitle = "请选择左侧候选";
            DetailMetaSummary = "右侧会显示候选答案、标签和来源样本。";
            AnswerFinalText = string.Empty;
            SynonymsText = string.Empty;
            TagsText = string.Empty;
            UsageNotesText = string.Empty;
            ReviewCommentText = string.Empty;
            RejectReasonText = string.Empty;
            VersionText = "v1";
            HistorySummary = "审核历史：0 条";
            return;
        }

        DetailTitle = candidate.Question;
        DetailMetaSummary = candidate.MetaSummary;
        AnswerFinalText = candidate.AnswerDraft;
        SynonymsText = string.Empty;
        TagsText = string.Join(", ", candidate.Tags);
        UsageNotesText = string.Empty;
        ReviewCommentText = string.Empty;
        RejectReasonText = string.Empty;
        VersionText = "v1";
        OperationSummary = $"当前正在审核：{candidate.Question}";
        _selectedPublishedMemberIds.Clear();
        OnPropertyChanged(nameof(CanUnpublishSelection));

        try
        {
            var sourceMap = new Dictionary<string, ReviewSourceListItem>(StringComparer.Ordinal);
            var historyMap = new Dictionary<Guid, KnowledgeReviewHistoryEventRecord>();
            foreach (var memberId in candidate.MemberIds)
            {
                var sources = await _clientService.GetReviewSourcesAsync(_serverBaseUrl, _apiToken, memberId, CancellationToken.None);
                foreach (var source in sources)
                {
                    var key = $"{source.SourceType}|{source.SourceId}|{source.Excerpt}";
                    sourceMap.TryAdd(key, new ReviewSourceListItem(source));
                }

                var history = await _clientService.GetReviewHistoryAsync(_serverBaseUrl, _apiToken, memberId, CancellationToken.None);
                foreach (var record in history)
                {
                    historyMap.TryAdd(record.Id, record);
                }

                var latestAction = history
                    .OrderByDescending(item => item.CreatedAt)
                    .Select(item => item.ActionType)
                    .FirstOrDefault();
                if (string.Equals(latestAction, "approved", StringComparison.OrdinalIgnoreCase))
                {
                    _selectedPublishedMemberIds.Add(memberId);
                }
            }

            SourceItems.Clear();
            foreach (var source in sourceMap.Values)
            {
                SourceItems.Add(source);
            }

            _allHistoryRecords = historyMap.Values
                .OrderByDescending(item => item.CreatedAt)
                .ToList();
            ApplyHistoryFilter();
            OnPropertyChanged(nameof(CanUnpublishSelection));
        }
        catch (Exception ex)
        {
            SourceItems.Clear();
            SourceItems.Add(new ReviewSourceListItem("读取来源失败", ex.Message));
            HistoryItems.Clear();
            _allHistoryRecords = [];
            HistoryItems.Add(new ReviewHistoryListItem("读取审核历史失败", ex.Message));
            _selectedPublishedMemberIds.Clear();
            OnPropertyChanged(nameof(CanUnpublishSelection));
            HistorySummary = "审核历史读取失败";
        }
    }

    private async Task RunBusyAsync(string busyMessage, Func<Task> action)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanUnpublishSelection));
        OperationSummary = busyMessage;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            OperationSummary = ex.Message;
            System.Windows.MessageBox.Show(this, ex.Message, "知识候选审核", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isBusy = false;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanUnpublishSelection));
        }
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(
            "正在刷新待审核候选...",
            () => RefreshQueueCoreAsync("已刷新待审核候选。"));
    }

    private void HistoryFilterComboBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyHistoryFilter();
    }

    private void QueueListBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (QueueListBox.SelectedItem is ReviewCandidateListItem item)
        {
            SelectedCandidate = item;
        }
    }

    private async void ApproveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidate is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(AnswerFinalText))
        {
            System.Windows.MessageBox.Show(this, "请先填写通过后要发布的答案。", "知识候选审核", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var affectedCount = SelectedCandidate.MemberIds.Count;
        var candidateLabel = SelectedCandidate.Question;
        await RunBusyAsync(
            "正在通过并发布候选...",
            async () =>
            {
                foreach (var memberId in SelectedCandidate.MemberIds)
                {
                    await _clientService.ApproveAsync(
                        _serverBaseUrl,
                        _apiToken,
                        memberId,
                        new ApproveKnowledgeCandidateRequest
                        {
                            AnswerFinal = AnswerFinalText.Trim(),
                            Synonyms = ParseCommaSeparated(SynonymsText),
                            Tags = ParseCommaSeparated(TagsText),
                            UsageNotes = UsageNotesText.Trim(),
                            ReviewComment = ReviewCommentText.Trim(),
                            Version = string.IsNullOrWhiteSpace(VersionText) ? "v1" : VersionText.Trim(),
                            Reviewer = ReviewerIdentity,
                            ReviewSessionId = _reviewSessionId
                        },
                        CancellationToken.None);
                }

                await RefreshQueueCoreAsync($"已通过并发布《{candidateLabel}》{(affectedCount > 1 ? $"，并同步处理 {affectedCount} 条相近候选。" : "。")}");
            });
    }

    private async void RejectButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidate is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(RejectReasonText))
        {
            System.Windows.MessageBox.Show(this, "请先填写拒绝原因。", "知识候选审核", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var affectedCount = SelectedCandidate.MemberIds.Count;
        var candidateLabel = SelectedCandidate.Question;
        await RunBusyAsync(
            "正在拒绝当前候选...",
            async () =>
            {
                foreach (var memberId in SelectedCandidate.MemberIds)
                {
                    await _clientService.RejectAsync(
                        _serverBaseUrl,
                        _apiToken,
                        memberId,
                        new RejectKnowledgeCandidateRequest
                        {
                            Reason = RejectReasonText.Trim(),
                            ReviewComment = ReviewCommentText.Trim(),
                            Reviewer = ReviewerIdentity,
                            ReviewSessionId = _reviewSessionId
                        },
                        CancellationToken.None);
                }

                await RefreshQueueCoreAsync($"已拒绝《{candidateLabel}》{(affectedCount > 1 ? $"，并同步处理 {affectedCount} 条相近候选。" : "。")}");
            });
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void UnpublishButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidate is null || _selectedPublishedMemberIds.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(RejectReasonText))
        {
            System.Windows.MessageBox.Show(this, "下线已发布知识前，请先填写原因。", "知识候选审核", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var affectedCount = _selectedPublishedMemberIds.Count;
        var candidateLabel = SelectedCandidate.Question;
        await RunBusyAsync(
            "正在下线已发布知识...",
            async () =>
            {
                foreach (var memberId in _selectedPublishedMemberIds)
                {
                    await _clientService.RemovePublishedAsync(
                        _serverBaseUrl,
                        _apiToken,
                        memberId,
                        new RemovePublishedKnowledgeCardRequest
                        {
                            Reason = RejectReasonText.Trim(),
                            ReviewComment = ReviewCommentText.Trim(),
                            Reviewer = ReviewerIdentity,
                            ReviewSessionId = _reviewSessionId
                        },
                        CancellationToken.None);
                }

                await RefreshQueueCoreAsync($"已下线《{candidateLabel}》对应的已发布知识{(affectedCount > 1 ? $"，共处理 {affectedCount} 条。" : "。")}");
            });
    }

    private static List<string> ParseCommaSeparated(string text)
    {
        return text
            .Split([',', '，', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ApplyHistoryFilter()
    {
        var stats = KnowledgeReviewHistoryFilter.BuildStats(_allHistoryRecords);
        var filteredItems = KnowledgeReviewHistoryFilter
            .Apply(_allHistoryRecords, SelectedHistoryFilterKey)
            .Select(item => new ReviewHistoryListItem(item))
            .ToArray();

        HistoryItems.Clear();
        foreach (var historyItem in filteredItems)
        {
            HistoryItems.Add(historyItem);
        }

        var filterLabel = HistoryFilterOptions.FirstOrDefault(item =>
            string.Equals(item.Key, SelectedHistoryFilterKey, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? "全部历史";
        HistorySummary =
            $"审核历史：{filteredItems.Length} / {stats.TotalCount} 条（当前筛选：{filterLabel}｜已发布 {stats.ApprovedCount}｜已拒绝 {stats.RejectedCount}｜已下线 {stats.UnpublishedCount}）";
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
    }

    public sealed class ReviewCandidateListItem
    {
        public ReviewCandidateListItem(KnowledgeReviewQueueGroup group)
        {
            var item = group.Primary;
            Id = item.Id;
            MemberIds = group.Members.Select(member => member.Id).ToArray();
            Status = item.Status;
            CandidateKind = item.CandidateKind;
            Question = item.QuestionNormalized;
            AnswerDraft = item.AnswerDraft;
            Tags = item.Tags;
            DuplicateCount = group.Members.Count;
            MetaSummary =
                $"类型：{GetCandidateKindDisplayName(item.CandidateKind)}｜平台：{BuildPlatformSummary(group.Platforms)}｜商品：{BuildProductSummary(group.ProductKeys)}｜置信度：{item.Confidence:0.00}｜来源：{group.CombinedSourceCount} 条" +
                (DuplicateCount > 1 ? $"｜合并：{DuplicateCount} 条同平台/同商品相近候选" : string.Empty);
            StatusText = item.Status;
            AnswerPreview = item.AnswerDraft.Length <= 90 ? item.AnswerDraft : $"{item.AnswerDraft[..90]}…";
        }

        public Guid Id { get; }
        public IReadOnlyList<Guid> MemberIds { get; }
        public string Status { get; }
        public string CandidateKind { get; }
        public string Question { get; }
        public string AnswerDraft { get; }
        public List<string> Tags { get; }
        public int DuplicateCount { get; }
        public string MetaSummary { get; }
        public string StatusText { get; }
        public string AnswerPreview { get; }

        private static string BuildProductSummary(IReadOnlyList<string> productKeys)
        {
            if (productKeys.Count == 0)
            {
                return "未标注";
            }

            if (productKeys.Count == 1)
            {
                return productKeys[0];
            }

            return $"{productKeys[0]} 等 {productKeys.Count} 个商品";
        }

        private static string BuildPlatformSummary(IReadOnlyList<string> platforms)
        {
            if (platforms.Count == 0)
            {
                return "unknown";
            }

            if (platforms.Count == 1)
            {
                return platforms[0];
            }

            return $"{platforms[0]} 等 {platforms.Count} 个平台";
        }

        private static string GetCandidateKindDisplayName(string candidateKind)
        {
            return string.Equals(candidateKind, "resource", StringComparison.OrdinalIgnoreCase)
                ? "资料卡"
                : "问答卡";
        }
    }

    public sealed class ReviewSourceListItem
    {
        public ReviewSourceListItem(KnowledgeReviewSourceRecord source)
        {
            SourceHeader = $"{source.SourceType} / {source.SourceId}";
            Excerpt = source.Excerpt;
        }

        public ReviewSourceListItem(string header, string excerpt)
        {
            SourceHeader = header;
            Excerpt = excerpt;
        }

        public string SourceHeader { get; }
        public string Excerpt { get; }
    }

    public sealed class ReviewHistoryListItem
    {
        public ReviewHistoryListItem(KnowledgeReviewHistoryEventRecord record)
        {
            var actionType = record.ActionType?.Trim().ToLowerInvariant() ?? string.Empty;
            ActionText = actionType switch
            {
                "approved" => "审核通过 / 已发布",
                "rejected" => "审核拒绝",
                "unpublished" => "已下线发布",
                _ => string.IsNullOrWhiteSpace(record.ActionType) ? "审核记录" : record.ActionType
            };
            MetaSummary =
                $"{record.CreatedAt:yyyy-MM-dd HH:mm:ss}" +
                (string.IsNullOrWhiteSpace(record.Reviewer) ? string.Empty : $"｜{record.Reviewer}") +
                (string.IsNullOrWhiteSpace(record.ReviewReason) ? string.Empty : $"｜原因：{record.ReviewReason}");
            DetailText = string.IsNullOrWhiteSpace(record.ReviewComment)
                ? (string.IsNullOrWhiteSpace(record.ReviewReason) ? "无附加说明" : record.ReviewReason)
                : record.ReviewComment;
            CreatedAt = record.CreatedAt;
            ActionType = actionType;
        }

        public ReviewHistoryListItem(string actionText, string detailText)
        {
            ActionText = actionText;
            MetaSummary = string.Empty;
            DetailText = detailText;
            CreatedAt = DateTimeOffset.MinValue;
            ActionType = string.Empty;
        }

        public string ActionText { get; }
        public string MetaSummary { get; }
        public string DetailText { get; }
        public DateTimeOffset CreatedAt { get; }
        public string ActionType { get; } = string.Empty;
    }

    public sealed record HistoryFilterOption(string Key, string DisplayName);
}
