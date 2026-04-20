using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickReply.Desktop.Services;
using Drawing = System.Drawing;

namespace QuickReply.Desktop;

public partial class PhraseEditorWindow : Window, INotifyPropertyChanged
{
    private const int MaxImageCount = 3;
    private static readonly string TempImageFolder = Path.Combine(Path.GetTempPath(), "QuickReplyTool", "phrase-editor");

    private string _phraseTitle;
    private string _phraseContent;
    private readonly IReadOnlyList<string> _initialPhraseImageSourcePaths;
    private readonly HashSet<string> _ownedTransientImagePaths = new(StringComparer.OrdinalIgnoreCase);

    public PhraseEditorWindow(
        string dialogHeading,
        string confirmButtonText,
        string phraseTitle,
        string phraseContent,
        IReadOnlyList<string>? phraseImageSourcePaths = null)
    {
        InitializeComponent();
        WindowTransitionService.Attach(this);

        DialogHeading = dialogHeading;
        ConfirmButtonText = confirmButtonText;
        _phraseTitle = phraseTitle;
        _phraseContent = phraseContent;
        _initialPhraseImageSourcePaths = (phraseImageSourcePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Take(MaxImageCount)
            .ToArray();

        ImageSlots = new ObservableCollection<PhraseEditorImageSlot>(
            Enumerable.Range(0, MaxImageCount).Select(index => CreateEmptySlot(index)));

        foreach (var slot in ImageSlots)
        {
            slot.PropertyChanged += ImageSlot_OnPropertyChanged;
        }

        LoadInitialImages();
        DataContext = this;
        Loaded += PhraseEditorWindow_OnLoaded;
        Closed += PhraseEditorWindow_OnClosed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DialogHeading { get; }

    public string ConfirmButtonText { get; }

    public ObservableCollection<PhraseEditorImageSlot> ImageSlots { get; }

    public string PhraseTitle
    {
        get => _phraseTitle;
        set => SetField(ref _phraseTitle, value);
    }

    public string PhraseContent
    {
        get => _phraseContent;
        set => SetField(ref _phraseContent, value);
    }

    public IReadOnlyList<string> PhraseImageSourcePaths => ImageSlots
        .Where(slot => slot.HasImage && !string.IsNullOrWhiteSpace(slot.SourcePath))
        .Select(slot => slot.SourcePath!)
        .ToArray();

    public bool HasPhraseImages => PhraseImageSourcePaths.Count > 0;

    public bool RemovePhraseImages => _initialPhraseImageSourcePaths.Count > 0 && PhraseImageSourcePaths.Count == 0;

    public string PhraseImageSummary => HasPhraseImages
        ? $"图片已导入，当前 {PhraseImageSourcePaths.Count}/{MaxImageCount} 张"
        : "暂未导入图片，可粘贴或拖入最多 3 张图片";

    private void PhraseEditorWindow_OnLoaded(object? sender, RoutedEventArgs e)
    {
        ContentTextBox.Focus();
        ContentTextBox.CaretIndex = ContentTextBox.Text?.Length ?? 0;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        SearchOverlayDiagnostics.Log(
            "phrase_images",
            "editor_save_clicked",
            new Dictionary<string, object?>
            {
                ["title"] = PhraseTitle,
                ["imageCount"] = PhraseImageSourcePaths.Count,
                ["imagePaths"] = string.Join("|", PhraseImageSourcePaths),
                ["removePhraseImages"] = RemovePhraseImages
            });
        WindowTransitionService.SuppressNextCloseAnimation(this);
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowTransitionService.SuppressNextCloseAnimation(this);
        DialogResult = false;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowTransitionService.SuppressNextCloseAnimation(this);
        Close();
    }

    public void CleanupOwnedTransientImages()
    {
        CleanupTransientPaths(_ownedTransientImagePaths.ToArray());
        _ownedTransientImagePaths.Clear();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void EditorField_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (ReferenceEquals(sender, ContentTextBox))
            {
                TitleTextBox.Focus();
                TitleTextBox.SelectAll();
            }
            else
            {
                ContentTextBox.Focus();
                ContentTextBox.CaretIndex = ContentTextBox.Text?.Length ?? 0;
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            if (ReferenceEquals(sender, TitleTextBox))
            {
                ContentTextBox.Focus();
                ContentTextBox.CaretIndex = ContentTextBox.Text?.Length ?? 0;
            }
            else
            {
                TitleTextBox.Focus();
                TitleTextBox.SelectAll();
            }

            e.Handled = true;
            return;
        }

        if (ReferenceEquals(sender, ContentTextBox) &&
            e.Key == Key.V &&
            (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
            TryAttachImagesFromClipboard())
        {
            e.Handled = true;
        }
    }

    private void ClearAllImagesButton_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var slot in ImageSlots)
        {
            slot.Clear();
        }

        CleanupUnusedTransientImages();
        NotifyImageStateChanged();
    }

    private void RemoveSlotImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int slotIndex })
        {
            return;
        }

        var slot = ImageSlots.FirstOrDefault(item => item.SlotIndex == slotIndex);
        if (slot is null)
        {
            return;
        }

        slot.Clear();
        CompactFilledSlots();
        CleanupUnusedTransientImages();
        NotifyImageStateChanged();
    }

    private void PhraseImageDropHost_OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = CanImportImagesFromDataObject(e.Data)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void PhraseImageDropHost_OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        var imagePaths = TryImportImagesFromDataObject(e.Data);
        foreach (var imagePath in imagePaths)
        {
            RegisterTransientImage(imagePath);
        }

        SearchOverlayDiagnostics.Log(
            "phrase_images",
            "editor_drop_received",
            new Dictionary<string, object?>
            {
                ["importedCount"] = imagePaths.Count,
                ["importedPaths"] = string.Join("|", imagePaths)
            });
        if (imagePaths.Count == 0)
        {
            return;
        }

        AddPhraseImageSources(imagePaths);
        e.Handled = true;
    }

    private void ImageSlot_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PhraseEditorImageSlot.SourcePath) or
            nameof(PhraseEditorImageSlot.Preview) or
            nameof(PhraseEditorImageSlot.HasImage) or
            nameof(PhraseEditorImageSlot.FileName))
        {
            NotifyImageStateChanged();
        }
    }

    private void LoadInitialImages()
    {
        AddPhraseImageSources(_initialPhraseImageSourcePaths, replaceExisting: true);
    }

    private bool TryAttachImagesFromClipboard()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var fileList = System.Windows.Clipboard.GetFileDropList().Cast<string>()
                    .Where(IsSupportedImageFile)
                    .ToArray();

                if (fileList.Length > 0)
                {
                    SearchOverlayDiagnostics.Log(
                        "phrase_images",
                        "editor_clipboard_file_imported",
                        new Dictionary<string, object?>
                        {
                            ["importedCount"] = fileList.Length,
                            ["importedPaths"] = string.Join("|", fileList)
                        });
                    AddPhraseImageSources(fileList);
                    return true;
                }
            }

            if (!System.Windows.Clipboard.ContainsImage())
            {
                return false;
            }

            var image = System.Windows.Clipboard.GetImage();
            if (image is null)
            {
                return false;
            }

            var tempImagePath = SaveClipboardImageToTemp(image);
            RegisterTransientImage(tempImagePath);
            SearchOverlayDiagnostics.Log(
                "phrase_images",
                "editor_clipboard_bitmap_imported",
                new Dictionary<string, object?>
                {
                    ["tempImagePath"] = tempImagePath
                });
            AddPhraseImageSources([tempImagePath]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void AddPhraseImageSources(IEnumerable<string> sourcePaths, bool replaceExisting = false)
    {
        var candidatePaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Where(IsSupportedImageFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidatePaths.Length == 0)
        {
            SearchOverlayDiagnostics.Log("phrase_images", "editor_add_sources_ignored_empty");
            return;
        }

        if (replaceExisting)
        {
            foreach (var slot in ImageSlots)
            {
                slot.Clear();
            }
        }

        var existingPaths = ImageSlots
            .Where(slot => slot.HasImage && !string.IsNullOrWhiteSpace(slot.SourcePath))
            .Select(slot => slot.SourcePath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var availableSlots = ImageSlots.Where(slot => !slot.HasImage).ToList();
        var addedCount = 0;
        foreach (var imagePath in candidatePaths)
        {
            if (availableSlots.Count == 0)
            {
                break;
            }

            if (!existingPaths.Add(imagePath))
            {
                continue;
            }

            var slot = availableSlots[0];
            availableSlots.RemoveAt(0);
            slot.SetImage(imagePath, LoadPreview(imagePath));
            addedCount++;
        }

        CompactFilledSlots();
        CleanupUnusedTransientImages();
        NotifyImageStateChanged();
        SearchOverlayDiagnostics.Log(
            "phrase_images",
            "editor_sources_added",
            new Dictionary<string, object?>
            {
                ["addedCount"] = addedCount,
                ["currentCount"] = PhraseImageSourcePaths.Count,
                ["currentPaths"] = string.Join("|", PhraseImageSourcePaths)
            });
    }

    private void CompactFilledSlots()
    {
        var filled = ImageSlots
            .Where(slot => slot.HasImage && !string.IsNullOrWhiteSpace(slot.SourcePath))
            .Select(slot => slot.SourcePath!)
            .ToArray();

        for (var index = 0; index < ImageSlots.Count; index++)
        {
            if (index < filled.Length)
            {
                var imagePath = filled[index];
                ImageSlots[index].SetImage(imagePath, LoadPreview(imagePath));
            }
            else
            {
                ImageSlots[index].Clear();
            }
        }
    }

    private void NotifyImageStateChanged()
    {
        OnPropertyChanged(nameof(PhraseImageSourcePaths));
        OnPropertyChanged(nameof(HasPhraseImages));
        OnPropertyChanged(nameof(RemovePhraseImages));
        OnPropertyChanged(nameof(PhraseImageSummary));
    }

    private static PhraseEditorImageSlot CreateEmptySlot(int slotIndex)
    {
        return new PhraseEditorImageSlot(slotIndex + 1);
    }

    private static bool CanImportImagesFromDataObject(System.Windows.IDataObject dataObject)
    {
        return TryGetImageFilePaths(dataObject).Count > 0 || TryExtractBitmapSource(dataObject) is not null;
    }

    private static IReadOnlyList<string> TryImportImagesFromDataObject(System.Windows.IDataObject dataObject)
    {
        var filePaths = TryGetImageFilePaths(dataObject);
        if (filePaths.Count > 0)
        {
            return filePaths;
        }

        var bitmapSource = TryExtractBitmapSource(dataObject);
        if (bitmapSource is null)
        {
            return [];
        }

        return [SaveBitmapSourceToTemp(bitmapSource, "drop")];
    }

    private static IReadOnlyList<string> TryGetImageFilePaths(System.Windows.IDataObject dataObject)
    {
        if (!dataObject.GetDataPresent(System.Windows.DataFormats.FileDrop) ||
            dataObject.GetData(System.Windows.DataFormats.FileDrop) is not string[] filePaths)
        {
            return [];
        }

        return filePaths
            .Where(IsSupportedImageFile)
            .ToArray();
    }

    private static bool IsSupportedImageFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static BitmapSource? TryExtractBitmapSource(System.Windows.IDataObject dataObject)
    {
        try
        {
            if (dataObject.GetDataPresent(System.Windows.DataFormats.Bitmap))
            {
                var rawBitmap = dataObject.GetData(System.Windows.DataFormats.Bitmap);
                if (rawBitmap is BitmapSource bitmapSource)
                {
                    bitmapSource.Freeze();
                    return bitmapSource;
                }

                if (rawBitmap is Drawing.Bitmap drawingBitmap)
                {
                    return ConvertDrawingBitmap(drawingBitmap);
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static ImageSource LoadPreview(string imagePath)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static string SaveClipboardImageToTemp(BitmapSource image)
    {
        return SaveBitmapSourceToTemp(image, "clipboard");
    }

    private static string SaveBitmapSourceToTemp(BitmapSource image, string prefix)
    {
        Directory.CreateDirectory(TempImageFolder);

        var tempPath = Path.Combine(TempImageFolder, $"{prefix}-{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        return tempPath;
    }

    private void PhraseEditorWindow_OnClosed(object? sender, EventArgs e)
    {
        if (DialogResult == true)
        {
            CleanupUnusedTransientImages();
            return;
        }

        CleanupOwnedTransientImages();
    }

    private void RegisterTransientImage(string path)
    {
        if (IsManagedTransientImagePath(path))
        {
            _ownedTransientImagePaths.Add(path);
        }
    }

    private void CleanupUnusedTransientImages()
    {
        var activePaths = PhraseImageSourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stalePaths = _ownedTransientImagePaths.Where(path => !activePaths.Contains(path)).ToArray();
        CleanupTransientPaths(stalePaths);

        foreach (var stalePath in stalePaths)
        {
            _ownedTransientImagePaths.Remove(stalePath);
        }
    }

    private static void CleanupTransientPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!IsManagedTransientImagePath(path) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }

    public static bool IsManagedTransientImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(path);
        var normalizedTempRoot = Path.GetFullPath(TempImageFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(normalizedTempRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static BitmapSource ConvertDrawingBitmap(Drawing.Bitmap drawingBitmap)
    {
        using var memoryStream = new MemoryStream();
        drawingBitmap.Save(memoryStream, Drawing.Imaging.ImageFormat.Png);
        memoryStream.Position = 0;

        var decoder = BitmapDecoder.Create(memoryStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
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

public sealed class PhraseEditorImageSlot : INotifyPropertyChanged
{
    private string? _sourcePath;
    private ImageSource? _preview;

    public PhraseEditorImageSlot(int displayIndex)
    {
        DisplayIndex = displayIndex;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int DisplayIndex { get; }

    public int SlotIndex => DisplayIndex - 1;

    public string SlotTitle => $"图片 {DisplayIndex}";

    public string PlaceholderText => $"拖入图片\n或 Ctrl+V 粘贴";

    public string? SourcePath
    {
        get => _sourcePath;
        private set
        {
            if (SetField(ref _sourcePath, value))
            {
                OnPropertyChanged(nameof(HasImage));
                OnPropertyChanged(nameof(FileName));
            }
        }
    }

    public ImageSource? Preview
    {
        get => _preview;
        private set => SetField(ref _preview, value);
    }

    public bool HasImage => !string.IsNullOrWhiteSpace(SourcePath);

    public string FileName => HasImage ? Path.GetFileName(SourcePath) : string.Empty;

    public void SetImage(string sourcePath, ImageSource preview)
    {
        SourcePath = sourcePath;
        Preview = preview;
    }

    public void Clear()
    {
        SourcePath = null;
        Preview = null;
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
