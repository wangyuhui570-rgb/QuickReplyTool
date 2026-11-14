using System.Windows;
using System.Windows.Input;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop;

public partial class IssueReportDialogWindow : Window
{
    private readonly IssueReportService _issueReportService;
    private readonly Action<string>? _onFileLocationOpened;

    public IssueReportDialogWindow(IssueReportService issueReportService, Action<string>? onFileLocationOpened = null)
    {
        InitializeComponent();
        _issueReportService = issueReportService;
        _onFileLocationOpened = onFileLocationOpened;
        Loaded += (_, _) => IssueTextBox.Focus();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void SubmitButton_OnClick(object sender, RoutedEventArgs e)
    {
        var text = IssueTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            IssueTextBox.Focus();
            return;
        }

        SubmitButton.IsEnabled = false;
        try
        {
            var result = await _issueReportService.CreateReportAsync(text);
            if (!result.Success)
            {
                System.Windows.MessageBox.Show(this, result.Message, "问题记录", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.FilePath))
            {
                _onFileLocationOpened?.Invoke(result.FilePath);
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"提交问题记录异常：{ex.Message}", "问题记录", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SubmitButton.IsEnabled = true;
        }
    }
}