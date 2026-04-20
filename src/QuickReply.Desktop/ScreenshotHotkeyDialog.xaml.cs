using System.Windows;
using System.Windows.Input;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop;

public partial class ScreenshotHotkeyDialog : Window
{
    private readonly string _currentHotkey;
    private string _capturedHotkey = string.Empty;

    public ScreenshotHotkeyDialog(string currentHotkey)
    {
        InitializeComponent();
        _currentHotkey = ScreenshotHotkeyBinding.NormalizeOrDefault(currentHotkey);
        CurrentHotkeyTextBlock.Text = _currentHotkey;
        Loaded += ScreenshotHotkeyDialog_OnLoaded;
    }

    public string SelectedHotkey { get; private set; } = string.Empty;

    private void ScreenshotHotkeyDialog_OnLoaded(object sender, RoutedEventArgs e)
    {
        CaptureSurface.Focus();
        Keyboard.Focus(CaptureSurface);
    }

    private void CaptureSurface_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CaptureSurface.Focus();
        Keyboard.Focus(CaptureSurface);
        e.Handled = true;
    }

    private void CaptureSurface_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (ScreenshotHotkeyBinding.TryBuildFromKeyEvent(e, out var hotkey, out var cancelRequested, out var error))
        {
            _capturedHotkey = hotkey;
            CapturedHotkeyTextBlock.Text = hotkey;
            ErrorTextBlock.Text = string.Empty;
            SaveButton.IsEnabled = true;
            e.Handled = true;
            return;
        }

        if (cancelRequested && string.IsNullOrWhiteSpace(_capturedHotkey))
        {
            DialogResult = false;
            Close();
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            ErrorTextBlock.Text = error;
        }

        e.Handled = true;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_capturedHotkey))
        {
            ErrorTextBlock.Text = "请先按下新的快捷键。";
            return;
        }

        SelectedHotkey = _capturedHotkey;
        DialogResult = true;
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void RestoreDefaultButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedHotkey = ScreenshotHotkeyBinding.DefaultHotkey;
        DialogResult = true;
        Close();
    }
}
