using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Native;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.ViewModels;
using Forms = System.Windows.Forms;

namespace QuickReply.Desktop;

public partial class SearchOverlayWindow : Window
{
    private static readonly Duration ModeToastAnimationDuration = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration OverlayFadeInDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration OverlayFadeOutDuration = new(TimeSpan.FromMilliseconds(150));
    private const double OverlayHiddenScale = 0.985d;
    private const double SearchOverlayScreenMargin = 24d;
    private readonly IExternalWorkbenchService _windowTracker;
    private readonly SearchOverlayViewModel _viewModel;
    private readonly DispatcherTimer _placementTimer;
    private readonly DispatcherTimer _modeToastTimer;
    private NativeMethods.LowLevelMouseProc? _mouseHookCallback;
    private IntPtr _mouseHookHandle;
    private NativeMethods.WinEventDelegate? _foregroundHookCallback;
    private IntPtr _foregroundHookHandle;
    private IntPtr _windowHandle;
    private IntPtr _overlayTargetWindowHandle;
    private IntPtr _overlayTargetFocusHandle;
    private IntPtr _overlayTargetCaretHandle;
    private NativeMethods.RECT _overlayTargetCaretRect;
    private bool _overlayTargetHasCaretRect;
    private uint _overlayTargetThreadId;
    private bool _isSending;
    private bool _isSettingsPreviewMode;
    private bool _isPrewarming;
    private bool _presentationWarmedUp;
    private int _activationRequestVersion;
    private int _activationRecoveryAttempts;
    private bool _isReturningFocusToExternalWindow;
    private bool _inputReadyLogged;
    private bool _suppressTabUntilRelease;
    private long _lastShowStartedAt;
    private int _overlaySessionId;
    private int _overlaySessionSequence;
    private int _overlayAnimationVersion;
    private int _overlayClosingSessionId;
    private AppSettings? _previewSettings;
    private Window? _previewAnchorWindow;
    private ReplyPhrase? _phraseMultiSelectAnchor;

    public SearchOverlayWindow(IExternalWorkbenchService windowTracker, SearchOverlayViewModel viewModel)
    {
        InitializeComponent();
        _windowTracker = windowTracker;
        _viewModel = viewModel;
        DataContext = viewModel;

        _placementTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _placementTimer.Tick += PlacementTimer_OnTick;

        _modeToastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        _modeToastTimer.Tick += ModeToastTimer_OnTick;

        SourceInitialized += SearchOverlayWindow_OnSourceInitialized;
        Activated += SearchOverlayWindow_OnActivated;
        PreviewKeyDown += SearchOverlayWindow_OnPreviewKeyDown;
        PreviewKeyUp += SearchOverlayWindow_OnPreviewKeyUp;
        Deactivated += SearchOverlayWindow_OnDeactivated;
        Loaded += SearchOverlayWindow_OnLoaded;
        LayoutUpdated += SearchOverlayWindow_OnLayoutUpdated;
        Closed += SearchOverlayWindow_OnClosed;
        IsVisibleChanged += SearchOverlayWindow_OnIsVisibleChanged;
        ContentRendered += SearchOverlayWindow_OnContentRendered;

        Dispatcher.BeginInvoke(EnsureWindowHandleCreated, DispatcherPriority.ApplicationIdle);
    }

    public bool CanAcceptSearchFocus => !IsVisible || _isPrewarming || !IsReadyForDirectTyping();

    public bool IsSettingsPreviewVisible => _isSettingsPreviewMode;

    public bool IsSearchSessionOpen => IsVisible && !_isSettingsPreviewMode && !_isPrewarming;

    public bool HasDirectTypingFocus => IsReadyForDirectTyping();

    public bool OwnsWindowHandle(IntPtr windowHandle)
    {
        return _windowHandle != IntPtr.Zero &&
               NormalizeTopLevelWindow(windowHandle) == _windowHandle;
    }

    public void ShowForSearch(IntPtr targetWindowHandle)
    {
        EnsureWindowHandleCreated();
        _overlayClosingSessionId = 0;

        if (_isPrewarming)
        {
            Hide();
            _isPrewarming = false;
            _presentationWarmedUp = true;
        }

        if (_isSettingsPreviewMode)
        {
            EndSettingsPreview();
        }

        HideModeToast(immediate: true);
        _overlaySessionId = Interlocked.Increment(ref _overlaySessionSequence);
        _lastShowStartedAt = Environment.TickCount64;
        _activationRecoveryAttempts = 0;
        _inputReadyLogged = false;
        _suppressTabUntilRelease = IsTabPhysicallyPressed();
        if (_suppressTabUntilRelease)
        {
            _ = WaitForTabReleaseAsync(_overlaySessionId);
        }

        if (targetWindowHandle != IntPtr.Zero)
        {
            _windowTracker.RememberExternalWindow(targetWindowHandle);
        }

        _overlayTargetWindowHandle = ResolveOverlayTargetWindowHandle(targetWindowHandle);
        CaptureOverlayTargetFocus();
        var openPayload = SearchOverlayDiagnostics.CreateWindowSnapshot("target", targetWindowHandle);
        openPayload["elapsedMs"] = 0;
        openPayload["isVisibleBeforeShow"] = IsVisible;
        SearchOverlayDiagnostics.Log("search_overlay", "show_for_search_start", openPayload);

        _viewModel.PrepareForOpen();
        SearchOverlayDiagnostics.Log("search_overlay", "prepare_for_open_completed", BuildOverlayStatePayload());
        UpdatePlacement();
        SearchOverlayDiagnostics.Log("search_overlay", "placement_ready", BuildOverlayStatePayload());

        var showStopwatch = Stopwatch.StartNew();
        if (!IsVisible)
        {
            ResetOpenAnimationState(hidden: true);
            Show();
        }
        showStopwatch.Stop();

        var shownPayload = BuildOverlayStatePayload();
        shownPayload["showCallDurationMs"] = showStopwatch.ElapsedMilliseconds;
        SearchOverlayDiagnostics.Log("search_overlay", "overlay_shown", shownPayload);
        ActivateWindowForInput();
        RunOpenAnimation();
        _ = TrackActivationDiagnosticsAsync(_overlaySessionId);
        _placementTimer.Start();
        RefreshSearchInputHints();
    }

    public void ShowSettingsPreview(Window anchorWindow, SearchOverlayMode mode, AppSettings previewSettings)
    {
        _overlayClosingSessionId = 0;
        _isSettingsPreviewMode = true;
        _previewAnchorWindow = anchorWindow;
        _previewSettings = previewSettings;
        _viewModel.BeginSettingsPreview(mode);
        ApplyTheme();
        UpdatePlacement();

        if (!IsVisible)
        {
            ResetOpenAnimationState(hidden: true);
            Show();
        }

        RunOpenAnimation();
        _placementTimer.Start();
        anchorWindow.Activate();
    }

    public void UpdateSettingsPreview(SearchOverlayMode mode, AppSettings previewSettings)
    {
        if (!_isSettingsPreviewMode)
        {
            return;
        }

        _previewSettings = previewSettings;
        _viewModel.UpdateSettingsPreview(mode);
        ApplyTheme();
        UpdatePlacement();
    }

    public void EndSettingsPreview()
    {
        if (!_isSettingsPreviewMode)
        {
            return;
        }

        _isSettingsPreviewMode = false;
        _previewSettings = null;
        _previewAnchorWindow = null;
        _viewModel.EndSettingsPreview();
        HideModeToast(immediate: true);
        HideOverlay();
    }

    private void SearchOverlayWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        InstallMouseHook();
        InstallForegroundHook();
    }

    private void SearchOverlayWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTheme();
        _viewModel.Settings.PropertyChanged += Settings_OnPropertyChanged;
        RefreshSearchInputHints();
        SearchTextBox.GotKeyboardFocus += SearchTextBox_OnGotKeyboardFocus;
        SearchTextBox.LostKeyboardFocus += SearchTextBox_OnLostKeyboardFocus;
    }

    private void SearchOverlayWindow_OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Settings.PropertyChanged -= Settings_OnPropertyChanged;
        SearchTextBox.GotKeyboardFocus -= SearchTextBox_OnGotKeyboardFocus;
        SearchTextBox.LostKeyboardFocus -= SearchTextBox_OnLostKeyboardFocus;
        _modeToastTimer.Stop();
        UninstallMouseHook();
        UninstallForegroundHook();
    }

    private void SearchOverlayWindow_OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SearchTextBox.Text))
        {
            return;
        }

        HideDuplicatePlaceholderHints();
    }

    private async void SearchOverlayWindow_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (SearchOverlayShortcutPolicy.IsSearchTextSendAndSaveShortcut(
                key,
                modifiers,
                SearchTextBox.IsKeyboardFocusWithin,
                e.IsRepeat))
        {
            e.Handled = true;
            await SendSearchTextAndSaveAsPhraseAsync();
            return;
        }

        if (SearchOverlayShortcutPolicy.IsSearchTextSendShortcut(
                key,
                modifiers,
                SearchTextBox.IsKeyboardFocusWithin,
                _viewModel.Settings.SearchTextSendShortcutMode))
        {
            e.Handled = true;
            await SendSearchTextAsync();
            return;
        }

        if (modifiers == ModifierKeys.Control && SearchTextBox.IsKeyboardFocusWithin && key == Key.S)
        {
            e.Handled = true;
            await SaveSearchTextAsPhraseAsync();
            return;
        }

        if (modifiers != ModifierKeys.None)
        {
            return;
        }

        if (key == Key.Left)
        {
            e.Handled = true;
            _viewModel.SwitchMode(-1);
            RefreshSearchInputHints();
            ShowModeToast();
            FocusSearchBox();
            return;
        }

        if (key == Key.Right)
        {
            e.Handled = true;
            _viewModel.SwitchMode(1);
            RefreshSearchInputHints();
            ShowModeToast();
            FocusSearchBox();
            return;
        }

        if (key == Key.Tab)
        {
            if (_suppressTabUntilRelease || e.IsRepeat)
            {
                e.Handled = true;
                var payload = BuildOverlayStatePayload();
                payload["ignoredBecauseTabHeld"] = _suppressTabUntilRelease;
                payload["isRepeat"] = e.IsRepeat;
                payload["tabPressedNow"] = IsTabPhysicallyPressed();
                SearchOverlayDiagnostics.Log("search_overlay", "tab_ignored_waiting_for_release", payload);
                return;
            }

            if (GetElapsedSinceShowMs() <= 180)
            {
                e.Handled = true;
                var payload = BuildOverlayStatePayload();
                payload["ignoredDuringOpenGraceMs"] = GetElapsedSinceShowMs();
                SearchOverlayDiagnostics.Log("search_overlay", "tab_ignored_during_open_grace", payload);
                return;
            }

            e.Handled = true;
            ReturnFocusToExternalWindow();
            return;
        }

        if (key == Key.Escape)
        {
            e.Handled = true;
            ReturnFocusToExternalWindow();
            return;
        }

        if (key == Key.Down)
        {
            e.Handled = true;
            _viewModel.MoveSelection(1);
            ScrollSelectionIntoView();
            return;
        }

        if (key == Key.Up)
        {
            e.Handled = true;
            _viewModel.MoveSelection(-1);
            ScrollSelectionIntoView();
            return;
        }

        if (key == Key.Enter)
        {
            if (_viewModel.HasResults)
            {
                e.Handled = true;
                var selectedResult = _viewModel.GetSelectedResult();
                if (selectedResult is not null)
                {
                    await SendResultAsync(selectedResult.Number);
                }

                return;
            }

            if (SearchTextBox.IsKeyboardFocusWithin && !string.IsNullOrWhiteSpace(_viewModel.SearchText))
            {
                e.Handled = true;
                await SendSearchTextAsync();
                return;
            }
        }

        var number = TryGetShortcutNumber(key);
        if (number is null)
        {
            return;
        }

        e.Handled = true;
        await SendResultAsync(number.Value);
    }

    private void SearchOverlayWindow_OnPreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            _suppressTabUntilRelease = false;
        }
    }

    private void PhraseModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.SetMode(SearchOverlayMode.Phrase);
        RefreshSearchInputHints();
        ShowModeToast();
        FocusSearchBox();
    }

    private void FileModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.SetMode(SearchOverlayMode.File);
        RefreshSearchInputHints();
        ShowModeToast();
        FocusSearchBox();
    }

    private async void SidebarWorkModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.ToggleSidebarWorkModeAsync();
        _viewModel.RefreshSidebarModePresentation();
        FocusSearchBox();
    }

    private async void ResultButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not int number)
        {
            return;
        }

        await SendResultAsync(number);
    }

    private void ResultRow_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (GetResultFromSender(sender) is { } result)
        {
            ApplyResultRightClickSelection(result);
            _viewModel.SelectedResult = result;
        }
    }

    private void ResultContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu || contextMenu.DataContext is not SearchOverlayResultItem result)
        {
            return;
        }

        BuildResultContextMenu(contextMenu, result);
    }

    private async void EditResultMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var result = GetResultFromSender(sender);
        if (result?.Phrase is null)
        {
            return;
        }

        var editor = new PhraseEditorWindow("编辑短语", "保存", result.Phrase.DisplayTitle, result.Phrase.Content, _viewModel.GetPhraseImageAbsolutePaths(result.Phrase))
        {
            Owner = this
        };

        var dialogResult = editor.ShowDialog();
        SearchOverlayDiagnostics.Log(
            "phrase_images",
            "search_overlay_edit_dialog_closed",
            new Dictionary<string, object?>
            {
                ["phraseId"] = result.Phrase.Id.ToString("N"),
                ["dialogResult"] = dialogResult
            });
        if (dialogResult != true)
        {
            FocusSearchBox();
            return;
        }

        try
        {
            await _viewModel.UpdatePhraseResultAsync(result, editor.PhraseTitle, editor.PhraseContent, editor.PhraseImageSourcePaths, editor.RemovePhraseImages);
        }
        catch (Exception ex)
        {
            editor.CleanupOwnedTransientImages();
            _viewModel.StatusMessage = $"编辑短语失败：{ex.Message}";
            FocusSearchBox();
            return;
        }
        FocusSearchBox();
    }

    private void OpenResultMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var result = GetResultFromSender(sender);
        if (result is null)
        {
            return;
        }

        _viewModel.OpenFileResult(result);
        FocusSearchBox();
    }

    private async void CopyResultMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var result = GetResultFromSender(sender);
        if (result is null)
        {
            return;
        }

        var copyResult = await _viewModel.CopyResultAsync(result.Number);
        if (copyResult.Success)
        {
            ReturnFocusToExternalWindow();
            return;
        }

        FocusSearchBox();
    }

    private async void CopyResultImagesMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var result = GetResultFromSender(sender);
        if (result is null)
        {
            return;
        }

        var copyResult = await _viewModel.CopyResultImagesAsync(result.Number);
        if (copyResult.Success)
        {
            ReturnFocusToExternalWindow();
            return;
        }

        FocusSearchBox();
    }

    private async void InputResultMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var result = GetResultFromSender(sender);
        if (result is null)
        {
            return;
        }

        var actionResult = await _viewModel.InputResultAsync(result.Number);
        if (actionResult.Success)
        {
            HideOverlay();
            return;
        }

        ActivateWindowForInput();
        FocusSearchBox();
    }

    private async void DeleteResultMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var result = GetResultFromSender(sender);
        if (result is null)
        {
            return;
        }

        var message = result.Mode == SearchOverlayMode.File
            ? $"删除后会从文件库移除：{result.FileItem?.DisplayName}，确定继续吗？"
            : $"删除后会移除短语：{result.Phrase?.DisplayTitle ?? result.Phrase?.Content}，确定继续吗？";

        var confirm = System.Windows.MessageBox.Show(
            this,
            message,
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            FocusSearchBox();
            return;
        }

        await _viewModel.DeleteResultAsync(result.Number);
        FocusSearchBox();
    }

    private void ApplyResultRightClickSelection(SearchOverlayResultItem result)
    {
        if (result.Phrase is null)
        {
            _viewModel.ClearPhraseMultiSelection();
            _phraseMultiSelectAnchor = null;
            return;
        }

        var visiblePhrases = _viewModel.Results
            .Where(item => item.Phrase is not null)
            .Select(item => item.Phrase!)
            .ToList();

        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            SelectResultPhraseRange(result.Phrase, visiblePhrases);
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result.Phrase.IsMultiSelected = !result.Phrase.IsMultiSelected;
            _phraseMultiSelectAnchor = result.Phrase;
            return;
        }

        var selectedPhrases = _viewModel.GetMultiSelectedPhrases();
        if (selectedPhrases.Count > 1 && result.Phrase.IsMultiSelected)
        {
            _phraseMultiSelectAnchor = result.Phrase;
            return;
        }

        _viewModel.ClearPhraseMultiSelection();
        result.Phrase.IsMultiSelected = true;
        _phraseMultiSelectAnchor = result.Phrase;
    }

    private void SelectResultPhraseRange(ReplyPhrase phrase, IReadOnlyList<ReplyPhrase> orderedPhrases)
    {
        if (_phraseMultiSelectAnchor is null)
        {
            _viewModel.ClearPhraseMultiSelection();
            phrase.IsMultiSelected = true;
            _phraseMultiSelectAnchor = phrase;
            return;
        }

        var anchorIndex = orderedPhrases.ToList().FindIndex(item => item.Id == _phraseMultiSelectAnchor.Id);
        var targetIndex = orderedPhrases.ToList().FindIndex(item => item.Id == phrase.Id);
        if (anchorIndex < 0 || targetIndex < 0)
        {
            _viewModel.ClearPhraseMultiSelection();
            phrase.IsMultiSelected = true;
            _phraseMultiSelectAnchor = phrase;
            return;
        }

        _viewModel.ClearPhraseMultiSelection();
        var start = Math.Min(anchorIndex, targetIndex);
        var end = Math.Max(anchorIndex, targetIndex);
        for (var index = start; index <= end; index++)
        {
            orderedPhrases[index].IsMultiSelected = true;
        }
    }

    private void BuildResultContextMenu(ContextMenu contextMenu, SearchOverlayResultItem currentResult)
    {
        contextMenu.Items.Clear();

        if (currentResult.Phrase is not null)
        {
            var selectedPhrases = _viewModel.GetMultiSelectedPhrases();
            if (selectedPhrases.Count > 1 && currentResult.Phrase.IsMultiSelected)
            {
                var moveMenuItem = new MenuItem { Header = "批量移动到" };
                foreach (var category in _viewModel.GetCategories().OrderBy(item => item.SortOrder).ThenBy(item => item.Name))
                {
                    var categoryMenuItem = new MenuItem { Header = category.Name };
                    categoryMenuItem.Click += async (_, _) => await MoveSelectedPhrasesAsync(selectedPhrases, category);
                    moveMenuItem.Items.Add(categoryMenuItem);
                }

                contextMenu.Items.Add(moveMenuItem);
                contextMenu.Items.Add(new Separator());

                var deleteMenuItem = new MenuItem { Header = $"批量删除（{selectedPhrases.Count}）" };
                deleteMenuItem.Click += async (_, _) => await DeleteSelectedPhrasesAsync(selectedPhrases);
                contextMenu.Items.Add(deleteMenuItem);
                return;
            }

            contextMenu.Items.Add(CreateResultMenuItem("编辑", EditResultMenuItem_OnClick));
            contextMenu.Items.Add(new Separator());
            var imagePaths = _viewModel.GetPhraseImageAbsolutePaths(currentResult.Phrase);
            if (imagePaths.Count > 0)
            {
                contextMenu.Items.Add(CreateResultMenuItem("预览图片", PreviewResultImagesMenuItem_OnClick));
                contextMenu.Items.Add(CreateResultMenuItem("仅复制图片", CopyResultImagesMenuItem_OnClick));
            }
            contextMenu.Items.Add(CreateResultMenuItem("复制", CopyResultMenuItem_OnClick));
            contextMenu.Items.Add(CreateResultMenuItem("仅输入不发送", InputResultMenuItem_OnClick));
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(CreateResultMenuItem("删除", DeleteResultMenuItem_OnClick));
            return;
        }

        if (currentResult.FileItem is not null)
        {
            contextMenu.Items.Add(CreateResultMenuItem("打开", OpenResultMenuItem_OnClick));
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(CreateResultMenuItem("复制", CopyResultMenuItem_OnClick));
            contextMenu.Items.Add(CreateResultMenuItem("仅输入不发送", InputResultMenuItem_OnClick));
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(CreateResultMenuItem("删除", DeleteResultMenuItem_OnClick));
        }
    }

    private async Task MoveSelectedPhrasesAsync(IReadOnlyList<ReplyPhrase> selectedPhrases, ReplyCategory targetCategory)
    {
        if (selectedPhrases.Count == 0)
        {
            return;
        }

        await _viewModel.MovePhrasesToCategoryAsync(selectedPhrases, targetCategory);
        _phraseMultiSelectAnchor = null;
        FocusSearchBox();
    }

    private async Task DeleteSelectedPhrasesAsync(IReadOnlyList<ReplyPhrase> selectedPhrases)
    {
        if (selectedPhrases.Count == 0)
        {
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            this,
            $"确定删除这 {selectedPhrases.Count} 条快捷短语吗？",
            "确认批量删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            FocusSearchBox();
            return;
        }

        await _viewModel.DeletePhrasesAsync(selectedPhrases);
        _phraseMultiSelectAnchor = null;
        FocusSearchBox();
    }

    private static MenuItem CreateResultMenuItem(string header, RoutedEventHandler clickHandler)
    {
        var menuItem = new MenuItem { Header = header };
        menuItem.Click += clickHandler;
        return menuItem;
    }

    private void PreviewResultImagesMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var result = GetResultFromSender(sender);
        if (result?.Phrase is null)
        {
            return;
        }

        ShowResultImagePreviewPopup(result.Phrase, GetContextMenuPlacementTarget(sender));
        FocusSearchBox();
    }

    private void SearchOverlayWindow_OnDeactivated(object? sender, EventArgs e)
    {
        if (_isPrewarming)
        {
            return;
        }

        SearchOverlayDiagnostics.Log("search_overlay", "window_deactivated", BuildOverlayStatePayload());
        if (_isReturningFocusToExternalWindow)
        {
            SearchOverlayDiagnostics.Log("search_overlay", "window_deactivated_ignored_return_focus", BuildOverlayStatePayload());
            return;
        }

        if (_isSettingsPreviewMode)
        {
            return;
        }

        if (_isSending)
        {
            return;
        }

        if (ShouldDeferActivationRecoveryUntilTabReleased())
        {
            SearchOverlayDiagnostics.Log("search_overlay", "window_deactivated_ignored_tab_held", BuildOverlayStatePayload());
            return;
        }

        if (IsOverlaySessionForeground(NativeMethods.GetForegroundWindow()))
        {
            return;
        }

        if (TryRecoverInputFocus("deactivated"))
        {
            return;
        }

        HideOverlay("deactivated");
    }

    private void ResultPreviewToolTip_OnOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button resultButton ||
            resultButton.DataContext is not SearchOverlayResultItem { Phrase: { } phrase } ||
            resultButton.ToolTip is not System.Windows.Controls.ToolTip toolTip)
        {
            e.Handled = true;
            return;
        }

        var imagePaths = _viewModel.GetPhraseImageAbsolutePaths(phrase);
        if (imagePaths.Count == 0)
        {
            e.Handled = true;
            return;
        }

        toolTip.Content = PhraseImagePreviewContentFactory.BuildHoverContent(phrase, imagePaths, includeTextPreview: true);
    }

    private void SearchOverlayWindow_OnActivated(object? sender, EventArgs e)
    {
        SearchOverlayDiagnostics.Log("search_overlay", "window_activated", BuildOverlayStatePayload());

        if (_isSettingsPreviewMode ||
            !IsVisible ||
            IsReadyForDirectTyping())
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isSettingsPreviewMode ||
                !IsVisible ||
                IsReadyForDirectTyping())
            {
                return;
            }

            var payload = BuildOverlayStatePayload();
            payload["phase"] = "activated_refocus";
            SearchOverlayDiagnostics.Log("search_overlay", "activation_recovery_started", payload);
            FocusSearchBox();
        }), DispatcherPriority.Input);
    }

    private void SearchOverlayWindow_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var payload = BuildOverlayStatePayload();
        payload["newVisibility"] = e.NewValue;
        SearchOverlayDiagnostics.Log("search_overlay", "window_visibility_changed", payload);
    }

    private void SearchOverlayWindow_OnContentRendered(object? sender, EventArgs e)
    {
        SearchOverlayDiagnostics.Log("search_overlay", "window_content_rendered", BuildOverlayStatePayload());
    }

    private void SearchTextBox_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var payload = BuildOverlayStatePayload();
        payload["oldFocusTargetType"] = e.OldFocus?.GetType().FullName ?? string.Empty;
        payload["newFocusTargetType"] = e.NewFocus?.GetType().FullName ?? string.Empty;
        SearchOverlayDiagnostics.Log("search_overlay", "search_box_got_keyboard_focus", payload);
    }

    private void SearchTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var payload = BuildOverlayStatePayload();
        payload["oldFocusTargetType"] = e.OldFocus?.GetType().FullName ?? string.Empty;
        payload["newFocusTargetType"] = e.NewFocus?.GetType().FullName ?? string.Empty;
        SearchOverlayDiagnostics.Log("search_overlay", "search_box_lost_keyboard_focus", payload);
    }

    private async Task SendResultAsync(int number)
    {
        if (_isSending)
        {
            return;
        }

        if (_viewModel.GetResultByNumber(number) is null)
        {
            return;
        }

        var restoreState = CaptureOverlayRestoreState();
        _isSending = true;
        HideOverlay("result_send", animate: false);
        try
        {
            var result = await _viewModel.SendResultAsync(number);
            if (result.Success)
            {
                return;
            }

            RestoreOverlayAfterSendFailure(restoreState, result);
        }
        finally
        {
            _isSending = false;
        }
    }

    private async Task SendSearchTextAsync()
    {
        if (_isSending)
        {
            return;
        }

        var pendingSearchText = _viewModel.SearchText;
        if (string.IsNullOrWhiteSpace(pendingSearchText))
        {
            FocusSearchBox();
            return;
        }

        var restoreState = CaptureOverlayRestoreState();
        _isSending = true;
        HideOverlay("plain_text_send", animate: false);

        try
        {
            var result = await _viewModel.SendSearchTextAsync();
            if (result.Success)
            {
                return;
            }

            RestoreOverlayAfterSendFailure(restoreState with { SearchText = pendingSearchText }, result);
        }
        finally
        {
            _isSending = false;
        }
    }

    private async Task SaveSearchTextAsPhraseAsync()
    {
        var result = await _viewModel.SaveSearchTextAsPhraseAsync();
        if (result.Success)
        {
            ShowTransientToast(result.Message.Contains("已存在", StringComparison.Ordinal)
                ? "已存在快捷短语"
                : "已保存快捷短语");
        }

        FocusSearchBox();
    }

    private async Task SendSearchTextAndSaveAsPhraseAsync()
    {
        if (_isSending)
        {
            return;
        }

        var pendingSearchText = _viewModel.SearchText;
        if (string.IsNullOrWhiteSpace(pendingSearchText))
        {
            FocusSearchBox();
            return;
        }

        var restoreState = CaptureOverlayRestoreState();
        _isSending = true;
        HideOverlay("plain_text_send_and_save", animate: false);

        try
        {
            var (sendResult, saveResult) = await _viewModel.SendSearchTextAndSaveAsPhraseAsync();
            if (!sendResult.Success)
            {
                RestoreOverlayAfterSendFailure(restoreState with { SearchText = pendingSearchText }, sendResult);
                return;
            }

            if (!saveResult.Success)
            {
                App.Instance?.ShowBackgroundNotification("快捷短语", "发送成功，但保存快捷短语失败。", isWarning: true);
                return;
            }

            App.Instance?.ShowBackgroundNotification(
                "快捷短语",
                saveResult.Message.Contains("已存在", StringComparison.Ordinal)
                    ? "已存在快捷短语"
                    : "已保存为快捷短语");
        }
        finally
        {
            _isSending = false;
        }
    }

    private void ReturnFocusToExternalWindow()
    {
        var targetWindowHandle = _overlayTargetWindowHandle != IntPtr.Zero
            ? _overlayTargetWindowHandle
            : _windowTracker.GetLastExternalWindowHandle();
        var targetFocusHandle = _overlayTargetFocusHandle;
        var targetCaretHandle = _overlayTargetCaretHandle;
        var targetCaretRect = _overlayTargetCaretRect;
        var targetHasCaretRect = _overlayTargetHasCaretRect;
        var targetThreadId = _overlayTargetThreadId;
        var verificationSessionId = _overlaySessionId;
        _isReturningFocusToExternalWindow = true;

        try
        {
            HideOverlay("return_focus");
            RestoreExternalWindowInputFocus(
                targetWindowHandle,
                targetFocusHandle,
                targetCaretHandle,
                targetCaretRect,
                targetHasCaretRect,
                targetThreadId,
                verificationSessionId);
        }
        finally
        {
            _isReturningFocusToExternalWindow = false;
        }
    }

    public void ReturnFocusToExternalWindowFromHotkey()
    {
        ReturnFocusToExternalWindow();
    }

    public void ReassertSearchInputFocusFromHotkey()
    {
        if (!IsSearchSessionOpen)
        {
            return;
        }

        ActivateWindowForInput();
    }

    private void HideOverlay(string reason = "hide", bool animate = true)
    {
        _placementTimer.Stop();
        HideModeToast(immediate: true);
        _activationRequestVersion++;
        _activationRecoveryAttempts = 0;

        var hidePayload = BuildOverlayStatePayload();
        hidePayload["reason"] = reason;
        SearchOverlayDiagnostics.Log("search_overlay", "overlay_hidden", hidePayload);

        if (IsVisible)
        {
            if (animate)
            {
                RunCloseAnimation();
            }
            else
            {
                _overlayClosingSessionId = 0;
                Hide();
                ResetOpenAnimationState(hidden: true);
            }
        }
        else
        {
            ResetOpenAnimationState(hidden: true);
        }

        _overlayTargetWindowHandle = IntPtr.Zero;
        _overlayTargetFocusHandle = IntPtr.Zero;
        _overlayTargetCaretHandle = IntPtr.Zero;
        _overlayTargetCaretRect = default;
        _overlayTargetHasCaretRect = false;
        _overlayTargetThreadId = 0;
    }

    private OverlayRestoreState CaptureOverlayRestoreState()
    {
        return new OverlayRestoreState(
            _overlayTargetWindowHandle != IntPtr.Zero
                ? _overlayTargetWindowHandle
                : _windowTracker.GetLastExternalWindowHandle(),
            _viewModel.CurrentMode,
            _viewModel.SearchText);
    }

    private void RestoreOverlayAfterSendFailure(OverlayRestoreState state, ReplyActionResult result)
    {
        if (state.TargetWindowHandle == IntPtr.Zero)
        {
            return;
        }

        if (ShouldKeepOverlayHiddenAfterSendFailure(result))
        {
            _viewModel.ShowActionStatus(result);
            App.Instance?.ShowBackgroundNotification(
                "快捷短语",
                SearchOverlayActionFeedbackFormatter.BuildMessage(result),
                isWarning: true);
            return;
        }

        ShowForSearch(state.TargetWindowHandle);
        _viewModel.SetMode(state.Mode);
        _viewModel.SearchText = state.SearchText;
        _viewModel.ShowActionStatus(result);
        ActivateWindowForInput();
        FocusSearchBox();
    }

    private static bool ShouldKeepOverlayHiddenAfterSendFailure(ReplyActionResult result)
    {
        if (result.Success)
        {
            return false;
        }

        return result.FailureKind is
            ReplyActionFailureKind.MissingTargetWindow or
            ReplyActionFailureKind.ActivationFailed or
            ReplyActionFailureKind.FocusLost or
            ReplyActionFailureKind.TargetChanged;
    }

    private void RunOpenAnimation()
    {
        _overlayClosingSessionId = 0;
        var animationVersion = ++_overlayAnimationVersion;
        OverlayChrome.BeginAnimation(UIElement.OpacityProperty, null);

        if (OverlayChrome.RenderTransform is not ScaleTransform scaleTransform)
        {
            scaleTransform = new ScaleTransform(1d, 1d);
            OverlayChrome.RenderTransform = scaleTransform;
        }

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scaleTransform.ScaleX = OverlayHiddenScale;
        scaleTransform.ScaleY = OverlayHiddenScale;
        OverlayChrome.Opacity = 0d;

        var startPayload = BuildOverlayStatePayload();
        startPayload["animationVersion"] = animationVersion;
        startPayload["animationPhase"] = "open";
        SearchOverlayDiagnostics.Log("search_overlay", "open_animation_started", startPayload);

        var fadeIn = new DoubleAnimation(1d, OverlayFadeInDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        fadeIn.Completed += (_, _) =>
        {
            if (animationVersion != _overlayAnimationVersion)
            {
                return;
            }

            OverlayChrome.Opacity = 1d;
            var completedPayload = BuildOverlayStatePayload();
            completedPayload["animationVersion"] = animationVersion;
            completedPayload["animationPhase"] = "open";
            SearchOverlayDiagnostics.Log("search_overlay", "open_animation_completed", completedPayload);
        };
        OverlayChrome.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        var scaleIn = new DoubleAnimation(1d, OverlayFadeInDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        scaleIn.Completed += (_, _) =>
        {
            if (animationVersion != _overlayAnimationVersion)
            {
                return;
            }

            scaleTransform.ScaleX = 1d;
            scaleTransform.ScaleY = 1d;
        };
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn.Clone());
    }

    private async Task TrackActivationDiagnosticsAsync(int overlaySessionId)
    {
        var checkpoints = new[] { 50, 120, 250, 500, 1000, 2000 };
        var elapsedCheckpoint = 0;

        foreach (var checkpointMs in checkpoints)
        {
            await Task.Delay(checkpointMs - elapsedCheckpoint);
            elapsedCheckpoint = checkpointMs;

            await Dispatcher.InvokeAsync(() =>
            {
                if (overlaySessionId != _overlaySessionId || _isSettingsPreviewMode)
                {
                    return;
                }

                var payload = BuildOverlayStatePayload();
                payload["checkpointMs"] = checkpointMs;
                payload["directTypingReady"] = IsReadyForDirectTyping();
                SearchOverlayDiagnostics.Log("search_overlay", "activation_checkpoint", payload);
            }, DispatcherPriority.Background);
        }
    }

    private void RunCloseAnimation()
    {
        _overlayClosingSessionId = _overlaySessionId;
        var animationVersion = ++_overlayAnimationVersion;
        OverlayChrome.BeginAnimation(UIElement.OpacityProperty, null);

        if (OverlayChrome.RenderTransform is not ScaleTransform scaleTransform)
        {
            scaleTransform = new ScaleTransform(1d, 1d);
            OverlayChrome.RenderTransform = scaleTransform;
        }

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        var fadeOut = new DoubleAnimation(0d, OverlayFadeOutDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        fadeOut.Completed += (_, _) =>
        {
            if (animationVersion != _overlayAnimationVersion)
            {
                return;
            }

            Hide();
            _overlayClosingSessionId = 0;
            ResetOpenAnimationState(hidden: true);
        };
        OverlayChrome.BeginAnimation(UIElement.OpacityProperty, fadeOut);

        var scaleOut = new DoubleAnimation(OverlayHiddenScale, OverlayFadeOutDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        scaleOut.Completed += (_, _) =>
        {
            if (animationVersion != _overlayAnimationVersion)
            {
                return;
            }

            scaleTransform.ScaleX = OverlayHiddenScale;
            scaleTransform.ScaleY = OverlayHiddenScale;
        };
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleOut);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleOut.Clone());
    }

    private void ResetOpenAnimationState(bool hidden = false)
    {
        OverlayChrome.BeginAnimation(UIElement.OpacityProperty, null);

        if (OverlayChrome.RenderTransform is ScaleTransform scaleTransform)
        {
            scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scaleTransform.ScaleX = hidden ? OverlayHiddenScale : 1d;
            scaleTransform.ScaleY = hidden ? OverlayHiddenScale : 1d;
        }

        OverlayChrome.Opacity = hidden ? 0d : 1d;
    }

    private readonly record struct OverlayRestoreState(
        IntPtr TargetWindowHandle,
        SearchOverlayMode Mode,
        string SearchText);

    private void PlacementTimer_OnTick(object? sender, EventArgs e)
    {
        if (_isSettingsPreviewMode)
        {
            if (_previewAnchorWindow is null || !_previewAnchorWindow.IsVisible)
            {
                EndSettingsPreview();
                return;
            }

            UpdatePlacement();
            return;
        }

        if (!IsVisible || _isSending)
        {
            return;
        }

        if (ShouldDeferActivationRecoveryUntilTabReleased())
        {
            UpdatePlacement();
            return;
        }

        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            if (TryRecoverInputFocus("placement_foreground_empty"))
            {
                return;
            }

            HideOverlay("placement_foreground_empty");
            return;
        }

        if (!IsOverlaySessionForeground(foregroundWindow))
        {
            if (TryRecoverInputFocus("placement_foreground_mismatch"))
            {
                return;
            }

            HideOverlay("placement_foreground_mismatch");
            return;
        }

        UpdatePlacement();
    }

    private void UpdatePlacement()
    {
        var activeSettings = _previewSettings ?? _viewModel.Settings;
        if (_isSettingsPreviewMode && _previewAnchorWindow is not null)
        {
            var workArea = SystemParameters.WorkArea;
            var referenceWidth = Math.Max(620d, _previewAnchorWindow.ActualWidth - 48d);
            var previewWidth = Math.Max(MinWidth, Math.Min(referenceWidth, workArea.Width - SearchOverlayScreenMargin));
            var previewHeight = SearchOverlayPlacementStrategy.CalculateOverlayHeight(
                activeSettings.SearchOverlayHeightPercent,
                activeSettings.SearchInputBoxHeight);
            var previewLeft = Math.Max(workArea.Left + 12d, Math.Min(_previewAnchorWindow.Left + 20d, workArea.Right - previewWidth - 12d));
            var previewTop = _previewAnchorWindow.Top + _previewAnchorWindow.ActualHeight + 10d;
            if (previewTop + previewHeight > workArea.Bottom - 12d)
            {
                previewTop = Math.Max(workArea.Top + 12d, _previewAnchorWindow.Top - previewHeight - 10d);
            }

            Width = previewWidth;
            Height = previewHeight;
            Left = previewLeft;
            Top = previewTop;
            ApplySearchInputSizing(previewWidth, activeSettings);
            return;
        }

        if (!TryGetOverlayTargetWindowInfo(out var targetInfo) || targetInfo is null)
        {
            return;
        }

        var screenWorkingArea = Forms.Screen.FromHandle(targetInfo.Handle).WorkingArea;
        var caretAnchor = TryCreateOverlayTargetCaretAnchor();
        var placement = SearchOverlayPlacementStrategy.Calculate(
            targetInfo,
            new SearchOverlayWorkingArea(
                screenWorkingArea.Left,
                screenWorkingArea.Top,
                screenWorkingArea.Right,
                screenWorkingArea.Bottom),
            new SearchOverlayPlacementOptions(
                MinWidth,
                activeSettings.SearchOverlayWidthPercent,
                activeSettings.SearchOverlayHeightPercent,
                activeSettings.SearchInputBoxHeight),
            caretAnchor);

        Width = placement.Width;
        Height = placement.Height;
        Left = placement.Left;
        Top = placement.Top;
        ApplySearchInputSizing(placement.Width, activeSettings);
    }

    private void ApplySearchInputSizing(double overlayWidth, AppSettings activeSettings)
    {
        var inputHeight = Math.Clamp(activeSettings.SearchInputBoxHeight, 40, 64);
        var inputFontSize = inputHeight >= 52 ? 14d : 13d;
        var overlayHorizontalPadding = OverlayChrome.Padding.Left + OverlayChrome.Padding.Right;
        var availableWidth = Math.Max(220d, overlayWidth - overlayHorizontalPadding);
        var desiredInputWidth = availableWidth * (activeSettings.SearchInputWidthPercent / 100d);

        SearchInputHost.Width = Math.Clamp(desiredInputWidth, Math.Min(220d, availableWidth), availableWidth);
        SearchInputChrome.Height = inputHeight;
        SearchTextBox.Height = Math.Max(24d, inputHeight - 4d);
        SearchTextBox.FontSize = inputFontSize;
    }

    private void ActivateWindowForInput()
    {
        EnsureWindowHandleCreated();
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var activationRequestVersion = ++_activationRequestVersion;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        var activationPayload = BuildOverlayStatePayload();
        activationPayload["activationRequestVersion"] = activationRequestVersion;
        activationPayload["phase"] = "fast";
        SearchOverlayDiagnostics.Log("search_overlay", "activation_started", activationPayload);

        Activate();
        FocusSearchBox();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (activationRequestVersion != _activationRequestVersion ||
                !IsVisible ||
                _isSettingsPreviewMode ||
                IsReadyForDirectTyping())
            {
                return;
            }

            var fallbackPayload = BuildOverlayStatePayload();
            fallbackPayload["activationRequestVersion"] = activationRequestVersion;
            fallbackPayload["phase"] = "fallback";
            SearchOverlayDiagnostics.Log("search_overlay", "activation_fallback_started", fallbackPayload);
            ForceActivateWindowForInput();
            FocusSearchBox();
        }), DispatcherPriority.Input);

        if (ShouldDeferActivationRecoveryUntilTabReleased())
        {
            return;
        }

        _ = ScheduleActivationFocusRecoveryAsync(activationRequestVersion, delayMs: 80, forceActivate: false);
        _ = ScheduleActivationFocusRecoveryAsync(activationRequestVersion, delayMs: 170, forceActivate: true);
        _ = ScheduleActivationFocusRecoveryAsync(activationRequestVersion, delayMs: 300, forceActivate: true);
    }

    private async Task ScheduleActivationFocusRecoveryAsync(
        int activationRequestVersion,
        int delayMs,
        bool forceActivate)
    {
        await Task.Delay(delayMs);
        await Dispatcher.InvokeAsync(() =>
        {
            if (activationRequestVersion != _activationRequestVersion ||
                !IsVisible ||
                _isSettingsPreviewMode ||
                IsReadyForDirectTyping())
            {
                return;
            }

            if (ShouldDeferActivationRecoveryUntilTabReleased())
            {
                return;
            }

            var payload = BuildOverlayStatePayload();
            payload["activationRequestVersion"] = activationRequestVersion;
            payload["phase"] = forceActivate ? "deferred_force" : "deferred_focus";
            payload["delayMs"] = delayMs;
            SearchOverlayDiagnostics.Log("search_overlay", "activation_recovery_started", payload);

            if (forceActivate)
            {
                ForceActivateWindowForInput();
            }
            else
            {
                Activate();
            }

            FocusSearchBox();
        }, DispatcherPriority.Input);
    }

    private bool IsReadyForDirectTyping()
    {
        return IsVisible &&
               IsActive &&
               IsOverlayForegroundWindow() &&
               SearchTextBox.IsLoaded &&
               SearchTextBox.IsKeyboardFocusWithin;
    }

    private async Task WaitForTabReleaseAsync(int overlaySessionId)
    {
        while (_suppressTabUntilRelease && overlaySessionId == _overlaySessionId)
        {
            await Task.Delay(25);
            if (!IsTabPhysicallyPressed())
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (overlaySessionId == _overlaySessionId)
                    {
                        _suppressTabUntilRelease = false;
                        if (IsVisible &&
                            !_isSettingsPreviewMode &&
                            !IsReadyForDirectTyping())
                        {
                            ActivateWindowForInput();
                        }
                    }
                }, DispatcherPriority.Input);
                return;
            }
        }
    }

    private bool IsOverlayForegroundWindow()
    {
        return _windowHandle != IntPtr.Zero &&
               NormalizeTopLevelWindow(NativeMethods.GetForegroundWindow()) == _windowHandle;
    }

    private void EnsureWindowHandleCreated()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            return;
        }

        _windowHandle = new WindowInteropHelper(this).EnsureHandle();
        SearchOverlayDiagnostics.Log("search_overlay", "window_handle_ensured", BuildOverlayStatePayload());
    }

    private void PrewarmOverlayPresentation()
    {
        if (_presentationWarmedUp || _isPrewarming || IsVisible || _isSettingsPreviewMode)
        {
            return;
        }

        EnsureWindowHandleCreated();
        var stopwatch = Stopwatch.StartNew();
        var originalLeft = Left;
        var originalTop = Top;
        var originalOpacity = Opacity;
        var originalShowActivated = ShowActivated;

        try
        {
            _isPrewarming = true;
            ShowActivated = false;
            Opacity = 0d;
            Left = -20000d;
            Top = -20000d;
            SearchOverlayDiagnostics.Log("search_overlay", "presentation_prewarm_started", BuildOverlayStatePayload());
            Show();
            UpdateLayout();
            Hide();
            _presentationWarmedUp = true;
        }
        finally
        {
            ShowActivated = originalShowActivated;
            Opacity = originalOpacity;
            Left = originalLeft;
            Top = originalTop;
            _isPrewarming = false;
            ResetOpenAnimationState();
            HideModeToast(immediate: true);
        }

        stopwatch.Stop();
        var payload = BuildOverlayStatePayload();
        payload["elapsedMs"] = stopwatch.ElapsedMilliseconds;
        SearchOverlayDiagnostics.Log("search_overlay", "presentation_prewarm_completed", payload);
    }

    private bool IsOverlaySessionForeground(IntPtr windowHandle)
    {
        var normalizedWindow = NormalizeTopLevelWindow(windowHandle);
        return normalizedWindow == _windowHandle ||
               (_overlayTargetWindowHandle != IntPtr.Zero && normalizedWindow == _overlayTargetWindowHandle);
    }

    private bool TryGetOverlayTargetWindowInfo(out WindowDockTargetInfo? windowInfo)
    {
        if (_overlayTargetWindowHandle != IntPtr.Zero &&
            _windowTracker.TryGetWindowInfo(_overlayTargetWindowHandle, out windowInfo) &&
            windowInfo is not null)
        {
            return true;
        }

        return _windowTracker.TryGetLastExternalWindowInfo(out windowInfo);
    }

    private IntPtr ResolveOverlayTargetWindowHandle(IntPtr requestedTargetWindowHandle)
    {
        var handle = requestedTargetWindowHandle != IntPtr.Zero
            ? requestedTargetWindowHandle
            : _windowTracker.GetLastExternalWindowHandle();
        return NormalizeTopLevelWindow(handle);
    }

    private static IntPtr NormalizeTopLevelWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var rootOwner = NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRootowner);
        if (rootOwner != IntPtr.Zero)
        {
            return rootOwner;
        }

        var root = NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRoot);
        return root != IntPtr.Zero ? root : windowHandle;
    }

    private void ForceActivateWindowForInput()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        var currentThreadId = NativeMethods.GetCurrentThreadId();
        var foregroundThreadId = foregroundWindow != IntPtr.Zero
            ? NativeMethods.GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero)
            : 0;
        var attachedToForeground = false;

        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                attachedToForeground = NativeMethods.AttachThreadInput(foregroundThreadId, currentThreadId, true);
            }

            NativeMethods.ShowWindow(_windowHandle, NativeMethods.SwRestore);
            NativeMethods.SetForegroundWindow(_windowHandle);
            NativeMethods.SetActiveWindow(_windowHandle);
            NativeMethods.SetFocus(_windowHandle);
        }
        finally
        {
            if (attachedToForeground)
            {
                NativeMethods.AttachThreadInput(foregroundThreadId, currentThreadId, false);
            }
        }
    }

    private void CaptureOverlayTargetFocus()
    {
        _overlayTargetFocusHandle = IntPtr.Zero;
        _overlayTargetCaretHandle = IntPtr.Zero;
        _overlayTargetCaretRect = default;
        _overlayTargetHasCaretRect = false;
        _overlayTargetThreadId = 0;

        if (_overlayTargetWindowHandle == IntPtr.Zero)
        {
            return;
        }

        var targetThreadId = NativeMethods.GetWindowThreadProcessId(_overlayTargetWindowHandle, IntPtr.Zero);
        if (targetThreadId == 0)
        {
            return;
        }

        var guiInfo = new NativeMethods.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        if (!NativeMethods.GetGUIThreadInfo(targetThreadId, ref guiInfo))
        {
            return;
        }

        _overlayTargetThreadId = targetThreadId;
        _overlayTargetFocusHandle = guiInfo.hwndFocus != IntPtr.Zero
            ? guiInfo.hwndFocus
            : _overlayTargetWindowHandle;
        _overlayTargetCaretHandle = guiInfo.hwndCaret;
        _overlayTargetCaretRect = guiInfo.rcCaret;
        _overlayTargetHasCaretRect = IsCaretRectUsable(guiInfo.rcCaret);

        var payload = BuildOverlayStatePayload();
        payload["capturedTargetFocusHandle"] = SearchOverlayDiagnostics.FormatHandle(_overlayTargetFocusHandle);
        payload["capturedTargetCaretHandle"] = SearchOverlayDiagnostics.FormatHandle(_overlayTargetCaretHandle);
        payload["capturedTargetCaretRect"] = FormatRect(_overlayTargetCaretRect);
        payload["capturedTargetHasCaretRect"] = _overlayTargetHasCaretRect;
        payload["capturedTargetThreadId"] = _overlayTargetThreadId;
        SearchOverlayDiagnostics.Log("search_overlay", "target_focus_captured", payload);
    }

    private void RestoreExternalWindowInputFocus()
    {
        var targetWindowHandle = _overlayTargetWindowHandle != IntPtr.Zero
            ? _overlayTargetWindowHandle
            : _windowTracker.GetLastExternalWindowHandle();
        RestoreExternalWindowInputFocus(
            targetWindowHandle,
            _overlayTargetFocusHandle,
            _overlayTargetCaretHandle,
            _overlayTargetCaretRect,
            _overlayTargetHasCaretRect,
            _overlayTargetThreadId,
            _overlaySessionId);
    }

    private void RestoreExternalWindowInputFocus(
        IntPtr targetWindowHandle,
        IntPtr targetFocusHandle,
        IntPtr targetCaretHandle,
        NativeMethods.RECT targetCaretRect,
        bool hasTargetCaretRect,
        uint targetThreadId,
        int verificationSessionId)
    {
        if (targetWindowHandle == IntPtr.Zero || !NativeMethods.IsWindow(targetWindowHandle))
        {
            _windowTracker.TryActivateLastExternalWindow();
            return;
        }

        if (NativeMethods.IsIconic(targetWindowHandle))
        {
            NativeMethods.ShowWindow(targetWindowHandle, NativeMethods.SwRestore);
        }

        var bringToTopResult = NativeMethods.BringWindowToTop(targetWindowHandle);
        var setForegroundResult = NativeMethods.SetForegroundWindow(targetWindowHandle);
        var setActiveResult = NativeMethods.SetActiveWindow(targetWindowHandle);

        if (targetFocusHandle == IntPtr.Zero || !NativeMethods.IsWindow(targetFocusHandle))
        {
            _windowTracker.RememberExternalWindow(targetWindowHandle);
            BeginVerifyExternalWindowInputFocusRestore(
                targetWindowHandle,
                targetFocusHandle,
                targetCaretHandle,
                targetCaretRect,
                hasTargetCaretRect,
                targetThreadId,
                verificationSessionId,
                bringToTopResult,
                setForegroundResult,
                setActiveResult != IntPtr.Zero,
                focusAttempted: false,
                focusPreviousHandle: IntPtr.Zero);
            return;
        }

        var currentThreadId = NativeMethods.GetCurrentThreadId();
        var resolvedTargetThreadId = targetThreadId != 0
            ? targetThreadId
            : NativeMethods.GetWindowThreadProcessId(targetFocusHandle, IntPtr.Zero);
        var attachedToTarget = false;
        IntPtr previousFocusHandle = IntPtr.Zero;

        try
        {
            if (resolvedTargetThreadId != 0 && resolvedTargetThreadId != currentThreadId)
            {
                attachedToTarget = NativeMethods.AttachThreadInput(resolvedTargetThreadId, currentThreadId, true);
            }

            previousFocusHandle = NativeMethods.SetFocus(targetFocusHandle);
        }
        finally
        {
            if (attachedToTarget)
            {
                NativeMethods.AttachThreadInput(resolvedTargetThreadId, currentThreadId, false);
            }
        }

        _windowTracker.RememberExternalWindow(targetWindowHandle);

        BeginVerifyExternalWindowInputFocusRestore(
            targetWindowHandle,
            targetFocusHandle,
            targetCaretHandle,
            targetCaretRect,
            hasTargetCaretRect,
            resolvedTargetThreadId,
            verificationSessionId,
            bringToTopResult,
            setForegroundResult,
            setActiveResult != IntPtr.Zero,
            focusAttempted: true,
            focusPreviousHandle: previousFocusHandle);
    }

    private void InstallMouseHook()
    {
        if (_mouseHookHandle != IntPtr.Zero)
        {
            return;
        }

        _mouseHookCallback = MouseHookCallback;
        _mouseHookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl,
            _mouseHookCallback,
            NativeMethods.GetModuleHandle(null),
            0);
    }

    private void InstallForegroundHook()
    {
        if (_foregroundHookHandle != IntPtr.Zero)
        {
            return;
        }

        _foregroundHookCallback = ForegroundHookCallback;
        _foregroundHookHandle = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground,
            NativeMethods.EventSystemForeground,
            IntPtr.Zero,
            _foregroundHookCallback,
            0,
            0,
            NativeMethods.WineventOutofcontext | NativeMethods.WineventSkipownprocess);
    }

    private void UninstallForegroundHook()
    {
        if (_foregroundHookHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWinEvent(_foregroundHookHandle);
        _foregroundHookHandle = IntPtr.Zero;
        _foregroundHookCallback = null;
    }

    private void ForegroundHookCallback(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (!ShouldLogForegroundTransitions())
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!ShouldLogForegroundTransitions())
            {
                return;
            }

            var payload = BuildOverlayStatePayload();
            payload["eventType"] = eventType;
            payload["eventThreadId"] = dwEventThread;
            payload["eventTimeMs"] = dwmsEventTime;
            SearchOverlayDiagnostics.AddWindowSnapshot(payload, "eventForeground", hwnd);
            SearchOverlayDiagnostics.Log("search_overlay", "foreground_changed", payload);
        }));
    }

    private bool ShouldLogForegroundTransitions()
    {
        return IsSearchSessionOpen || GetElapsedSinceShowMs() <= 2500;
    }

    private void UninstallMouseHook()
    {
        if (_mouseHookHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
        _mouseHookHandle = IntPtr.Zero;
        _mouseHookCallback = null;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsSearchSessionOpen && !_isSending)
        {
            var message = wParam.ToInt32();
            if (message is NativeMethods.WmLbuttondown or NativeMethods.WmRbuttondown or NativeMethods.WmMbuttondown or NativeMethods.WmNclbuttondown)
            {
                var mouseData = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    new Action(() => HandleGlobalMouseDown(mouseData.pt.X, mouseData.pt.Y)));
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private void HandleGlobalMouseDown(int screenX, int screenY)
    {
        if (!IsSearchSessionOpen)
        {
            return;
        }

        var isInsideOverlay = screenX >= Left &&
                              screenX <= Left + ActualWidth &&
                              screenY >= Top &&
                              screenY <= Top + ActualHeight;
        if (isInsideOverlay)
        {
            return;
        }

        HideOverlay("mouse_click_outside");
    }

    private void FocusSearchBox()
    {
        void FocusInput()
        {
            if (!IsVisible)
            {
                return;
            }

            FocusManager.SetFocusedElement(this, SearchTextBox);
            SearchTextBox.Focus();
            Keyboard.Focus(SearchTextBox);
            SearchTextBox.Select(SearchTextBox.Text.Length, 0);
            SearchTextBox.ScrollToEnd();

            if (!_inputReadyLogged && IsReadyForDirectTyping())
            {
                _inputReadyLogged = true;
                var focusPayload = BuildOverlayStatePayload();
                focusPayload["elapsedMs"] = GetElapsedSinceShowMs();
                SearchOverlayDiagnostics.Log("search_overlay", "search_box_ready_for_input", focusPayload);
            }
        }

        if (SearchTextBox.IsLoaded)
        {
            FocusInput();
        }

        Dispatcher.BeginInvoke(FocusInput, DispatcherPriority.Input);
    }

    private bool TryRecoverInputFocus(string reason)
    {
        if (_isReturningFocusToExternalWindow ||
            _isSettingsPreviewMode ||
            _isSending ||
            !IsVisible ||
            ShouldDeferActivationRecoveryUntilTabReleased() ||
            !IsWithinActivationGracePeriod() ||
            _activationRecoveryAttempts >= 2)
        {
            return false;
        }

        _activationRecoveryAttempts++;
        var recoverPayload = BuildOverlayStatePayload();
        recoverPayload["reason"] = reason;
        recoverPayload["recoveryAttempt"] = _activationRecoveryAttempts;
        recoverPayload["elapsedMs"] = GetElapsedSinceShowMs();
        SearchOverlayDiagnostics.Log("search_overlay", "activation_recovery_requested", recoverPayload);
        Dispatcher.BeginInvoke(new Action(ActivateWindowForInput), DispatcherPriority.Input);
        return true;
    }

    private bool IsWithinActivationGracePeriod()
    {
        return _lastShowStartedAt > 0 && GetElapsedSinceShowMs() <= 900;
    }

    private long GetElapsedSinceShowMs()
    {
        return _lastShowStartedAt <= 0 ? 0 : Math.Max(0, Environment.TickCount64 - _lastShowStartedAt);
    }

    private Dictionary<string, object?> BuildOverlayStatePayload()
    {
        var payload = SearchOverlayDiagnostics.CreateWindowSnapshot("overlay", _windowHandle);
        payload["overlaySessionId"] = _overlaySessionId;
        payload["isVisible"] = IsVisible;
        payload["isActive"] = IsActive;
        payload["isOverlayForeground"] = IsOverlayForegroundWindow();
        payload["isReadyForDirectTyping"] = IsReadyForDirectTyping();
        payload["directTypingState"] = DescribeDirectTypingState();
        payload["searchBoxFocused"] = SearchTextBox.IsKeyboardFocusWithin;
        payload["searchBoxIsFocused"] = SearchTextBox.IsFocused;
        payload["searchBoxIsLoaded"] = SearchTextBox.IsLoaded;
        payload["searchBoxIsVisible"] = SearchTextBox.IsVisible;
        payload["searchBoxIsEnabled"] = SearchTextBox.IsEnabled;
        payload["searchBoxFocusable"] = SearchTextBox.Focusable;
        payload["focusedElementType"] = FocusManager.GetFocusedElement(this)?.GetType().FullName ?? string.Empty;
        payload["overlayChromeOpacity"] = OverlayChrome.Opacity;
        payload["overlayChromeScaleX"] = OverlayChrome.RenderTransform is ScaleTransform scaleTransform ? scaleTransform.ScaleX : 1d;
        payload["overlayChromeScaleY"] = OverlayChrome.RenderTransform is ScaleTransform scaleTransformY ? scaleTransformY.ScaleY : 1d;
        payload["elapsedMs"] = GetElapsedSinceShowMs();
        payload["recoveryAttempts"] = _activationRecoveryAttempts;
        payload["isReturningFocusToExternalWindow"] = _isReturningFocusToExternalWindow;
        payload["overlayTargetHandle"] = SearchOverlayDiagnostics.FormatHandle(_overlayTargetWindowHandle);
        payload["overlayTargetFocusHandle"] = SearchOverlayDiagnostics.FormatHandle(_overlayTargetFocusHandle);
        payload["overlayTargetCaretHandle"] = SearchOverlayDiagnostics.FormatHandle(_overlayTargetCaretHandle);
        payload["overlayTargetHasCaretRect"] = _overlayTargetHasCaretRect;
        payload["overlayTargetCaretRect"] = FormatRect(_overlayTargetCaretRect);
        payload["overlayTargetThreadId"] = _overlayTargetThreadId;
        SearchOverlayDiagnostics.AddWindowSnapshot(payload, "foreground", NativeMethods.GetForegroundWindow());
        SearchOverlayDiagnostics.AddWindowSnapshot(payload, "trackedExternal", _windowTracker.GetLastExternalWindowHandle());
        SearchOverlayDiagnostics.AddWindowSnapshot(payload, "overlayTarget", _overlayTargetWindowHandle);
        SearchOverlayDiagnostics.AddGuiThreadSnapshot(payload, "overlayTargetGui", _overlayTargetThreadId);
        return payload;
    }

    private string DescribeDirectTypingState()
    {
        var blockers = new List<string>();

        if (!IsVisible)
        {
            blockers.Add("overlay_hidden");
        }

        if (!IsActive)
        {
            blockers.Add("window_inactive");
        }

        if (!IsOverlayForegroundWindow())
        {
            blockers.Add("foreground_not_overlay");
        }

        if (!SearchTextBox.IsLoaded)
        {
            blockers.Add("textbox_not_loaded");
        }

        if (!SearchTextBox.IsVisible)
        {
            blockers.Add("textbox_hidden");
        }

        if (!SearchTextBox.IsEnabled)
        {
            blockers.Add("textbox_disabled");
        }

        if (!SearchTextBox.Focusable)
        {
            blockers.Add("textbox_not_focusable");
        }

        if (!SearchTextBox.IsKeyboardFocusWithin)
        {
            blockers.Add("textbox_no_keyboard_focus");
        }

        return blockers.Count == 0 ? "ready" : string.Join("|", blockers);
    }

    private void BeginVerifyExternalWindowInputFocusRestore(
        IntPtr targetWindowHandle,
        IntPtr targetFocusHandle,
        IntPtr targetCaretHandle,
        NativeMethods.RECT targetCaretRect,
        bool hasTargetCaretRect,
        uint targetThreadId,
        int verificationSessionId,
        bool bringToTopResult,
        bool setForegroundResult,
        bool setActiveSucceeded,
        bool focusAttempted,
        IntPtr focusPreviousHandle)
    {
        var payload = SearchOverlayDiagnostics.CreateWindowSnapshot("target", targetWindowHandle);
        payload["expectedTargetFocusHandle"] = SearchOverlayDiagnostics.FormatHandle(targetFocusHandle);
        payload["expectedTargetCaretHandle"] = SearchOverlayDiagnostics.FormatHandle(targetCaretHandle);
        payload["expectedTargetCaretRect"] = FormatRect(targetCaretRect);
        payload["expectedTargetHasCaretRect"] = hasTargetCaretRect;
        payload["targetThreadId"] = targetThreadId;
        payload["verificationSessionId"] = verificationSessionId;
        payload["bringToTopResult"] = bringToTopResult;
        payload["setForegroundResult"] = setForegroundResult;
        payload["setActiveSucceeded"] = setActiveSucceeded;
        payload["focusAttempted"] = focusAttempted;
        payload["focusPreviousHandle"] = SearchOverlayDiagnostics.FormatHandle(focusPreviousHandle);
        payload["foregroundAtReturn"] = SearchOverlayDiagnostics.FormatHandle(NativeMethods.GetForegroundWindow());
        SearchOverlayDiagnostics.AddGuiThreadSnapshot(payload, "targetGuiImmediatelyAfterRestore", targetThreadId);
        SearchOverlayDiagnostics.Log("search_overlay", "target_focus_restore_attempted", payload);

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => _ = VerifyExternalWindowInputFocusRestoreAsync(
                targetWindowHandle,
                targetFocusHandle,
                targetCaretHandle,
                targetCaretRect,
                hasTargetCaretRect,
                targetThreadId,
                verificationSessionId)));
    }

    private async Task VerifyExternalWindowInputFocusRestoreAsync(
        IntPtr targetWindowHandle,
        IntPtr targetFocusHandle,
        IntPtr targetCaretHandle,
        NativeMethods.RECT targetCaretRect,
        bool hasTargetCaretRect,
        uint targetThreadId,
        int verificationSessionId)
    {
        var initialDelayMs = Math.Clamp(_viewModel.Settings.RestoreFocusDelayMs, 50, 2000);
        const int maxVerificationAttempts = 3;
        var retryDelayMs = Math.Clamp(_viewModel.Settings.ActivationRetryDelayMs, 50, 2000);

        for (var attempt = 1; attempt <= maxVerificationAttempts; attempt++)
        {
            await Task.Delay(attempt == 1 ? initialDelayMs : retryDelayMs);

            if (!IsRestoreVerificationSessionCurrent(verificationSessionId))
            {
                var abortedPayload = SearchOverlayDiagnostics.CreateWindowSnapshot("target", targetWindowHandle);
                abortedPayload["verificationAttempt"] = attempt;
                abortedPayload["verificationSessionId"] = verificationSessionId;
                abortedPayload["currentOverlaySessionId"] = _overlaySessionId;
                abortedPayload["overlayVisible"] = IsVisible;
                SearchOverlayDiagnostics.Log("search_overlay", "target_focus_restore_verification_aborted_session_changed", abortedPayload);
                return;
            }

            var foregroundWindow = NativeMethods.GetForegroundWindow();
            var hasTargetGuiInfo = TryGetGuiThreadInfo(targetThreadId, out var targetGuiInfo);
            var isFallbackForegroundReady = IsCaretFallbackForegroundReady(foregroundWindow, targetWindowHandle, verificationSessionId);

            var payload = SearchOverlayDiagnostics.CreateWindowSnapshot("target", targetWindowHandle);
            payload["delayMs"] = attempt == 1 ? initialDelayMs : retryDelayMs;
            payload["verificationAttempt"] = attempt;
            payload["verificationAttemptCount"] = maxVerificationAttempts;
            payload["verificationSessionId"] = verificationSessionId;
            payload["expectedTargetFocusHandle"] = SearchOverlayDiagnostics.FormatHandle(targetFocusHandle);
            payload["expectedTargetCaretHandle"] = SearchOverlayDiagnostics.FormatHandle(targetCaretHandle);
            payload["expectedTargetCaretRect"] = FormatRect(targetCaretRect);
            payload["expectedTargetHasCaretRect"] = hasTargetCaretRect;
            payload["foregroundAfterDelay"] = SearchOverlayDiagnostics.FormatHandle(foregroundWindow);
            SearchOverlayDiagnostics.AddWindowSnapshot(payload, "foreground", foregroundWindow);
            SearchOverlayDiagnostics.AddGuiThreadSnapshot(payload, "targetGuiAfterDelay", targetThreadId);
            payload["hasTargetGuiInfoAfterDelay"] = hasTargetGuiInfo;
            payload["capturedCaretHandleStillValid"] = targetCaretHandle != IntPtr.Zero && NativeMethods.IsWindow(targetCaretHandle);
            payload["isFallbackForegroundReady"] = isFallbackForegroundReady;

            var shouldUseCaretFallback = hasTargetGuiInfo &&
                                         isFallbackForegroundReady &&
                                         ShouldUseCaretFocusFallback(
                                             targetWindowHandle,
                                             targetGuiInfo.hwndFocus,
                                             targetGuiInfo.hwndCaret,
                                             targetCaretRect,
                                             hasTargetCaretRect);
            payload["shouldUseCaretFallback"] = shouldUseCaretFallback;

            var shouldRetry = !shouldUseCaretFallback &&
                              ShouldRetryExternalWindowInputFocusRestoreVerification(
                                  attempt,
                                  maxVerificationAttempts,
                                  targetWindowHandle,
                                  foregroundWindow,
                                  verificationSessionId,
                                  hasTargetGuiInfo,
                                  targetGuiInfo,
                                  hasTargetCaretRect);
            payload["willRetryVerification"] = shouldRetry;
            SearchOverlayDiagnostics.Log("search_overlay", "target_focus_restore_verified", payload);

            if (shouldUseCaretFallback)
            {
                var fallbackPayload = SearchOverlayDiagnostics.CreateWindowSnapshot("target", targetWindowHandle);
                fallbackPayload["verificationAttempt"] = attempt;
                fallbackPayload["verificationSessionId"] = verificationSessionId;
                fallbackPayload["expectedTargetFocusHandle"] = SearchOverlayDiagnostics.FormatHandle(targetFocusHandle);
                fallbackPayload["expectedTargetCaretHandle"] = SearchOverlayDiagnostics.FormatHandle(targetCaretHandle);
                fallbackPayload["expectedTargetCaretRect"] = FormatRect(targetCaretRect);
                fallbackPayload["expectedTargetHasCaretRect"] = hasTargetCaretRect;
                fallbackPayload["actualTargetFocusHandle"] = SearchOverlayDiagnostics.FormatHandle(targetGuiInfo.hwndFocus);
                fallbackPayload["actualTargetCaretHandle"] = SearchOverlayDiagnostics.FormatHandle(targetGuiInfo.hwndCaret);
                fallbackPayload["foregroundBeforeFallback"] = SearchOverlayDiagnostics.FormatHandle(foregroundWindow);
                fallbackPayload["isFallbackForegroundReady"] = isFallbackForegroundReady;
                fallbackPayload["caretFallbackApplied"] = false;
                fallbackPayload["caretFallbackDisabled"] = true;
                SearchOverlayDiagnostics.Log("search_overlay", "target_focus_restore_fallback_skipped", fallbackPayload);
                return;
            }

            if (!shouldRetry)
            {
                return;
            }
        }
    }

    private bool ShouldUseCaretFocusFallback(
        IntPtr targetWindowHandle,
        IntPtr actualFocusHandle,
        IntPtr actualCaretHandle,
        NativeMethods.RECT targetCaretRect,
        bool hasTargetCaretRect)
    {
        if (targetWindowHandle == IntPtr.Zero ||
            !NativeMethods.IsWindow(targetWindowHandle) ||
            !hasTargetCaretRect)
        {
            return false;
        }

        return actualFocusHandle == IntPtr.Zero ||
               actualFocusHandle == targetWindowHandle ||
               actualCaretHandle == targetWindowHandle;
    }

    private bool IsCaretFallbackForegroundReady(IntPtr foregroundWindowHandle, IntPtr targetWindowHandle, int verificationSessionId)
    {
        var normalizedForegroundWindow = NormalizeTopLevelWindow(foregroundWindowHandle);
        if (normalizedForegroundWindow == IntPtr.Zero)
        {
            return false;
        }

        var normalizedTargetWindow = NormalizeTopLevelWindow(targetWindowHandle);
        return normalizedForegroundWindow == normalizedTargetWindow ||
               (normalizedForegroundWindow == _windowHandle && IsRestoreVerificationSessionCurrent(verificationSessionId));
    }

    private bool ShouldRetryExternalWindowInputFocusRestoreVerification(
        int attempt,
        int maxAttempts,
        IntPtr targetWindowHandle,
        IntPtr foregroundWindowHandle,
        int verificationSessionId,
        bool hasTargetGuiInfo,
        NativeMethods.GUITHREADINFO targetGuiInfo,
        bool hasTargetCaretRect)
    {
        if (attempt >= maxAttempts || !hasTargetCaretRect)
        {
            return false;
        }

        if (!hasTargetGuiInfo)
        {
            return true;
        }

        if (targetGuiInfo.hwndCaret != IntPtr.Zero)
        {
            return false;
        }

        var normalizedTargetWindow = NormalizeTopLevelWindow(targetWindowHandle);
        var normalizedForegroundWindow = NormalizeTopLevelWindow(foregroundWindowHandle);
        var isOverlayForegroundAllowed = normalizedForegroundWindow == _windowHandle &&
                                         IsRestoreVerificationSessionCurrent(verificationSessionId);
        if (normalizedForegroundWindow != IntPtr.Zero &&
            normalizedForegroundWindow != normalizedTargetWindow &&
            !isOverlayForegroundAllowed)
        {
            return false;
        }

        var normalizedFocusWindow = NormalizeTopLevelWindow(targetGuiInfo.hwndFocus);
        return normalizedForegroundWindow == IntPtr.Zero ||
               isOverlayForegroundAllowed ||
               normalizedForegroundWindow == normalizedTargetWindow ||
               normalizedFocusWindow == normalizedTargetWindow ||
               targetGuiInfo.hwndFocus == IntPtr.Zero;
    }

    private bool IsRestoreVerificationSessionCurrent(int verificationSessionId)
    {
        return verificationSessionId != 0 &&
               _overlaySessionId == verificationSessionId &&
               (!IsVisible || _overlayClosingSessionId == verificationSessionId);
    }

    private static bool TryGetGuiThreadInfo(uint threadId, out NativeMethods.GUITHREADINFO guiInfo)
    {
        guiInfo = default;
        if (threadId == 0)
        {
            return false;
        }

        guiInfo = new NativeMethods.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        return NativeMethods.GetGUIThreadInfo(threadId, ref guiInfo);
    }

    private SearchOverlayCaretAnchor? TryCreateOverlayTargetCaretAnchor()
    {
        if (!_overlayTargetHasCaretRect ||
            _overlayTargetCaretHandle == IntPtr.Zero ||
            !NativeMethods.IsWindow(_overlayTargetCaretHandle))
        {
            return null;
        }

        var topLeft = new NativeMethods.POINT
        {
            X = _overlayTargetCaretRect.Left,
            Y = _overlayTargetCaretRect.Top
        };
        var bottomRight = new NativeMethods.POINT
        {
            X = _overlayTargetCaretRect.Right,
            Y = _overlayTargetCaretRect.Bottom
        };

        if (!NativeMethods.ClientToScreen(_overlayTargetCaretHandle, ref topLeft) ||
            !NativeMethods.ClientToScreen(_overlayTargetCaretHandle, ref bottomRight))
        {
            return null;
        }

        var anchor = new SearchOverlayCaretAnchor(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        return anchor.IsUsable ? anchor : null;
    }

    private static bool IsCaretRectUsable(NativeMethods.RECT rect)
    {
        return rect.Right >= rect.Left && rect.Bottom > rect.Top;
    }

    private static bool IsTabPhysicallyPressed()
    {
        return (NativeMethods.GetAsyncKeyState(NativeMethods.VkTab) & 0x8000) != 0;
    }

    private bool ShouldDeferActivationRecoveryUntilTabReleased()
    {
        return _suppressTabUntilRelease || IsTabPhysicallyPressed();
    }

    private static string FormatRect(NativeMethods.RECT rect)
    {
        return $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";
    }

    private void SearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshSearchInputHints();
    }

    private void RefreshSearchInputHints()
    {
        UpdateSearchPlaceholderVisibility();
        Dispatcher.BeginInvoke(HideDuplicatePlaceholderHints, DispatcherPriority.Loaded);
    }

    private void UpdateSearchPlaceholderVisibility()
    {
        SearchPlaceholderTextBlock.Visibility = string.IsNullOrWhiteSpace(SearchTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void HideDuplicatePlaceholderHints()
    {
        var placeholderText = SearchPlaceholderTextBlock.Text;
        if (string.IsNullOrWhiteSpace(placeholderText))
        {
            return;
        }

        foreach (var textBlock in EnumerateVisualDescendants<TextBlock>(this))
        {
            if (ReferenceEquals(textBlock, SearchPlaceholderTextBlock))
            {
                continue;
            }

            if (IsDescendantOf(textBlock, SearchInputChrome))
            {
                continue;
            }

            if (!string.Equals(textBlock.Text, placeholderText, StringComparison.Ordinal))
            {
                continue;
            }

            textBlock.Visibility = Visibility.Collapsed;
        }

        foreach (var contentControl in EnumerateVisualDescendants<ContentControl>(this))
        {
            if (contentControl.Content is not string contentText)
            {
                continue;
            }

            if (IsDescendantOf(contentControl, SearchInputChrome))
            {
                continue;
            }

            if (!string.Equals(contentText, placeholderText, StringComparison.Ordinal))
            {
                continue;
            }

            contentControl.Visibility = Visibility.Collapsed;
        }
    }

    private static IEnumerable<T> EnumerateVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var descendant in EnumerateVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        var current = child;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ShowModeToast()
    {
        ShowTransientToast(_viewModel.CurrentModeToastText);
    }

    private void ShowTransientToast(string text)
    {
        if (_isSettingsPreviewMode || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _modeToastTimer.Stop();
        ModeToastTextBlock.Text = text;
        ModeToastHost.Visibility = Visibility.Visible;
        ModeToastHost.BeginAnimation(UIElement.OpacityProperty, null);
        ModeToastHost.Opacity = 0d;
        ModeToastHost.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(1d, ModeToastAnimationDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        _modeToastTimer.Start();
    }

    private void ModeToastTimer_OnTick(object? sender, EventArgs e)
    {
        _modeToastTimer.Stop();
        HideModeToast();
    }

    private void HideModeToast(bool immediate = false)
    {
        _modeToastTimer.Stop();

        if (immediate)
        {
            ModeToastHost.BeginAnimation(UIElement.OpacityProperty, null);
            ModeToastHost.Opacity = 0d;
            ModeToastHost.Visibility = Visibility.Collapsed;
            return;
        }

        var fadeOut = new DoubleAnimation(0d, ModeToastAnimationDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) => ModeToastHost.Visibility = Visibility.Collapsed;
        ModeToastHost.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private static int? TryGetShortcutNumber(Key key)
    {
        return key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D0,
            _ => null
        };
    }

    private void ScrollSelectionIntoView()
    {
        var selectedResult = _viewModel.GetSelectedResult();
        if (selectedResult is not null)
        {
            ResultsList.ScrollIntoView(selectedResult);
        }
    }

    private static SearchOverlayResultItem? GetResultFromSender(object sender)
    {
        return sender switch
        {
            FrameworkElement element when element.DataContext is SearchOverlayResultItem result => result,
            MenuItem menuItem when menuItem.DataContext is SearchOverlayResultItem result => result,
            _ => null
        };
    }

    private void ShowResultImagePreviewPopup(ReplyPhrase phrase, FrameworkElement? anchorElement)
    {
        var imagePaths = _viewModel.GetPhraseImageAbsolutePaths(phrase);
        if (imagePaths.Count == 0)
        {
            _viewModel.StatusMessage = "这条快捷短语当前没有可预览的图片。";
            return;
        }

        ResultImagePreviewPopupChrome.Child = PhraseImagePreviewContentFactory.BuildPopupContent(phrase, imagePaths);
        ResultImagePreviewPopup.PlacementTarget = anchorElement ?? ResultsList;
        ResultImagePreviewPopup.IsOpen = true;
    }

    private static FrameworkElement? GetContextMenuPlacementTarget(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu contextMenu })
        {
            return contextMenu.PlacementTarget as FrameworkElement;
        }

        return null;
    }

    private void Settings_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSettingsPreviewMode)
        {
            return;
        }

        if (e.PropertyName is nameof(AppSettings.TagColorHex) or
            nameof(AppSettings.TagOpacityPercent) or
            nameof(AppSettings.CategoryColorHex) or
            nameof(AppSettings.CategoryOpacityPercent) or
            nameof(AppSettings.SearchOverlayColorHex) or
            nameof(AppSettings.SearchOverlayOpacityPercent) or
            nameof(AppSettings.SearchInputColorHex) or
            nameof(AppSettings.SearchInputOpacityPercent) or
            nameof(AppSettings.DefaultSearchOverlayMode) or
            nameof(AppSettings.SearchOverlayWidthPercent) or
            nameof(AppSettings.SearchOverlayHeightPercent) or
            nameof(AppSettings.SearchInputWidthPercent) or
            nameof(AppSettings.SearchInputBoxHeight))
        {
            ApplyTheme();
            if (IsVisible)
            {
                UpdatePlacement();
            }
        }
    }

    private void ApplyTheme()
    {
        ThemeResourceService.ApplySearchOverlayTheme(Resources, _previewSettings ?? _viewModel.Settings);
    }
}
