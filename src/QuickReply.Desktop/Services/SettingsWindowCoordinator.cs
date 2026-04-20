using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.ViewModels;
using Forms = System.Windows.Forms;

namespace QuickReply.Desktop.Services;

internal sealed class SettingsWindowCoordinator
{
    private readonly Window _owner;
    private readonly MainViewModel _viewModel;
    private readonly SearchOverlayWindow _searchOverlayWindow;
    private readonly Action _applyTheme;
    private SettingsWindow? _settingsWindow;
    private bool _isSidebarPreviewActive;
    private AppSettings? _originalSettingsSnapshot;

    public SettingsWindowCoordinator(
        Window owner,
        MainViewModel viewModel,
        SearchOverlayWindow searchOverlayWindow,
        Action applyTheme)
    {
        _owner = owner;
        _viewModel = viewModel;
        _searchOverlayWindow = searchOverlayWindow;
        _applyTheme = applyTheme;
    }

    public void Open()
    {
        if (_settingsWindow is not null)
        {
            if (!_settingsWindow.IsVisible)
            {
                _settingsWindow = null;
            }
            else
            {
                _settingsWindow.Activate();
                return;
            }
        }

        _originalSettingsSnapshot = _viewModel.CreateSettingsSnapshot();
        var settingsWindow = new SettingsWindow(_viewModel, _originalSettingsSnapshot);
        _settingsWindow = settingsWindow;
        PositionSettingsWindow(settingsWindow);

        settingsWindow.PropertyChanged += SettingsWindow_OnPropertyChanged;
        settingsWindow.Closed += SettingsWindow_OnClosed;

        try
        {
            UpdatePreviewWindows(settingsWindow);
            settingsWindow.Show();
            settingsWindow.Activate();
        }
        finally
        {
            if (_settingsWindow is null)
            {
                _searchOverlayWindow.EndSettingsPreview();
                _applyTheme();
            }
        }
    }

    public void Close()
    {
        _settingsWindow?.Close();
    }

    private void SettingsWindow_OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not SettingsWindow settingsWindow)
        {
            return;
        }

        if (args.PropertyName == nameof(SettingsWindow.SelectedSectionKey))
        {
            UpdatePreviewWindows(settingsWindow);
            return;
        }

        if (!IsSearchOverlayPreviewSetting(args.PropertyName) &&
            !IsSidebarPreviewSetting(args.PropertyName))
        {
            return;
        }

        UpdatePreviewWindows(settingsWindow);
    }

    private async void SettingsWindow_OnClosed(object? sender, EventArgs e)
    {
        if (sender is not SettingsWindow settingsWindow)
        {
            return;
        }

        settingsWindow.PropertyChanged -= SettingsWindow_OnPropertyChanged;
        settingsWindow.Closed -= SettingsWindow_OnClosed;
        _settingsWindow = null;

        _searchOverlayWindow.EndSettingsPreview();

        try
        {
            if (!settingsWindow.WasConfirmed || _originalSettingsSnapshot is null)
            {
                EndSidebarPreview(restoreOriginalSettings: true);
                return;
            }

            EndSidebarPreview(restoreOriginalSettings: false);

            var committedSettings = _viewModel.CreateSettingsSnapshot();
            settingsWindow.ApplySelectionsTo(committedSettings);
            var originalPhraseImageFolderPath = _originalSettingsSnapshot.PhraseImageFolderPath;

            _viewModel.BeginSettingsPreview();
            try
            {
                var startupConfigurationResult = _viewModel.ApplyCommittedSettings(committedSettings, updateStatusMessage: true);
                await _viewModel.ApplyPhraseImageFolderChangeAsync(originalPhraseImageFolderPath);
                _applyTheme();
                await _viewModel.PersistSettingsAsync();
                App.Instance?.RefreshScreenshotToolConfiguration(showNotificationOnFailure: true);
                await _viewModel.SyncFileLibraryAsync(updateStatus: startupConfigurationResult.Success);
            }
            catch
            {
                _viewModel.ApplyCommittedSettings(_originalSettingsSnapshot);
                _applyTheme();
                throw;
            }
            finally
            {
                _viewModel.EndSettingsPreview();
            }
        }
        finally
        {
            _ = _owner.Dispatcher.BeginInvoke(() =>
            {
                _searchOverlayWindow.EndSettingsPreview();
                _applyTheme();
                _owner.Activate();
                _owner.Focus();
            });
        }
    }

    private void UpdatePreviewWindows(SettingsWindow settingsWindow)
    {
        var previewSettings = _viewModel.CreateSettingsSnapshot();
        settingsWindow.ApplySelectionsTo(previewSettings);

        if (string.Equals(settingsWindow.SelectedSectionKey, SettingsWindow.AppearanceSectionKey, StringComparison.Ordinal))
        {
            _searchOverlayWindow.EndSettingsPreview();
            StartOrUpdateSidebarPreview(settingsWindow, previewSettings);
            return;
        }

        EndSidebarPreview(restoreOriginalSettings: true);

        if (string.Equals(settingsWindow.SelectedSectionKey, SettingsWindow.SearchSectionKey, StringComparison.Ordinal))
        {
            if (_searchOverlayWindow.IsSettingsPreviewVisible)
            {
                _searchOverlayWindow.UpdateSettingsPreview(settingsWindow.SelectedDefaultSearchOverlayMode, previewSettings);
            }
            else
            {
                _searchOverlayWindow.ShowSettingsPreview(settingsWindow, settingsWindow.SelectedDefaultSearchOverlayMode, previewSettings);
            }
        }
        else
        {
            _searchOverlayWindow.EndSettingsPreview();
        }
    }

    private void StartOrUpdateSidebarPreview(SettingsWindow settingsWindow, AppSettings previewSettings)
    {
        var previewBounds = GetSidebarPreviewBounds(settingsWindow, previewSettings);
        if (!_isSidebarPreviewActive)
        {
            _viewModel.BeginSettingsPreview();
            _isSidebarPreviewActive = true;
        }

        _viewModel.ApplyPreviewSettings(previewSettings);
        _applyTheme();
        _viewModel.BeginSidebarSettingsPreview(
            previewBounds.Left,
            previewBounds.Top,
            previewBounds.Width,
            previewBounds.Height);
    }

    private void EndSidebarPreview(bool restoreOriginalSettings)
    {
        if (!_isSidebarPreviewActive)
        {
            return;
        }

        if (restoreOriginalSettings && _originalSettingsSnapshot is not null)
        {
            _viewModel.ApplyPreviewSettings(_originalSettingsSnapshot);
            _applyTheme();
        }

        _viewModel.EndSidebarSettingsPreview();
        _viewModel.EndSettingsPreview();
        _isSidebarPreviewActive = false;
    }

    private void PositionSettingsWindow(SettingsWindow settingsWindow)
    {
        var workArea = Forms.Screen.FromHandle(new WindowInteropHelper(_owner).Handle).WorkingArea;
        var desiredLeft = _owner.Left + _owner.Width + 16;
        if (desiredLeft + settingsWindow.Width > workArea.Right)
        {
            desiredLeft = _owner.Left - settingsWindow.Width - 16;
        }

        settingsWindow.WindowStartupLocation = WindowStartupLocation.Manual;
        settingsWindow.Left = Math.Max(workArea.Left + 8, Math.Min(desiredLeft, workArea.Right - settingsWindow.Width - 8));
        settingsWindow.Top = Math.Max(workArea.Top + 8, Math.Min(_owner.Top, workArea.Bottom - settingsWindow.Height - 8));
    }

    private static Rect GetSidebarPreviewBounds(SettingsWindow settingsWindow, AppSettings previewSettings)
    {
        var screen = Forms.Screen.FromHandle(new WindowInteropHelper(settingsWindow).Handle).WorkingArea;
        var width = Math.Clamp(previewSettings.SidebarWidth, 180, 620);
        var height = Math.Clamp(screen.Height * (previewSettings.SidebarHeightPercent / 100d), 360, screen.Height);
        const double gap = 12;

        var left = settingsWindow.Left - width - gap;
        if (left < screen.Left + gap)
        {
            left = settingsWindow.Left + settingsWindow.Width + gap;
        }

        var top = settingsWindow.Top;
        top = Math.Max(screen.Top + gap, Math.Min(top, screen.Bottom - height - gap));
        return new Rect(left, top, width, height);
    }

    private static bool IsSearchOverlayPreviewSetting(string? propertyName)
    {
        return propertyName is nameof(SettingsWindow.SelectedDefaultSearchOverlayMode) or
            nameof(SettingsWindow.SelectedSearchOverlayWidthPercent) or
            nameof(SettingsWindow.SelectedSearchOverlayHeightPercent) or
            nameof(SettingsWindow.SelectedSearchInputWidthPercent) or
            nameof(SettingsWindow.SelectedSearchInputBoxHeight) or
            nameof(SettingsWindow.SelectedTagColorHex) or
            nameof(SettingsWindow.SelectedTagOpacityPercent) or
            nameof(SettingsWindow.SelectedSearchOverlayColorHex) or
            nameof(SettingsWindow.SelectedSearchOverlayOpacityPercent) or
            nameof(SettingsWindow.SelectedSearchInputColorHex) or
            nameof(SettingsWindow.SelectedSearchInputOpacityPercent);
    }

    private static bool IsSidebarPreviewSetting(string? propertyName)
    {
        return propertyName is nameof(SettingsWindow.SelectedSidebarWorkMode) or
            nameof(SettingsWindow.SelectedSidebarWidth) or
            nameof(SettingsWindow.SelectedSidebarHeightPercent) or
            nameof(SettingsWindow.SelectedCategoryTabsPerRow) or
            nameof(SettingsWindow.SelectedCategoryFontSize) or
            nameof(SettingsWindow.SelectedPhraseFontSize) or
            nameof(SettingsWindow.SelectedPhraseRowSpacing) or
            nameof(SettingsWindow.SelectedCategoryColorHex) or
            nameof(SettingsWindow.SelectedCategoryOpacityPercent) or
            nameof(SettingsWindow.SelectedTagColorHex) or
            nameof(SettingsWindow.SelectedTagOpacityPercent) or
            nameof(SettingsWindow.SelectedPhraseColorHex) or
            nameof(SettingsWindow.SelectedPhraseOpacityPercent) or
            nameof(SettingsWindow.SelectedSidebarColorHex) or
            nameof(SettingsWindow.SelectedSidebarOpacityPercent);
    }
}
