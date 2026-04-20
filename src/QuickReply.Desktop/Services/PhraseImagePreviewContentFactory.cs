using System.Collections.Concurrent;
using System.IO;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public static class PhraseImagePreviewContentFactory
{
    private static readonly ConcurrentDictionary<string, System.Windows.Media.ImageSource?> ImageCache = new(StringComparer.OrdinalIgnoreCase);

    public static void InvalidatePaths(IEnumerable<string> imagePaths)
    {
        foreach (var imagePath in imagePaths)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                continue;
            }

            ImageCache.TryRemove(imagePath, out _);
        }
    }

    public static void InvalidateFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        var normalizedFolder = Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        var matchingKeys = ImageCache.Keys
            .Where(path => Path.GetFullPath(path).StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var key in matchingKeys)
        {
            ImageCache.TryRemove(key, out _);
        }
    }

    public static System.Windows.FrameworkElement BuildHoverContent(
        ReplyPhrase phrase,
        IReadOnlyList<string> imagePaths,
        bool includeTextPreview)
    {
        return BuildContent(phrase, imagePaths, includeTextPreview, imageMaxWidth: 220d, imageMaxHeight: 160d);
    }

    public static System.Windows.FrameworkElement BuildPopupContent(ReplyPhrase phrase, IReadOnlyList<string> imagePaths)
    {
        return BuildContent(phrase, imagePaths, includeTextPreview: true, imageMaxWidth: 300d, imageMaxHeight: 220d);
    }

    private static System.Windows.FrameworkElement BuildContent(
        ReplyPhrase phrase,
        IReadOnlyList<string> imagePaths,
        bool includeTextPreview,
        double imageMaxWidth,
        double imageMaxHeight)
    {
        var root = new System.Windows.Controls.StackPanel
        {
            MaxWidth = 560d
        };

        if (phrase.HasDisplayTitle)
        {
            root.Children.Add(new System.Windows.Controls.Border
            {
                Padding = new System.Windows.Thickness(6, 2, 6, 2),
                Margin = new System.Windows.Thickness(0, 0, 0, 8),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Background = CreateBrush("#E8EEF9"),
                BorderBrush = CreateBrush("#C9D8EF"),
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(8),
                Child = new System.Windows.Controls.TextBlock
                {
                    FontSize = 10,
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    Foreground = CreateBrush("#1F2937"),
                    Text = phrase.DisplayTitle
                }
            });
        }

        if (includeTextPreview && !string.IsNullOrWhiteSpace(phrase.Content))
        {
            root.Children.Add(new System.Windows.Controls.TextBlock
            {
                Foreground = CreateBrush("#111827"),
                Text = phrase.Content,
                TextWrapping = System.Windows.TextWrapping.Wrap
            });
        }

        if (imagePaths.Count == 0)
        {
            return root;
        }

        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Margin = new System.Windows.Thickness(0, includeTextPreview ? 10 : 0, 0, 8),
            FontSize = 11,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = CreateBrush("#475569"),
            Text = $"绑定图片 {imagePaths.Count} 张"
        });

        var panel = new System.Windows.Controls.WrapPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal
        };

        foreach (var imagePath in imagePaths.Where(File.Exists))
        {
            panel.Children.Add(BuildImageCard(imagePath, imageMaxWidth, imageMaxHeight));
        }

        root.Children.Add(new System.Windows.Controls.ScrollViewer
        {
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            MaxHeight = imageMaxHeight * 2 + 48d,
            Content = panel
        });

        return root;
    }

    private static System.Windows.FrameworkElement BuildImageCard(string imagePath, double imageMaxWidth, double imageMaxHeight)
    {
        var container = new System.Windows.Controls.Border
        {
            Margin = new System.Windows.Thickness(0, 0, 10, 10),
            Padding = new System.Windows.Thickness(6),
            Background = CreateBrush("#F8FAFC"),
            BorderBrush = CreateBrush("#D9E2EC"),
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(12)
        };

        var imageControl = new System.Windows.Controls.Image
        {
            MaxWidth = imageMaxWidth,
            MaxHeight = imageMaxHeight,
            Stretch = System.Windows.Media.Stretch.Uniform
        };

        var cachedImage = TryGetCachedImage(imagePath);
        if (cachedImage is not null)
        {
            imageControl.Source = cachedImage;
        }
        else
        {
            imageControl.Width = Math.Min(imageMaxWidth, 180d);
            imageControl.Height = Math.Min(imageMaxHeight, 120d);
            imageControl.Source = CreatePlaceholderImage(
                (int)Math.Max(80d, Math.Min(imageMaxWidth, 180d)),
                (int)Math.Max(60d, Math.Min(imageMaxHeight, 120d)));
            _ = EnsureImageLoadedAsync(imageControl, imagePath);
        }

        var stack = new System.Windows.Controls.StackPanel();
        stack.Children.Add(imageControl);
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Margin = new System.Windows.Thickness(0, 6, 0, 0),
            FontSize = 10,
            Foreground = CreateBrush("#64748B"),
            Text = Path.GetFileName(imagePath),
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
            MaxWidth = imageMaxWidth
        });

        container.Child = stack;
        return container;
    }

    private static System.Windows.Media.ImageSource? TryGetCachedImage(string imagePath)
    {
        return ImageCache.TryGetValue(imagePath, out var image) ? image : null;
    }

    private static async Task EnsureImageLoadedAsync(System.Windows.Controls.Image imageControl, string imagePath)
    {
        try
        {
            var image = await Task.Run(() => LoadImage(imagePath));
            ImageCache[imagePath] = image;

            if (image is null)
            {
                return;
            }

            await imageControl.Dispatcher.InvokeAsync(() =>
            {
                imageControl.ClearValue(System.Windows.FrameworkElement.WidthProperty);
                imageControl.ClearValue(System.Windows.FrameworkElement.HeightProperty);
                imageControl.Source = image;
            });
        }
        catch
        {
            ImageCache[imagePath] = null;
        }
    }

    private static System.Windows.Media.ImageSource? LoadImage(string imagePath)
    {
        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    private static System.Windows.Media.ImageSource CreatePlaceholderImage(int width, int height)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var drawingContext = visual.RenderOpen())
        {
            drawingContext.DrawRoundedRectangle(CreateBrush("#E2E8F0"), null, new System.Windows.Rect(0, 0, width, height), 12, 12);
            var pen = new System.Windows.Media.Pen(CreateBrush("#CBD5E1"), 2);
            drawingContext.DrawLine(pen, new System.Windows.Point(16, height - 18), new System.Windows.Point(width / 2d, height / 2d + 4));
            drawingContext.DrawLine(pen, new System.Windows.Point(width / 2d, height / 2d + 4), new System.Windows.Point(width - 18, 20));
            drawingContext.DrawEllipse(CreateBrush("#CBD5E1"), null, new System.Windows.Point(width - 28, 24), 7, 7);
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width,
            height,
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static System.Windows.Media.SolidColorBrush CreateBrush(string hex)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
