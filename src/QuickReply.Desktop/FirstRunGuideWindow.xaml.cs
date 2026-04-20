using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop;

public partial class FirstRunGuideWindow : Window, INotifyPropertyChanged
{
    private readonly FolderSelectionDialogService _folderSelectionDialogService = new();
    private int _stepIndex = 1;
    private SearchFocusHotkeyMode _selectedSearchHotkeyMode;
    private SendHotkeyMode _selectedSendHotkeyMode;
    private string _selectedFileLibraryFolderPath = string.Empty;

    public FirstRunGuideWindow(
        SearchFocusHotkeyMode initialSearchHotkeyMode,
        SendHotkeyMode initialSendHotkeyMode,
        string initialFileLibraryFolderPath)
    {
        InitializeComponent();
        WindowTransitionService.Attach(this);
        _selectedSearchHotkeyMode = initialSearchHotkeyMode;
        _selectedSendHotkeyMode = initialSendHotkeyMode;
        _selectedFileLibraryFolderPath = initialFileLibraryFolderPath ?? string.Empty;
        DataContext = this;
        UpdateStepVisualState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SearchFocusHotkeyMode SelectedSearchHotkeyMode
    {
        get => _selectedSearchHotkeyMode;
        set => SetField(ref _selectedSearchHotkeyMode, value);
    }

    public SendHotkeyMode SelectedSendHotkeyMode
    {
        get => _selectedSendHotkeyMode;
        set => SetField(ref _selectedSendHotkeyMode, value);
    }

    public string SelectedFileLibraryFolderPath
    {
        get => _selectedFileLibraryFolderPath;
        set
        {
            if (SetField(ref _selectedFileLibraryFolderPath, value?.Trim() ?? string.Empty))
            {
                OnPropertyChanged(nameof(FileLibraryFolderHelperText));
                OnPropertyChanged(nameof(FileLibraryFolderHelperBrush));
                OnPropertyChanged(nameof(CanMoveNext));
            }
        }
    }

    public bool CanGoBack => _stepIndex > 1;

    public bool CanMoveNext => _stepIndex < 3 || string.IsNullOrWhiteSpace(SettingsInputValidator.ValidateFirstRunFileLibraryFolder(SelectedFileLibraryFolderPath));

    public string NextButtonText => _stepIndex >= 3 ? "完成设置" : "下一步";

    public string FileLibraryFolderHelperText => SettingsInputValidator.ValidateFirstRunFileLibraryFolder(SelectedFileLibraryFolderPath)
        ?? "这里可以先留空，后面再到选项里补。";

    public System.Windows.Media.Brush FileLibraryFolderHelperBrush => string.IsNullOrWhiteSpace(SettingsInputValidator.ValidateFirstRunFileLibraryFolder(SelectedFileLibraryFolderPath))
        ? CreateBrush("#64748B")
        : CreateBrush("#C9342C");

    public System.Windows.Media.Brush StepDividerBrush1 => CreateBrush(_stepIndex >= 2 ? "#2563EB" : "#D9E4F1");

    public System.Windows.Media.Brush StepDividerBrush2 => CreateBrush(_stepIndex >= 3 ? "#2563EB" : "#D9E4F1");

    public System.Windows.Media.Brush StepBadgeBrush2 => CreateBrush(_stepIndex >= 2 ? "#2563EB" : "#E2E8F0");

    public System.Windows.Media.Brush StepBadgeBrush3 => CreateBrush(_stepIndex >= 3 ? "#2563EB" : "#E2E8F0");

    public System.Windows.Media.Brush StepBadgeTextBrush2 => _stepIndex >= 2 ? System.Windows.Media.Brushes.White : CreateBrush("#64748B");

    public System.Windows.Media.Brush StepBadgeTextBrush3 => _stepIndex >= 3 ? System.Windows.Media.Brushes.White : CreateBrush("#64748B");

    private void TitleBar_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void SkipButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowTransitionService.SuppressNextCloseAnimation(this);
        DialogResult = false;
    }

    private void BrowseFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var result = _folderSelectionDialogService.SelectFolder(
            "选择客服资料文件夹",
            SelectedFileLibraryFolderPath);

        if (result.HasError)
        {
            System.Windows.MessageBox.Show(
                this,
                $"{result.ErrorMessage}{Environment.NewLine}{Environment.NewLine}你也可以直接在输入框里粘贴一个本地目录路径后继续。",
                "无法打开文件夹选择器",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (result.Confirmed)
        {
            SelectedFileLibraryFolderPath = result.SelectedPath;
        }
    }

    private void BackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_stepIndex <= 1)
        {
            return;
        }

        _stepIndex--;
        UpdateStepVisualState();
    }

    private void NextButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanMoveNext)
        {
            return;
        }

        if (_stepIndex >= 3)
        {
            WindowTransitionService.SuppressNextCloseAnimation(this);
            DialogResult = true;
            return;
        }

        _stepIndex++;
        UpdateStepVisualState();
    }

    private void UpdateStepVisualState()
    {
        StepOnePanel.Visibility = _stepIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepTwoPanel.Visibility = _stepIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepThreePanel.Visibility = _stepIndex == 3 ? Visibility.Visible : Visibility.Collapsed;

        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanMoveNext));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(StepDividerBrush1));
        OnPropertyChanged(nameof(StepDividerBrush2));
        OnPropertyChanged(nameof(StepBadgeBrush2));
        OnPropertyChanged(nameof(StepBadgeBrush3));
        OnPropertyChanged(nameof(StepBadgeTextBrush2));
        OnPropertyChanged(nameof(StepBadgeTextBrush3));
    }

    private static System.Windows.Media.Brush CreateBrush(string hex)
    {
        return (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
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
}
