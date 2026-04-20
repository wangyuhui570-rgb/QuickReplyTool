using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop;

public partial class PhraseCleanupWindow : Window, INotifyPropertyChanged
{
    private const double SimilarityThreshold = 0.86d;
    private readonly MainViewModel _viewModel;
    private string _summaryText = "正在扫描...";
    private string _exactSummaryText = "正在扫描完全重复项...";
    private string _similarSummaryText = "正在扫描相似短语...";

    public PhraseCleanupWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        WindowTransitionService.Attach(this);
        _viewModel = viewModel;
        DataContext = this;
        Loaded += PhraseCleanupWindow_OnLoaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PhraseCleanupGroup> ExactDuplicateGroups { get; } = [];

    public ObservableCollection<PhraseCleanupGroup> SimilarGroups { get; } = [];

    public string SummaryText
    {
        get => _summaryText;
        private set => SetField(ref _summaryText, value);
    }

    public string ExactSummaryText
    {
        get => _exactSummaryText;
        private set => SetField(ref _exactSummaryText, value);
    }

    public string SimilarSummaryText
    {
        get => _similarSummaryText;
        private set => SetField(ref _similarSummaryText, value);
    }

    public bool HasExactGroups => ExactDuplicateGroups.Count > 0;

    public bool HasSimilarGroups => SimilarGroups.Count > 0;

    public bool CanDeleteExactSelection => ExactDuplicateGroups.Any(group => group.HasSelection);

    public bool CanDeleteSimilarSelection => SimilarGroups.Any(group => group.HasSelection);

    public string DeleteExactButtonText => $"删除已选重复项 ({GetExactSelectedCount()})";

    public string DeleteSimilarButtonText => $"删除已选相似项 ({GetSimilarSelectedCount()})";

    private async void PhraseCleanupWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        await ScanAsync();
    }

    private async void RescanButton_OnClick(object sender, RoutedEventArgs e)
    {
        await ScanAsync();
    }

    private async void DeleteExactSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedAsync(ExactDuplicateGroups, "完全重复项");
    }

    private async void DeleteSimilarSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedAsync(SimilarGroups, "相似短语");
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async Task ScanAsync()
    {
        SummaryText = "正在扫描快捷短语，请稍候...";
        ExactSummaryText = "正在扫描完全重复项...";
        SimilarSummaryText = "正在扫描相似短语...";

        await Task.Yield();

        var allPhrases = _viewModel.Categories
            .SelectMany(category => category.Phrases)
            .GroupBy(phrase => phrase.Id)
            .Select(group => group.First())
            .ToList();

        var exactGroups = BuildExactDuplicateGroups(allPhrases);
        var exactIds = exactGroups
            .SelectMany(group => group.Items)
            .Select(item => item.Phrase.Id)
            .ToHashSet();
        var similarGroups = BuildSimilarGroups(allPhrases.Where(phrase => !exactIds.Contains(phrase.Id)).ToList());

        ReplaceGroups(ExactDuplicateGroups, exactGroups);
        ReplaceGroups(SimilarGroups, similarGroups);

        ExactSummaryText = exactGroups.Count == 0
            ? "没有发现完全重复的快捷短语。"
            : $"共找到 {exactGroups.Count} 组完全重复项，默认已帮你勾选建议删除的多余短语。";
        SimilarSummaryText = similarGroups.Count == 0
            ? "没有发现内容高度相似的快捷短语。"
            : $"共找到 {similarGroups.Count} 组相似短语候选，请手动勾选想删除的多余项。";
        SummaryText = $"当前共扫描 {allPhrases.Count} 条快捷短语。完全重复 {exactGroups.Sum(group => group.TotalCount)} 条，相似候选 {similarGroups.Sum(group => group.TotalCount)} 条。";

        NotifySelectionStateChanged();
    }

    private List<PhraseCleanupGroup> BuildExactDuplicateGroups(IReadOnlyList<ReplyPhrase> phrases)
    {
        return phrases
            .GroupBy(BuildExactDuplicateKey)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .Select(group =>
            {
                var ranked = group
                    .OrderByDescending(GetPhraseSortScore)
                    .ThenByDescending(phrase => phrase.HasImage)
                    .ThenByDescending(phrase => phrase.UpdatedAt)
                    .ToList();

                var cleanupGroup = new PhraseCleanupGroup
                {
                    Header = $"{GetDisplayName(ranked[0])}  共 {ranked.Count} 条",
                    Description = "只要快捷短语内容完全一致，就视为百分百重复。建议保留使用更多、更新更近或带图片的那一条。"
                };

                for (var index = 0; index < ranked.Count; index++)
                {
                    cleanupGroup.Items.Add(BuildCleanupItem(ranked[index], index > 0));
                }

                return cleanupGroup;
            })
            .OrderByDescending(group => group.TotalCount)
            .ThenBy(group => group.Header, StringComparer.CurrentCulture)
            .ToList();
    }

    private List<PhraseCleanupGroup> BuildSimilarGroups(IReadOnlyList<ReplyPhrase> phrases)
    {
        if (phrases.Count < 2)
        {
            return [];
        }

        var nodes = phrases
            .Select(phrase => new SimilarPhraseNode(phrase, BuildSimilarityText(phrase)))
            .Where(node => !string.IsNullOrWhiteSpace(node.NormalizedText))
            .ToList();
        var parent = nodes.ToDictionary(node => node.Phrase.Id, node => node.Phrase.Id);
        var edgeScores = new Dictionary<Guid, List<double>>();

        for (var index = 0; index < nodes.Count; index++)
        {
            var left = nodes[index];
            for (var inner = index + 1; inner < nodes.Count; inner++)
            {
                var right = nodes[inner];
                if (!CanCompare(left.NormalizedText, right.NormalizedText))
                {
                    continue;
                }

                var similarity = CalculateDiceSimilarity(left.NormalizedText, right.NormalizedText);
                if (similarity < SimilarityThreshold || similarity >= 0.999d)
                {
                    continue;
                }

                Union(parent, left.Phrase.Id, right.Phrase.Id);
                AddSimilarityScore(edgeScores, left.Phrase.Id, similarity);
                AddSimilarityScore(edgeScores, right.Phrase.Id, similarity);
            }
        }

        return nodes
            .GroupBy(node => Find(parent, node.Phrase.Id))
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var ranked = group
                    .Select(node => node.Phrase)
                    .OrderByDescending(GetPhraseSortScore)
                    .ThenByDescending(phrase => phrase.HasImage)
                    .ThenByDescending(phrase => phrase.UpdatedAt)
                    .ToList();
                var scores = ranked
                    .SelectMany(phrase => edgeScores.TryGetValue(phrase.Id, out var items) ? items : [])
                    .DefaultIfEmpty(SimilarityThreshold)
                    .ToList();
                var bestSimilarity = scores.Max();

                var cleanupGroup = new PhraseCleanupGroup
                {
                    Header = $"{GetDisplayName(ranked[0])}  共 {ranked.Count} 条",
                    Description = $"内容高度相似，最高相似度约 {(int)Math.Round(bestSimilarity * 100)}%。建议人工确认后删除。"
                };

                foreach (var phrase in ranked)
                {
                    cleanupGroup.Items.Add(BuildCleanupItem(phrase, false));
                }

                return cleanupGroup;
            })
            .OrderByDescending(group => group.TotalCount)
            .ThenBy(group => group.Header, StringComparer.CurrentCulture)
            .ToList();
    }

    private PhraseCleanupItem BuildCleanupItem(ReplyPhrase phrase, bool isSelected)
    {
        var sendCount = _viewModel.GetPhraseSendCount(phrase.Id);
        var lastSentAt = _viewModel.GetPhraseLastSentAt(phrase.Id);
        return new PhraseCleanupItem
        {
            Phrase = phrase,
            UsageSummary = lastSentAt is null
                ? $"已发送 {sendCount} 次 | 暂无发送记录"
                : $"已发送 {sendCount} 次 | 最近 {lastSentAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}",
            ImageSummary = phrase.HasImage ? $"含 {phrase.ImageCount} 张图片" : "无图片",
            IsSelected = isSelected
        };
    }

    private async Task DeleteSelectedAsync(IEnumerable<PhraseCleanupGroup> groups, string typeLabel)
    {
        var selectedPhrases = groups
            .SelectMany(group => group.Items)
            .Where(item => item.IsSelected)
            .Select(item => item.Phrase)
            .DistinctBy(phrase => phrase.Id)
            .ToList();

        if (selectedPhrases.Count == 0)
        {
            System.Windows.MessageBox.Show(this, $"请先勾选要删除的{typeLabel}。", "未选择删除项", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmMessage = $"将删除 {selectedPhrases.Count} 条{typeLabel}，这个操作不可撤销。\n\n确定继续吗？";
        var confirmResult = System.Windows.MessageBox.Show(this, confirmMessage, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        var deletedCount = await _viewModel.DeletePhrasesAsync(selectedPhrases);
        System.Windows.MessageBox.Show(this, $"已删除 {deletedCount} 条{typeLabel}。", "整理完成", MessageBoxButton.OK, MessageBoxImage.Information);
        await ScanAsync();
    }

    private void ReplaceGroups(ObservableCollection<PhraseCleanupGroup> target, IReadOnlyList<PhraseCleanupGroup> source)
    {
        target.Clear();
        foreach (var group in source)
        {
            group.AttachSelectionTracking();
            foreach (var item in group.Items)
            {
                item.PropertyChanged += CleanupItem_OnPropertyChanged;
            }

            target.Add(group);
        }

        OnPropertyChanged(nameof(HasExactGroups));
        OnPropertyChanged(nameof(HasSimilarGroups));
    }

    private void CleanupItem_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhraseCleanupItem.IsSelected))
        {
            NotifySelectionStateChanged();
        }
    }

    private void NotifySelectionStateChanged()
    {
        OnPropertyChanged(nameof(CanDeleteExactSelection));
        OnPropertyChanged(nameof(CanDeleteSimilarSelection));
        OnPropertyChanged(nameof(DeleteExactButtonText));
        OnPropertyChanged(nameof(DeleteSimilarButtonText));
    }

    private int GetExactSelectedCount()
    {
        return ExactDuplicateGroups.Sum(group => group.SelectedCount);
    }

    private int GetSimilarSelectedCount()
    {
        return SimilarGroups.Sum(group => group.SelectedCount);
    }

    private static string BuildExactDuplicateKey(ReplyPhrase phrase)
    {
        return NormalizeForExactComparison(phrase.Content);
    }

    private static string NormalizeForExactComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => string.Join(" ", line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)))
            .Aggregate(new StringBuilder(), (builder, line) =>
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(line.Trim());
                return builder;
            })
            .ToString()
            .Trim();
    }

    private static string BuildSimilarityText(ReplyPhrase phrase)
    {
        var source = $"{phrase.DisplayTitle} {phrase.Content}".Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            if (char.IsWhiteSpace(character) || char.IsPunctuation(character) || char.IsSymbol(character))
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static bool CanCompare(string left, string right)
    {
        if (left.Length < 4 || right.Length < 4)
        {
            return false;
        }

        return Math.Abs(left.Length - right.Length) <= Math.Max(8, (int)(Math.Max(left.Length, right.Length) * 0.35));
    }

    private static double CalculateDiceSimilarity(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1d;
        }

        var leftTokens = BuildBigrams(left);
        var rightTokens = BuildBigrams(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0d;
        }

        var shared = 0;
        var rightUsage = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in rightTokens)
        {
            rightUsage[token] = rightUsage.TryGetValue(token, out var count) ? count + 1 : 1;
        }

        foreach (var token in leftTokens)
        {
            if (!rightUsage.TryGetValue(token, out var remaining) || remaining <= 0)
            {
                continue;
            }

            rightUsage[token] = remaining - 1;
            shared++;
        }

        return (2d * shared) / (leftTokens.Count + rightTokens.Count);
    }

    private static List<string> BuildBigrams(string value)
    {
        if (value.Length == 1)
        {
            return [value];
        }

        var tokens = new List<string>(Math.Max(1, value.Length - 1));
        for (var index = 0; index < value.Length - 1; index++)
        {
            tokens.Add(value.Substring(index, 2));
        }

        return tokens;
    }

    private static void AddSimilarityScore(Dictionary<Guid, List<double>> edgeScores, Guid phraseId, double score)
    {
        if (!edgeScores.TryGetValue(phraseId, out var scores))
        {
            scores = [];
            edgeScores[phraseId] = scores;
        }

        scores.Add(score);
    }

    private static Guid Find(Dictionary<Guid, Guid> parent, Guid id)
    {
        if (parent[id] == id)
        {
            return id;
        }

        parent[id] = Find(parent, parent[id]);
        return parent[id];
    }

    private static void Union(Dictionary<Guid, Guid> parent, Guid left, Guid right)
    {
        var leftRoot = Find(parent, left);
        var rightRoot = Find(parent, right);
        if (leftRoot != rightRoot)
        {
            parent[rightRoot] = leftRoot;
        }
    }

    private double GetPhraseSortScore(ReplyPhrase phrase)
    {
        var sendCount = _viewModel.GetPhraseSendCount(phrase.Id);
        var lastSentAt = _viewModel.GetPhraseLastSentAt(phrase.Id);
        var recencyScore = lastSentAt is null ? 0 : lastSentAt.Value.ToUnixTimeSeconds() / 1_000_000_000d;
        return sendCount * 1000d + recencyScore;
    }

    private static string GetDisplayName(ReplyPhrase phrase)
    {
        return phrase.HasDisplayTitle ? phrase.DisplayTitle : phrase.SingleLineContent;
    }

    private bool SetField<T>(ref T field, T value, string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed record SimilarPhraseNode(ReplyPhrase Phrase, string NormalizedText);
}
