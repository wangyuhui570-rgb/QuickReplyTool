using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.ComponentModel;
using System.Globalization;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop;

public partial class MainWindow : Window
{
    private readonly GlobalSearchFocusService _globalSearchFocusService;
    private readonly SearchOverlayWindow _searchOverlayWindow;
    private readonly SettingsWindowCoordinator _settingsWindowCoordinator;
    private System.Windows.Point _categoryDragStartPoint;
    private DateTime _categoryPressedAt;
    private ReplyCategory? _categoryPressedItem;
    private bool _categoryDragStarted;
    private ReplyCategory? _inlineCategoryEditingCategory;
    private bool _isInlineCategoryInsertVisible;
    private bool _isCommittingInlineCategoryInsert;
    private bool _isCommittingInlineCategoryEdit;
    private ReplyPhrase? _inlineTagEditingPhrase;
    private ReplyPhrase? _inlineInsertEditingPhrase;
    private ReplyPhrase? _inlineContentEditingPhrase;
    private bool _isCommittingInlineTagEdit;
    private bool _isCommittingInlineInsertEdit;
    private bool _isCommittingInlineContentEdit;
    private FrameworkElement? _categoryEditPopupAnchor;
    private bool _isHandlingFrequentReplySuggestion;
    private AppSettings? _observedSettings;
    private readonly System.Windows.Threading.DispatcherTimer _phraseClickTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(220)
    };
    private ReplyPhrase? _pendingPhraseClickPhrase;
    private ReplyPhrase? _phraseMultiSelectAnchor;

    public MainWindow(
        MainViewModel viewModel,
        GlobalSearchFocusService globalSearchFocusService,
        SearchOverlayWindow searchOverlayWindow)
    {
        InitializeComponent();
        WindowTransitionService.Attach(this, animateOnLoaded: false);
        DataContext = viewModel;
        _globalSearchFocusService = globalSearchFocusService;
        _searchOverlayWindow = searchOverlayWindow;
        _settingsWindowCoordinator = new SettingsWindowCoordinator(this, viewModel, searchOverlayWindow, ApplyTheme);
        _phraseClickTimer.Tick += PhraseClickTimer_OnTick;

        Loaded += MainWindow_OnLoaded;
        Closed += MainWindow_OnClosed;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupTrace.Mark("main_window.loaded");
        ApplyTheme();
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
            AttachSettingsSubscription();
            ViewModel.FrequentReplyPhraseSuggested += ViewModel_OnFrequentReplyPhraseSuggested;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            StartupTrace.Mark("main_window.global_search_focus_starting");
            _globalSearchFocusService.Start(
                this,
                GetSearchFocusHotkeyMode,
                CanRedirectSearchFocus,
                FocusSearchOverlay,
                CanHandleSearchFocusFromOwnWindow);
            StartupTrace.Mark("main_window.global_search_focus_started");
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            ViewModel.FrequentReplyPhraseSuggested -= ViewModel_OnFrequentReplyPhraseSuggested;
        }

        DetachSettingsSubscription();
        _phraseClickTimer.Stop();
        _globalSearchFocusService.Stop();
        _settingsWindowCoordinator.Close();
        _searchOverlayWindow.Close();
    }

    private SearchFocusHotkeyMode GetSearchFocusHotkeyMode()
    {
        return ViewModel?.Settings.SearchFocusHotkeyMode ?? SearchFocusHotkeyMode.Tab;
    }

    private bool CanRedirectSearchFocus(IntPtr foregroundWindowHandle)
    {
        if (_searchOverlayWindow.IsSearchSessionOpen)
        {
            return true;
        }

        if (!_searchOverlayWindow.CanAcceptSearchFocus)
        {
            return false;
        }

        if (foregroundWindowHandle != new WindowInteropHelper(this).Handle)
        {
            // "只在指定软件中触发": returning false lets the keystroke pass through to the app.
            return TriggerAppFilterGate.IsForegroundAllowed(ViewModel?.Settings, foregroundWindowHandle);
        }

        return !IsInlineEditingActive() && !HasEditableKeyboardFocus();
    }

    private bool CanHandleSearchFocusFromOwnWindow(IntPtr foregroundWindowHandle)
    {
        return _searchOverlayWindow.IsSearchSessionOpen &&
               _searchOverlayWindow.OwnsWindowHandle(foregroundWindowHandle);
    }

    private void FocusSearchOverlay(IntPtr targetWindowHandle)
    {
        if (_searchOverlayWindow.IsSearchSessionOpen)
        {
            if (_searchOverlayWindow.HasDirectTypingFocus)
            {
                _searchOverlayWindow.ReturnFocusToExternalWindowFromHotkey();
            }
            else
            {
                _searchOverlayWindow.ReassertSearchInputFocusFromHotkey();
            }

            return;
        }

        if (GetSearchFocusHotkeyMode() == SearchFocusHotkeyMode.Tab)
        {
            _globalSearchFocusService.SuppressTabUntilRelease();
        }

        _searchOverlayWindow.ShowForSearch(targetWindowHandle);
    }

    private async void ViewModel_OnFrequentReplyPhraseSuggested(object? sender, FrequentReplyPhraseSuggestion suggestion)
    {
        if (ViewModel is null || _isHandlingFrequentReplySuggestion)
        {
            return;
        }

        _isHandlingFrequentReplySuggestion = true;
        try
        {
            var message = BuildFrequentReplySuggestionMessage(suggestion);
            var confirm = System.Windows.MessageBox.Show(
                this,
                message,
                "保存快捷短语",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (confirm == MessageBoxResult.Yes)
            {
                var result = await ViewModel.AcceptFrequentReplyPhraseSuggestionAsync(suggestion);
                if (!result.Success)
                {
                    System.Windows.MessageBox.Show(
                        this,
                        result.Message,
                        "保存失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return;
            }

            ViewModel.DismissFrequentReplyPhraseSuggestion(suggestion);
        }
        finally
        {
            _isHandlingFrequentReplySuggestion = false;
        }
    }

    private void CompactSidebarLauncherButton_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ExpandCompactSidebar();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenSettingsWindow();
    }

    public void OpenSettingsWindow()
    {
        if (ViewModel is null)
        {
            return;
        }

        _settingsWindowCoordinator.Open();
    }

    private void AddCategoryMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        BeginInlineCategoryInsert();
    }

    private void EditCategoryMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var category = GetCategoryFromSender(sender);
        if (ViewModel is null || category is null)
        {
            return;
        }

        var anchorElement = sender is MenuItem menuItem && menuItem.Parent is ContextMenu contextMenu
            ? contextMenu.PlacementTarget as FrameworkElement
            : null;

        Dispatcher.BeginInvoke(
            new Action(() => BeginCategoryPopupEdit(category, anchorElement)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void CategoryContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu || contextMenu.DataContext is not ReplyCategory category)
        {
            return;
        }

        BuildCategoryContextMenu(contextMenu, category);
    }

    private async void DeleteCategoryMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var category = GetCategoryFromSender(sender);
        if (ViewModel is null || category is null)
        {
            return;
        }

        var phraseCount = category.Phrases.Count;
        if (phraseCount == 0)
        {
            await ViewModel.DeleteCategoryWithPhrasesAsync(category);
            return;
        }

        var message = $"删除分类会删除目前分类的 {phraseCount} 条快捷短语，确定要这么操作吗？";
        var confirmResult = System.Windows.MessageBox.Show(
            this,
            message,
            "确认删除分类",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        await ViewModel.DeleteCategoryWithPhrasesAsync(category);
    }

    private async void MergeCategoryMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem ||
            menuItem.Tag is not ReplyCategory targetCategory ||
            menuItem.DataContext is not ReplyCategory sourceCategory ||
            ViewModel is null)
        {
            return;
        }

        if (sourceCategory.Id == targetCategory.Id)
        {
            return;
        }

        await ViewModel.MergeCategoryIntoAsync(sourceCategory, targetCategory);
    }

    private void CategoryChip_OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var category = GetCategoryFromSender(sender);
        if (ViewModel is not null && category is not null)
        {
            ViewModel.SelectedCategory = category;
        }
    }

    private void CategoryChip_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsEditableElement(e.OriginalSource as DependencyObject))
        {
            ClearCategoryDragState();
            return;
        }

        var category = GetCategoryFromSender(sender);
        if (ViewModel is not null && category is not null)
        {
            ViewModel.SelectedCategory = category;
        }

        if (e.ClickCount >= 2 && category is not null)
        {
            ClearCategoryDragState();
            e.Handled = true;
            var anchorElement = sender as FrameworkElement;
            Dispatcher.BeginInvoke(
                new Action(() => BeginCategoryPopupEdit(category, anchorElement)),
                System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        _categoryPressedItem = category;
        _categoryPressedAt = DateTime.UtcNow;
        _categoryDragStartPoint = e.GetPosition(this);
        _categoryDragStarted = false;
    }

    private void CategoryChip_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsEditableElement(e.OriginalSource as DependencyObject))
        {
            ClearCategoryDragState();
            return;
        }

        ClearCategoryDragState();
    }

    private void CategoryChip_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (IsEditableElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (_categoryPressedItem is null || _categoryDragStarted || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPosition = e.GetPosition(this);
        var movedEnough =
            Math.Abs(currentPosition.X - _categoryDragStartPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(currentPosition.Y - _categoryDragStartPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;

        if (!movedEnough || DateTime.UtcNow - _categoryPressedAt < TimeSpan.FromMilliseconds(220))
        {
            return;
        }

        _categoryDragStarted = true;
        DragDrop.DoDragDrop(
            (DependencyObject)sender,
            new System.Windows.DataObject(typeof(ReplyCategory), _categoryPressedItem),
            System.Windows.DragDropEffects.Move);
        ClearCategoryDragState();
    }

    private void CategoryChip_OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ReplyCategory))
            ? System.Windows.DragDropEffects.Move
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void CategoryChip_OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (ViewModel is null ||
            !e.Data.GetDataPresent(typeof(ReplyCategory)) ||
            e.Data.GetData(typeof(ReplyCategory)) is not ReplyCategory sourceCategory ||
            GetCategoryFromSender(sender) is not ReplyCategory targetCategory)
        {
            return;
        }

        await ViewModel.MoveCategoryAsync(sourceCategory, targetCategory);
    }

    private void BuildCategoryContextMenu(ContextMenu contextMenu, ReplyCategory sourceCategory)
    {
        contextMenu.Items.Clear();

        var addItem = new MenuItem
        {
            Header = "新建分类",
            DataContext = sourceCategory
        };
        addItem.Click += AddCategoryMenuItem_OnClick;
        contextMenu.Items.Add(addItem);

        var editItem = new MenuItem
        {
            Header = "编辑分类",
            DataContext = sourceCategory
        };
        editItem.Click += EditCategoryMenuItem_OnClick;
        contextMenu.Items.Add(editItem);

        var mergeTargets = ViewModel?.Categories
            .Where(category => category.Id != sourceCategory.Id)
            .OrderBy(category => category.SortOrder)
            .ToList() ?? [];

        var mergeItem = new MenuItem
        {
            Header = "合并到",
            DataContext = sourceCategory,
            IsEnabled = mergeTargets.Count > 0
        };

        foreach (var targetCategory in mergeTargets)
        {
            var targetItem = new MenuItem
            {
                Header = $"{targetCategory.Name} ({targetCategory.Phrases.Count})",
                Tag = targetCategory,
                DataContext = sourceCategory
            };
            targetItem.Click += MergeCategoryMenuItem_OnClick;
            mergeItem.Items.Add(targetItem);
        }

        contextMenu.Items.Add(mergeItem);
        contextMenu.Items.Add(new Separator());

        var deleteItem = new MenuItem
        {
            Header = "删除分类",
            DataContext = sourceCategory
        };
        deleteItem.Click += DeleteCategoryMenuItem_OnClick;
        contextMenu.Items.Add(deleteItem);
    }

    private void BeginCategoryPopupEdit(ReplyCategory category, FrameworkElement? anchorElement = null)
    {
        if (ViewModel is null)
        {
            return;
        }

        CancelInlineCategoryInsert();
        CancelInlineCategoryEdit(_inlineCategoryEditingCategory);
        CloseCategoryEditPopup();

        _inlineCategoryEditingCategory = category;
        _categoryEditPopupAnchor = anchorElement ?? ResolveCategoryChipAnchor(category);
        category.IsInlineEditing = true;
        category.InlineNameDraft = category.Name;
        ViewModel.SelectedCategory = category;

        CategoryEditPopup.PlacementTarget = _categoryEditPopupAnchor ?? CategoryTabs;
        CategoryPopupTextBox.Text = category.Name;
        CategoryEditPopup.IsOpen = true;
    }

    private async Task CommitCategoryPopupEditAsync()
    {
        if (ViewModel is null || _inlineCategoryEditingCategory is not { } category)
        {
            return;
        }

        var updatedName = CategoryPopupTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(updatedName))
        {
            CategoryPopupTextBox.Focus();
            CategoryPopupTextBox.SelectAll();
            return;
        }

        try
        {
            _isCommittingInlineCategoryEdit = true;
            await ViewModel.UpdateCategoryAsync(category, updatedName);
        }
        finally
        {
            _isCommittingInlineCategoryEdit = false;
            CloseCategoryEditPopup();
        }
    }

    private void CancelCategoryPopupEdit()
    {
        CloseCategoryEditPopup();
    }

    private void CategoryEditPopup_OnOpened(object sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            CategoryPopupTextBox.Focus();
            Keyboard.Focus(CategoryPopupTextBox);
            CategoryPopupTextBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CategoryEditPopup_OnClosed(object sender, EventArgs e)
    {
        if (_isCommittingInlineCategoryEdit)
        {
            return;
        }

        ResetCategoryEditPopupState();
    }

    private async void CategoryEditSaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        await CommitCategoryPopupEditAsync();
    }

    private void CategoryEditCancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        CancelCategoryPopupEdit();
    }

    private async void CategoryPopupTextBox_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitCategoryPopupEditAsync();
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelCategoryPopupEdit();
        }
    }

    private void CloseCategoryEditPopup()
    {
        if (CategoryEditPopup.IsOpen)
        {
            CategoryEditPopup.IsOpen = false;
        }

        ResetCategoryEditPopupState();
    }

    private void ResetCategoryEditPopupState()
    {
        if (_inlineCategoryEditingCategory is { } category)
        {
            category.IsInlineEditing = false;
            category.InlineNameDraft = string.Empty;
            category.InlineEditCaretMode = InlineCategoryEditCaretMode.End;
            category.InlineEditPointerRatio = 1d;
        }

        _inlineCategoryEditingCategory = null;
        _categoryEditPopupAnchor = null;
        CategoryEditPopup.PlacementTarget = null;
        CategoryPopupTextBox.Text = string.Empty;
    }

    private FrameworkElement? ResolveCategoryChipAnchor(ReplyCategory category)
    {
        if (CategoryTabs.ItemContainerGenerator.ContainerFromItem(category) is not DependencyObject container)
        {
            return null;
        }

        return FindVisualDescendant<FrameworkElement>(
            container,
            element => element.Name == "CategoryChipBorder" && ReferenceEquals(element.DataContext, category));
    }

    private void NewCategoryInlineTextBox_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isInlineCategoryInsertVisible || sender is not System.Windows.Controls.TextBox textBox)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private async void NewCategoryInlineTextBox_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isInlineCategoryInsertVisible)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitInlineCategoryInsertAsync();
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelInlineCategoryInsert();
        }
    }

    private async void NewCategoryInlineTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_isInlineCategoryInsertVisible || _isCommittingInlineCategoryInsert)
        {
            return;
        }

        await CommitInlineCategoryInsertAsync();
    }

    private void InlineCategoryTextBox_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox ||
            textBox.DataContext is not ReplyCategory category ||
            !category.IsInlineEditing)
        {
            return;
        }

        ApplyInlineCategoryEditFocus(textBox, category);
    }

    private async void InlineCategoryTextBox_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { DataContext: ReplyCategory category })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitInlineCategoryEditAsync(category);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelInlineCategoryEdit(category);
        }
    }

    private async void InlineCategoryTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { DataContext: ReplyCategory category })
        {
            return;
        }

        if (!category.IsInlineEditing || _isCommittingInlineCategoryEdit)
        {
            return;
        }

        await CommitInlineCategoryEditAsync(category);
    }

    private async void AddPhraseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var editor = new PhraseEditorWindow("新增短语", "新增", string.Empty, string.Empty)
        {
            Owner = this
        };

        var dialogResult = editor.ShowDialog();
        SearchOverlayDiagnostics.Log(
            "phrase_images",
            "main_window_create_dialog_closed",
            new Dictionary<string, object?>
            {
                ["dialogResult"] = dialogResult
            });
        if (dialogResult != true)
        {
            return;
        }

        try
        {
            await ViewModel.CreatePhraseAsync(editor.PhraseTitle, editor.PhraseTitle, editor.PhraseContent, editor.PhraseImageSourcePaths);
        }
        catch (Exception ex)
        {
            editor.CleanupOwnedTransientImages();
            ViewModel.StatusMessage = $"新增短语失败：{ex.Message}";
        }
    }

    private void PhraseItem_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var phrase = GetPhraseFromSender(sender);
        if (ViewModel is null || phrase is null)
        {
            return;
        }

        if (e.ClickCount > 1 || phrase.IsInlineContentEditing || phrase.IsInlineInsertEditing || phrase.IsInlineTagEditing)
        {
            return;
        }

        _pendingPhraseClickPhrase = phrase;
        _phraseClickTimer.Stop();
        _phraseClickTimer.Start();
    }

    private void PhraseItem_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2)
        {
            return;
        }

        var phrase = GetPhraseFromSender(sender);
        if (ViewModel is null || phrase is null)
        {
            return;
        }

        _phraseClickTimer.Stop();
        _pendingPhraseClickPhrase = null;
        BeginInlineContentEdit(phrase);
        e.Handled = true;
    }

    private async void SendPhraseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var phrase = GetPhraseFromSender(sender);
        if (ViewModel is null || phrase is null)
        {
            return;
        }

        await ViewModel.SendPhraseAsync(phrase, "phrase_list_send");
    }

    private void PhraseRow_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var phrase = GetPhraseFromSender(sender);
        if (ViewModel is not null && phrase is not null)
        {
            ApplyPhraseRightClickSelection(phrase);
            ViewModel.SelectedPhrase = phrase;
        }
    }

    private void PhraseContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu || contextMenu.DataContext is not ReplyPhrase phrase)
        {
            return;
        }

        BuildPhraseContextMenu(contextMenu, phrase);
    }

    private async void EditPhraseMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var phrase = GetPhraseFromSender(sender);
        if (ViewModel is null || phrase is null)
        {
            return;
        }

        var editor = new PhraseEditorWindow("编辑短语", "保存", phrase.Title, phrase.Content, ViewModel.GetPhraseImageAbsolutePaths(phrase))
        {
            Owner = this
        };

        var dialogResult = editor.ShowDialog();
        SearchOverlayDiagnostics.Log(
            "phrase_images",
            "main_window_edit_dialog_closed",
            new Dictionary<string, object?>
            {
                ["phraseId"] = phrase.Id.ToString("N"),
                ["dialogResult"] = dialogResult
            });
        if (dialogResult != true)
        {
            return;
        }

        try
        {
            await ViewModel.UpdatePhraseAsync(phrase, editor.PhraseTitle, editor.PhraseTitle, editor.PhraseContent, editor.PhraseImageSourcePaths, editor.RemovePhraseImages);
        }
        catch (Exception ex)
        {
            editor.CleanupOwnedTransientImages();
            ViewModel.StatusMessage = $"编辑短语失败：{ex.Message}";
        }
    }

    private async void InsertPhraseMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var phrase = GetPhraseFromSender(sender);
        if (ViewModel is null || phrase is null)
        {
            return;
        }

        var editor = new PhraseEditorWindow("插入短语", "插入", string.Empty, string.Empty)
        {
            Owner = this
        };

        if (editor.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await ViewModel.InsertPhraseAfterAsync(phrase, editor.PhraseTitle, editor.PhraseTitle, editor.PhraseContent, editor.PhraseImageSourcePaths);
        }
        catch (Exception ex)
        {
            editor.CleanupOwnedTransientImages();
            ViewModel.StatusMessage = $"插入短语失败：{ex.Message}";
        }
    }

    private async void DeletePhraseMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var phrase = GetPhraseFromSender(sender);
        if (ViewModel is null || phrase is null)
        {
            return;
        }

        await ViewModel.DeletePhraseAsync(phrase);
    }

    private void AddTagMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var phrase = GetPhraseFromSender(sender);
        if (ViewModel is null || phrase is null)
        {
            return;
        }

        BeginInlineTagEdit(phrase);
    }

    private void ApplyPhraseRightClickSelection(ReplyPhrase phrase)
    {
        if (ViewModel is null)
        {
            return;
        }

        var visiblePhrases = ViewModel.FilteredPhrases.ToList();
        var modifiers = Keyboard.Modifiers;

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            SelectPhraseRange(phrase, visiblePhrases);
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            phrase.IsMultiSelected = !phrase.IsMultiSelected;
            _phraseMultiSelectAnchor = phrase;
            return;
        }

        var selectedPhrases = ViewModel.GetMultiSelectedPhrases();
        if (selectedPhrases.Count > 1 && phrase.IsMultiSelected)
        {
            _phraseMultiSelectAnchor = phrase;
            return;
        }

        ViewModel.ClearPhraseMultiSelection();
        phrase.IsMultiSelected = true;
        _phraseMultiSelectAnchor = phrase;
    }

    private void SelectPhraseRange(ReplyPhrase phrase, IReadOnlyList<ReplyPhrase> orderedPhrases)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (_phraseMultiSelectAnchor is null)
        {
            ViewModel.ClearPhraseMultiSelection();
            phrase.IsMultiSelected = true;
            _phraseMultiSelectAnchor = phrase;
            return;
        }

        var anchorIndex = orderedPhrases.ToList().FindIndex(item => item.Id == _phraseMultiSelectAnchor.Id);
        var targetIndex = orderedPhrases.ToList().FindIndex(item => item.Id == phrase.Id);
        if (anchorIndex < 0 || targetIndex < 0)
        {
            ViewModel.ClearPhraseMultiSelection();
            phrase.IsMultiSelected = true;
            _phraseMultiSelectAnchor = phrase;
            return;
        }

        ViewModel.ClearPhraseMultiSelection();
        var start = Math.Min(anchorIndex, targetIndex);
        var end = Math.Max(anchorIndex, targetIndex);
        for (var index = start; index <= end; index++)
        {
            orderedPhrases[index].IsMultiSelected = true;
        }
    }

    private void BuildPhraseContextMenu(ContextMenu contextMenu, ReplyPhrase currentPhrase)
    {
        if (ViewModel is null)
        {
            return;
        }

        contextMenu.Items.Clear();
        var selectedPhrases = ViewModel.GetMultiSelectedPhrases();
        if (selectedPhrases.Count > 1 && currentPhrase.IsMultiSelected)
        {
            var moveMenuItem = new MenuItem { Header = "批量移动到" };
            foreach (var category in ViewModel.Categories.OrderBy(item => item.SortOrder).ThenBy(item => item.Name))
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

        contextMenu.Items.Add(CreatePhraseMenuItem("编辑", EditPhraseMenuItem_OnClick));
        contextMenu.Items.Add(CreatePhraseMenuItem("插入短语", InsertPhraseMenuItem_OnClick));
        contextMenu.Items.Add(CreatePhraseMenuItem("删除", DeletePhraseMenuItem_OnClick));
        contextMenu.Items.Add(new Separator());
        var imagePaths = ViewModel.GetPhraseImageAbsolutePaths(currentPhrase);
        if (imagePaths.Count > 0)
        {
            contextMenu.Items.Add(CreatePhraseMenuItem("预览图片", PreviewPhraseImagesMenuItem_OnClick));
            contextMenu.Items.Add(CreatePhraseMenuItem("仅复制图片", CopyPhraseImagesMenuItem_OnClick));
        }
        contextMenu.Items.Add(CreatePhraseMenuItem("添加标签", AddTagMenuItem_OnClick));
    }

    private async Task MoveSelectedPhrasesAsync(IReadOnlyList<ReplyPhrase> selectedPhrases, ReplyCategory targetCategory)
    {
        if (ViewModel is null || selectedPhrases.Count == 0)
        {
            return;
        }

        await ViewModel.MovePhrasesToCategoryAsync(selectedPhrases, targetCategory);
        _phraseMultiSelectAnchor = null;
    }

    private async Task DeleteSelectedPhrasesAsync(IReadOnlyList<ReplyPhrase> selectedPhrases)
    {
        if (ViewModel is null || selectedPhrases.Count == 0)
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
            return;
        }

        await ViewModel.DeletePhrasesAsync(selectedPhrases);
        _phraseMultiSelectAnchor = null;
    }

    private static MenuItem CreatePhraseMenuItem(string header, RoutedEventHandler clickHandler)
    {
        var menuItem = new MenuItem { Header = header };
        menuItem.Click += clickHandler;
        return menuItem;
    }

    private void PreviewPhraseImagesMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var phrase = GetPhraseFromSender(sender);
        if (ViewModel is null || phrase is null)
        {
            return;
        }

        ShowPhraseImagePreviewPopup(phrase, GetContextMenuPlacementTarget(sender));
    }

    private async void CopyPhraseImagesMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var phrase = GetPhraseFromSender(sender);
        if (ViewModel is null || phrase is null)
        {
            return;
        }

        await ViewModel.CopyPhraseImagesDirectAsync(phrase, "phrase_list_copy_images");
    }

    private void InlineTagTextBox_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox || textBox.DataContext is not ReplyPhrase phrase || !phrase.IsInlineTagEditing)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private async void InlineTagTextBox_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox || textBox.DataContext is not ReplyPhrase phrase)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitInlineTagEditAsync(phrase);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelInlineTagEdit(phrase);
        }
    }

    private async void InlineTagTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox || textBox.DataContext is not ReplyPhrase phrase)
        {
            return;
        }

        if (!phrase.IsInlineTagEditing || _isCommittingInlineTagEdit)
        {
            return;
        }

        await CommitInlineTagEditAsync(phrase);
    }

    private void InlineTagTextBox_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void InlineTagTextBox_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void InlineInsertTextBox_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox || textBox.DataContext is not ReplyPhrase phrase || !phrase.IsInlineInsertEditing)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            textBox.Focus();
            textBox.CaretIndex = textBox.Text.Length;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private async void InlineInsertTextBox_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { DataContext: ReplyPhrase phrase })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitInlineInsertEditAsync(phrase);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelInlineInsertEdit(phrase);
        }
    }

    private async void InlineInsertTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { DataContext: ReplyPhrase phrase })
        {
            return;
        }

        if (!phrase.IsInlineInsertEditing || _isCommittingInlineInsertEdit)
        {
            return;
        }

        await CommitInlineInsertEditAsync(phrase);
    }

    private void InlineInsertTextBox_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void InlineInsertTextBox_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void InlineContentTextBox_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox || textBox.DataContext is not ReplyPhrase phrase || !phrase.IsInlineContentEditing)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            textBox.Focus();
            textBox.CaretIndex = textBox.Text.Length;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private async void InlineContentTextBox_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { DataContext: ReplyPhrase phrase })
        {
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            await CommitInlineContentEditAsync(phrase);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelInlineContentEdit(phrase);
        }
    }

    private async void InlineContentTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { DataContext: ReplyPhrase phrase })
        {
            return;
        }

        if (!phrase.IsInlineContentEditing || _isCommittingInlineContentEdit)
        {
            return;
        }

        await CommitInlineContentEditAsync(phrase);
    }

    private void InlineContentTextBox_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void InlineContentTextBox_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private async void PhraseClickTimer_OnTick(object? sender, EventArgs e)
    {
        _phraseClickTimer.Stop();

        if (ViewModel is null || _pendingPhraseClickPhrase is null)
        {
            _pendingPhraseClickPhrase = null;
            return;
        }

        var phrase = _pendingPhraseClickPhrase;
        _pendingPhraseClickPhrase = null;
        await ViewModel.InputPhraseAsync(phrase, "phrase_list_input");
    }

    private void ClearCategoryDragState()
    {
        _categoryPressedItem = null;
        _categoryPressedAt = default;
        _categoryDragStarted = false;
    }

    private static ReplyPhrase? GetPhraseFromSender(object sender)
    {
        return sender switch
        {
            FrameworkElement element when element.DataContext is ReplyPhrase phrase => phrase,
            MenuItem menuItem when menuItem.DataContext is ReplyPhrase phrase => phrase,
            _ => null
        };
    }

    private static ReplyCategory? GetCategoryFromSender(object sender)
    {
        return sender switch
        {
            FrameworkElement element when element.DataContext is ReplyCategory category => category,
            MenuItem menuItem when menuItem.DataContext is ReplyCategory category => category,
            _ => null
        };
    }

    private void Settings_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.CategoryColorHex) or
            nameof(AppSettings.CategoryOpacityPercent) or
            nameof(AppSettings.TagColorHex) or
            nameof(AppSettings.TagOpacityPercent) or
            nameof(AppSettings.PhraseColorHex) or
            nameof(AppSettings.PhraseOpacityPercent) or
            nameof(AppSettings.SidebarColorHex) or
            nameof(AppSettings.SidebarOpacityPercent))
        {
            ApplyTheme();
        }
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Settings) or "")
        {
            AttachSettingsSubscription();
            ApplyTheme();
        }
    }

    private void AttachSettingsSubscription()
    {
        if (ViewModel is null)
        {
            DetachSettingsSubscription();
            return;
        }

        if (ReferenceEquals(_observedSettings, ViewModel.Settings))
        {
            return;
        }

        DetachSettingsSubscription();
        _observedSettings = ViewModel.Settings;
        _observedSettings.PropertyChanged += Settings_OnPropertyChanged;
    }

    private void DetachSettingsSubscription()
    {
        if (_observedSettings is null)
        {
            return;
        }

        _observedSettings.PropertyChanged -= Settings_OnPropertyChanged;
        _observedSettings = null;
    }

    private void ApplyTheme()
    {
        if (ViewModel is null)
        {
            return;
        }

        if (System.Windows.Application.Current is not null)
        {
            ThemeResourceService.ApplyApplicationTheme(System.Windows.Application.Current.Resources, ViewModel.Settings);
        }

        ThemeResourceService.ApplyMainWindowTheme(Resources, ViewModel.Settings);
    }

    private bool IsInlineEditingActive()
    {
        return _isInlineCategoryInsertVisible ||
               _inlineCategoryEditingCategory?.IsInlineEditing == true ||
               _inlineTagEditingPhrase?.IsInlineTagEditing == true ||
               _inlineInsertEditingPhrase?.IsInlineInsertEditing == true ||
               _inlineContentEditingPhrase?.IsInlineContentEditing == true;
    }

    private bool HasEditableKeyboardFocus()
    {
        var focusedElement = FocusManager.GetFocusedElement(this) ?? Keyboard.FocusedElement;
        return IsEditableElement(focusedElement as DependencyObject);
    }

    private static bool IsEditableElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.TextBoxBase ||
                element is PasswordBox ||
                element is System.Windows.Controls.ComboBox ||
                element is DatePicker)
            {
                return true;
            }

            element = LogicalTreeHelper.GetParent(element) ?? VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void BeginInlineCategoryInsert()
    {
        CancelInlineCategoryEdit(_inlineCategoryEditingCategory);
        _isInlineCategoryInsertVisible = true;
        NewCategoryInlineTextBox.Text = string.Empty;
        NewCategoryInlineHost.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            NewCategoryInlineTextBox.Focus();
            NewCategoryInlineTextBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private async Task CommitInlineCategoryInsertAsync()
    {
        if (ViewModel is null || !_isInlineCategoryInsertVisible)
        {
            return;
        }

        var draft = NewCategoryInlineTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(draft))
        {
            CancelInlineCategoryInsert();
            return;
        }

        try
        {
            _isCommittingInlineCategoryInsert = true;
            await ViewModel.CreateCategoryAsync(draft);
        }
        finally
        {
            _isCommittingInlineCategoryInsert = false;
            CancelInlineCategoryInsert();
        }
    }

    private void CancelInlineCategoryInsert()
    {
        _isInlineCategoryInsertVisible = false;
        NewCategoryInlineTextBox.Text = string.Empty;
        NewCategoryInlineHost.Visibility = Visibility.Collapsed;
    }

    private void BeginInlineCategoryEdit(
        ReplyCategory category,
        InlineCategoryEditCaretMode caretMode,
        double pointerRatio = 1d)
    {
        if (!ReferenceEquals(_inlineCategoryEditingCategory, category))
        {
            CancelInlineCategoryEdit(_inlineCategoryEditingCategory);
        }

        CancelInlineCategoryInsert();
        _inlineCategoryEditingCategory = category;
        category.InlineNameDraft = category.Name;
        category.InlineEditCaretMode = caretMode;
        category.InlineEditPointerRatio = pointerRatio;
        category.IsInlineEditing = true;
        if (ViewModel is not null)
        {
            ViewModel.SelectedCategory = category;
        }

        ScheduleInlineCategoryEditFocus(category);
    }

    private async Task CommitInlineCategoryEditAsync(ReplyCategory category)
    {
        if (ViewModel is null || !category.IsInlineEditing)
        {
            return;
        }

        var draft = category.InlineNameDraft.Trim();
        if (string.IsNullOrWhiteSpace(draft))
        {
            CancelInlineCategoryEdit(category);
            return;
        }

        try
        {
            _isCommittingInlineCategoryEdit = true;
            await ViewModel.UpdateCategoryAsync(category, draft);
        }
        finally
        {
            _isCommittingInlineCategoryEdit = false;
            if (ReferenceEquals(_inlineCategoryEditingCategory, category))
            {
                _inlineCategoryEditingCategory = null;
            }

            category.IsInlineEditing = false;
            category.InlineNameDraft = string.Empty;
        }
    }

    private void CancelInlineCategoryEdit(ReplyCategory? category)
    {
        if (category is null)
        {
            return;
        }

        category.IsInlineEditing = false;
        category.InlineNameDraft = string.Empty;
        category.InlineEditCaretMode = InlineCategoryEditCaretMode.End;
        category.InlineEditPointerRatio = 1d;
        if (ReferenceEquals(_inlineCategoryEditingCategory, category))
        {
            _inlineCategoryEditingCategory = null;
        }
    }

    private static int EstimateCategoryCaretIndex(ReplyCategory category)
    {
        var text = category.InlineNameDraft ?? string.Empty;
        if (text.Length == 0)
        {
            return 0;
        }

        return (int)Math.Round(text.Length * Math.Clamp(category.InlineEditPointerRatio, 0d, 1d), MidpointRounding.AwayFromZero);
    }

    private void ScheduleInlineCategoryEditFocus(ReplyCategory category, int remainingAttempts = 4)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!category.IsInlineEditing)
            {
                return;
            }

            CategoryTabs.ScrollIntoView(category);
            CategoryTabs.UpdateLayout();

            if (TryGetInlineCategoryTextBox(category) is not { } textBox)
            {
                if (remainingAttempts > 0)
                {
                    ScheduleInlineCategoryEditFocus(category, remainingAttempts - 1);
                }

                return;
            }

            ApplyInlineCategoryEditFocus(textBox, category);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ApplyInlineCategoryEditFocus(System.Windows.Controls.TextBox textBox, ReplyCategory category)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!category.IsInlineEditing || !textBox.IsVisible)
            {
                return;
            }

            textBox.Focus();
            Keyboard.Focus(textBox);

            var caretIndex = category.InlineEditCaretMode == InlineCategoryEditCaretMode.PointerRatio
                ? EstimateCategoryCaretIndex(category)
                : category.InlineNameDraft.Length;
            textBox.Select(Math.Clamp(caretIndex, 0, textBox.Text.Length), 0);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private System.Windows.Controls.TextBox? TryGetInlineCategoryTextBox(ReplyCategory category)
    {
        if (CategoryTabs.ItemContainerGenerator.ContainerFromItem(category) is not DependencyObject container)
        {
            return null;
        }

        return FindVisualDescendant<System.Windows.Controls.TextBox>(
            container,
            textBox => textBox.Name == "InlineCategoryTextBox" && ReferenceEquals(textBox.DataContext, category));
    }

    private static T? FindVisualDescendant<T>(DependencyObject? root, Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && (predicate is null || predicate(match)))
            {
                return match;
            }

            if (FindVisualDescendant(child, predicate) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private void BeginInlineTagEdit(ReplyPhrase phrase)
    {
        if (!ReferenceEquals(_inlineTagEditingPhrase, phrase))
        {
            CancelInlineTagEdit(_inlineTagEditingPhrase);
        }

        CancelInlineCategoryInsert();
        CancelInlineCategoryEdit(_inlineCategoryEditingCategory);
        CancelInlineInsertEdit(_inlineInsertEditingPhrase);
        CancelInlineContentEdit(_inlineContentEditingPhrase);

        _inlineTagEditingPhrase = phrase;
        phrase.InlineTagDraft = phrase.Tags;
        phrase.IsInlineTagEditing = true;
        if (ViewModel is not null)
        {
            ViewModel.SelectedPhrase = phrase;
        }
    }

    private async Task CommitInlineTagEditAsync(ReplyPhrase phrase)
    {
        if (ViewModel is null || _isCommittingInlineTagEdit || !phrase.IsInlineTagEditing)
        {
            return;
        }

        _isCommittingInlineTagEdit = true;
        try
        {
            phrase.IsInlineTagEditing = false;
            await ViewModel.UpdatePhraseTagsAsync(phrase, phrase.InlineTagDraft);
            phrase.InlineTagDraft = phrase.Tags;
            if (ReferenceEquals(_inlineTagEditingPhrase, phrase))
            {
                _inlineTagEditingPhrase = null;
            }
        }
        finally
        {
            _isCommittingInlineTagEdit = false;
        }
    }

    private void CancelInlineTagEdit(ReplyPhrase? phrase)
    {
        if (phrase is null)
        {
            return;
        }

        phrase.InlineTagDraft = phrase.Tags;
        phrase.IsInlineTagEditing = false;
        if (ReferenceEquals(_inlineTagEditingPhrase, phrase))
        {
            _inlineTagEditingPhrase = null;
        }
    }

    private void BeginInlineInsertEdit(ReplyPhrase phrase)
    {
        if (!ReferenceEquals(_inlineInsertEditingPhrase, phrase))
        {
            CancelInlineInsertEdit(_inlineInsertEditingPhrase);
        }

        CancelInlineCategoryInsert();
        CancelInlineCategoryEdit(_inlineCategoryEditingCategory);
        CancelInlineContentEdit(_inlineContentEditingPhrase);
        CancelInlineTagEdit(_inlineTagEditingPhrase);

        _inlineInsertEditingPhrase = phrase;
        phrase.InlineInsertDraft = TryGetClipboardText();
        phrase.IsInlineInsertEditing = true;
        if (ViewModel is not null)
        {
            ViewModel.SelectedPhrase = phrase;
        }
    }

    private async Task CommitInlineInsertEditAsync(ReplyPhrase phrase)
    {
        if (ViewModel is null || _isCommittingInlineInsertEdit || !phrase.IsInlineInsertEditing)
        {
            return;
        }

        var content = phrase.InlineInsertDraft.Trim();
        phrase.IsInlineInsertEditing = false;

        if (string.IsNullOrWhiteSpace(content))
        {
            CancelInlineInsertEdit(phrase);
            return;
        }

        _isCommittingInlineInsertEdit = true;
        try
        {
            await ViewModel.InsertPhraseAfterAsync(phrase, string.Empty, string.Empty, content);
            phrase.InlineInsertDraft = string.Empty;
            if (ReferenceEquals(_inlineInsertEditingPhrase, phrase))
            {
                _inlineInsertEditingPhrase = null;
            }
        }
        finally
        {
            _isCommittingInlineInsertEdit = false;
        }
    }

    private void CancelInlineInsertEdit(ReplyPhrase? phrase)
    {
        if (phrase is null)
        {
            return;
        }

        phrase.InlineInsertDraft = string.Empty;
        phrase.IsInlineInsertEditing = false;
        if (ReferenceEquals(_inlineInsertEditingPhrase, phrase))
        {
            _inlineInsertEditingPhrase = null;
        }
    }

    private void BeginInlineContentEdit(ReplyPhrase phrase)
    {
        if (!ReferenceEquals(_inlineContentEditingPhrase, phrase))
        {
            CancelInlineContentEdit(_inlineContentEditingPhrase);
        }

        CancelInlineCategoryInsert();
        CancelInlineCategoryEdit(_inlineCategoryEditingCategory);
        CancelInlineInsertEdit(_inlineInsertEditingPhrase);
        CancelInlineTagEdit(_inlineTagEditingPhrase);

        _inlineContentEditingPhrase = phrase;
        phrase.InlineContentDraft = phrase.Content;
        phrase.IsInlineContentEditing = true;
        if (ViewModel is not null)
        {
            ViewModel.SelectedPhrase = phrase;
        }
    }

    private async Task CommitInlineContentEditAsync(ReplyPhrase phrase)
    {
        if (ViewModel is null || _isCommittingInlineContentEdit || !phrase.IsInlineContentEditing)
        {
            return;
        }

        _isCommittingInlineContentEdit = true;
        try
        {
            phrase.IsInlineContentEditing = false;
            await ViewModel.UpdatePhraseAsync(phrase, phrase.Title, phrase.Tags, phrase.InlineContentDraft);
            phrase.InlineContentDraft = phrase.Content;
            if (ReferenceEquals(_inlineContentEditingPhrase, phrase))
            {
                _inlineContentEditingPhrase = null;
            }
        }
        finally
        {
            _isCommittingInlineContentEdit = false;
        }
    }

    private void CancelInlineContentEdit(ReplyPhrase? phrase)
    {
        if (phrase is null)
        {
            return;
        }

        phrase.InlineContentDraft = phrase.Content;
        phrase.IsInlineContentEditing = false;
        if (ReferenceEquals(_inlineContentEditingPhrase, phrase))
        {
            _inlineContentEditingPhrase = null;
        }
    }

    private void PhrasePreviewToolTip_OnOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not Border phraseCardBorder ||
            phraseCardBorder.DataContext is not ReplyPhrase phrase ||
            ViewModel is null ||
            phraseCardBorder.ToolTip is not System.Windows.Controls.ToolTip toolTip)
        {
            e.Handled = true;
            return;
        }

        var imagePaths = ViewModel.GetPhraseImageAbsolutePaths(phrase);
        var includeTextPreview = phraseCardBorder.FindName("PhraseContentTextBlock") is TextBlock contentTextBlock &&
                                 IsTextTrimmed(contentTextBlock);

        if (phrase.IsInlineTagEditing ||
            phrase.IsInlineInsertEditing ||
            phrase.IsInlineContentEditing ||
            (!includeTextPreview && imagePaths.Count == 0))
        {
            e.Handled = true;
            return;
        }

        toolTip.MaxWidth = imagePaths.Count > 0 ? 560d : 420d;
        toolTip.Content = PhraseImagePreviewContentFactory.BuildHoverContent(phrase, imagePaths, includeTextPreview);
    }

    private static bool IsTextTrimmed(TextBlock textBlock)
    {
        var text = textBlock.Text;
        if (string.IsNullOrWhiteSpace(text) || textBlock.ActualWidth <= 0)
        {
            return false;
        }

        var typeface = new Typeface(
            textBlock.FontFamily,
            textBlock.FontStyle,
            textBlock.FontWeight,
            textBlock.FontStretch);

        var dpi = VisualTreeHelper.GetDpi(textBlock);
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            textBlock.FlowDirection,
            typeface,
            textBlock.FontSize,
            System.Windows.Media.Brushes.Black,
            dpi.PixelsPerDip);

        return formattedText.WidthIncludingTrailingWhitespace > textBlock.ActualWidth + 1;
    }

    private static string TryGetClipboardText()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText().Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void ShowPhraseImagePreviewPopup(ReplyPhrase phrase, FrameworkElement? anchorElement)
    {
        if (ViewModel is null)
        {
            return;
        }

        var imagePaths = ViewModel.GetPhraseImageAbsolutePaths(phrase);
        if (imagePaths.Count == 0)
        {
            ViewModel.StatusMessage = "这条快捷短语当前没有可预览的图片。";
            return;
        }

        PhraseImagePreviewPopupChrome.Child = PhraseImagePreviewContentFactory.BuildPopupContent(phrase, imagePaths);
        PhraseImagePreviewPopup.PlacementTarget = anchorElement ?? PhraseList;
        PhraseImagePreviewPopup.IsOpen = true;
    }

    private static FrameworkElement? GetContextMenuPlacementTarget(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu contextMenu })
        {
            return contextMenu.PlacementTarget as FrameworkElement;
        }

        return null;
    }

    private static string BuildFrequentReplySuggestionMessage(FrequentReplyPhraseSuggestion suggestion)
    {
        var preview = BuildFrequentReplyPreview(suggestion.PayloadText);
        var customerHint = string.IsNullOrWhiteSpace(suggestion.CustomerText)
            ? string.Empty
            : $"{Environment.NewLine}客户上下文：{BuildFrequentReplyPreview(suggestion.CustomerText)}";

        return $"这条人工回复最近已使用 {suggestion.UseCount} 次，是否保存为快捷短语？{customerHint}{Environment.NewLine}{Environment.NewLine}{preview}";
    }

    private static string BuildFrequentReplyPreview(string text)
    {
        var normalized = text
            .Replace("\r\n", " / ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();

        return normalized.Length <= 88 ? normalized : $"{normalized[..88]}...";
    }

}

