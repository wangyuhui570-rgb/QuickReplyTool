using System.Windows;
using System.Windows.Input;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop;

public partial class ImportProgressWindow : Window
{
    public ImportProgressWindow()
    {
        InitializeComponent();
        WindowTransitionService.Attach(this);
    }

    public void UpdateProgress(PhraseImportProgress progress)
    {
        var percent = Math.Clamp(progress.Percent, 0, 100);
        ImportProgressBar.Value = percent;
        StatusTextBlock.Text = string.IsNullOrWhiteSpace(progress.Message)
            ? $"正在导入... {percent}%"
            : progress.Message;
        PercentTextBlock.Text = $"{percent}%";
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
