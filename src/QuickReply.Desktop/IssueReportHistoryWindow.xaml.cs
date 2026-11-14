using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop;

public partial class IssueReportHistoryWindow : Window
{
    private readonly IssueReportService _issueReportService;
    private readonly Action _onOpenDirectory;

    public sealed class IssueEntryRow
    {
        public required IssueReportIndexEntry Entry { get; init; }
        public string CreatedAtText => Entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string DescriptionSummary => Entry.UserDescription.Length > 45 ? Entry.UserDescription[..45] + "..." : Entry.UserDescription;
        public string StatusText => string.IsNullOrWhiteSpace(Entry.FilePath) ? "未保存" : (Entry.UploadSucceeded ? "已上传" : "本地保存");
    }

    public IssueReportHistoryWindow(IssueReportService issueReportService, Action onOpenDirectory)
    {
        InitializeComponent();
        _issueReportService = issueReportService;
        _onOpenDirectory = onOpenDirectory;
        Loaded += async (_, _) => await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        try
        {
            var entries = await _issueReportService.LoadRecentIssueEntriesAsync();
            var rows = entries.Select(entry => new IssueEntryRow { Entry = entry }).ToList();
            EntriesListView.ItemsSource = rows;
            if (rows.Count > 0)
            {
                EntriesListView.SelectedIndex = 0;
            }
            else
            {
                DetailTextBox.Text = "当前还没有任何问题记录。";
            }
        }
        catch (Exception ex)
        {
            DetailTextBox.Text = $"加载问题记录历史失败：{ex.Message}";
        }
    }

    private void EntriesListView_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntriesListView.SelectedItem is not IssueEntryRow row)
        {
            DetailTextBox.Text = "选择一条问题记录后查看详细信息。";
            return;
        }

        var entry = row.Entry;
        var sb = new StringBuilder();
        sb.AppendLine($"【时间】{entry.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"【问题 ID】{entry.IssueId}");
        sb.AppendLine($"【描述】{entry.UserDescription}");
        if (!string.IsNullOrWhiteSpace(entry.FilePath))
        {
            sb.AppendLine($"【本地诊断包】{entry.FilePath}");
        }
        if (entry.UploadAttempted)
        {
            sb.AppendLine($"【上传状态】{(entry.UploadSucceeded ? "成功" : "失败")}（{entry.UploadMessage}）");
        }
        if (!string.IsNullOrWhiteSpace(entry.PatchVersion))
        {
            sb.AppendLine($"【补丁版本】{entry.PatchVersion}");
        }

        DetailTextBox.Text = sb.ToString();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OpenDirectoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        _onOpenDirectory();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}