using System.Windows;
using System.Windows.Input;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop;

public partial class FileLibraryWindow : Window
{
    private readonly MainViewModel _viewModel;
    private LibraryFileItem? _selectedFileItem;

    public FileLibraryWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        WindowTransitionService.Attach(this);
        _viewModel = viewModel;
        DataContext = viewModel;
        UpdateSummary();
    }

    public LibraryFileItem? SelectedFileItem
    {
        get => _selectedFileItem;
        set => _selectedFileItem = value;
    }

    private async void AddFilesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要加入文件库的文件",
            Multiselect = true,
            Filter = "所有支持的文件|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp;*.mp4;*.mov;*.avi;*.mkv;*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.txt;*.zip;*.rar|所有文件|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await _viewModel.AddFileItemsAsync(dialog.FileNames);
        UpdateSummary();
    }

    private async void RemoveSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedFileItem is null)
        {
            return;
        }

        await _viewModel.DeleteFileItemAsync(SelectedFileItem);
        SelectedFileItem = null;
        UpdateSummary();
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

    private void UpdateSummary()
    {
        SummaryText.Text = $"已配置 {_viewModel.FileItems.Count} 个文件。";
    }
}
