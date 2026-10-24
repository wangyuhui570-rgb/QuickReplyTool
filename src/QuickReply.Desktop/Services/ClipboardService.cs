using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickReply.Desktop.Native;

namespace QuickReply.Desktop.Services;

public sealed class ClipboardService : IClipboardService
{
    private const int RetryCount = 12;
    // File-drop second stage on Douyin was hanging 12–35s on busy clipboard; fail faster.
    private const int FileDropRetryCount = 4;
    private const int FileDropDispatcherTimeoutMs = 1500;
    private const int RetryDelayMs = 80;
    private const int MaxRetryDelayMs = 300;
    private const int MaxCachedImages = 24;
    private const int DispatcherOperationTimeoutMs = 4000;
    private static readonly ConcurrentDictionary<string, CachedImageEntry> ImageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ClipboardDispatcherSyncRoot = new();
    private static Dispatcher? _clipboardDispatcher;

    public async Task<ClipboardSnapshot> CaptureSnapshotAsync()
    {
        try
        {
            return await RetryClipboardAccessAsync(() =>
            {
                var rawData = System.Windows.Clipboard.GetDataObject();
                if (rawData is null)
                {
                    return new ClipboardSnapshot(true, false, null, new ClipboardFingerprint("empty"));
                }

                var formats = rawData.GetFormats();
                if (formats.Length == 0)
                {
                    return new ClipboardSnapshot(true, false, null, new ClipboardFingerprint("empty"));
                }

                var clonedData = CloneClipboardData();
                if (clonedData is null)
                {
                    return new ClipboardSnapshot(
                        true,
                        true,
                        null,
                        CreateSequenceFingerprint(),
                        canRestore: false);
                }

                return new ClipboardSnapshot(
                    true,
                    true,
                    clonedData,
                    CreateSequenceFingerprint(),
                    canRestore: true);
            });
        }
        catch
        {
            return new ClipboardSnapshot(false, false, null, null);
        }
    }

    public Task<ClipboardFingerprint?> CaptureCurrentFingerprintAsync()
    {
        try
        {
            return Task.FromResult<ClipboardFingerprint?>(CreateSequenceFingerprint());
        }
        catch
        {
            return Task.FromResult<ClipboardFingerprint?>(null);
        }
    }

    public async Task<ReplyActionResult> SetTextAsync(string text, bool persist = true)
    {
        try
        {
            await RetryClipboardAccessAsync(() =>
            {
                if (persist)
                {
                    System.Windows.Clipboard.SetText(text);
                    System.Windows.Clipboard.Flush();
                    return true;
                }

                var dataObject = new System.Windows.DataObject();
                dataObject.SetData(System.Windows.DataFormats.UnicodeText, text);
                dataObject.SetData(System.Windows.DataFormats.Text, text);
                System.Windows.Clipboard.SetDataObject(dataObject, false);
                return true;
            });

            return new ReplyActionResult(true, "内容已写入系统剪贴板。");
        }
        catch (Exception ex)
        {
            return new ReplyActionResult(
                false,
                $"写入系统剪贴板失败：{ex.Message}",
                FailureKind: ReplyActionFailureKind.ClipboardBusy,
                UserGuidance: "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
        }
    }

    public async Task<ReplyActionResult> SetImageAsync(string imagePath, bool persist = true)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return new ReplyActionResult(
                false,
                "未找到要写入剪贴板的图片。",
                FailureKind: ReplyActionFailureKind.MissingInput,
                UserGuidance: "请确认图片文件仍存在，或重新选择图片后再试。");
        }

        var sequenceBeforeWrite = NativeMethods.GetClipboardSequenceNumber();
        try
        {
            var imageEntry = await LoadImageAsync(imagePath).ConfigureAwait(false);
            await RetryClipboardAccessAsync(() =>
            {
                var dataObject = CreateImageDataObject(imageEntry.Image, imageEntry.PngBytes);
                System.Windows.Clipboard.SetDataObject(dataObject, persist);
                if (persist)
                {
                    System.Windows.Clipboard.Flush();
                }

                return true;
            });

            return new ReplyActionResult(true, "图片已写入系统剪贴板。");
        }
        catch (Exception ex)
        {
            if (await WasClipboardWriteLikelySuccessfulAsync(sequenceBeforeWrite, requireText: false, requireImage: true).ConfigureAwait(false))
            {
                return new ReplyActionResult(true, "图片已写入系统剪贴板。");
            }

            return new ReplyActionResult(
                false,
                $"写入图片剪贴板失败：{ex.Message}",
                FailureKind: ReplyActionFailureKind.ClipboardBusy,
                UserGuidance: "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
        }
    }

    public async Task<ReplyActionResult> SetTextAndImageAsync(string text, string imagePath, bool persist = true)
    {
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(imagePath))
        {
            return new ReplyActionResult(
                false,
                "当前图文内容为空，无法写入剪贴板。",
                FailureKind: ReplyActionFailureKind.EmptyContent,
                UserGuidance: "请先输入文本内容或选择图片后再复制。");
        }

        if (!string.IsNullOrWhiteSpace(imagePath) && !File.Exists(imagePath))
        {
            return new ReplyActionResult(
                false,
                "未找到要写入剪贴板的图片。",
                FailureKind: ReplyActionFailureKind.MissingInput,
                UserGuidance: "请确认图片文件仍存在，或重新选择图片后再试。");
        }

        var sequenceBeforeWrite = NativeMethods.GetClipboardSequenceNumber();
        try
        {
            CachedImageEntry? imageEntry = string.IsNullOrWhiteSpace(imagePath)
                ? null
                : await LoadImageAsync(imagePath).ConfigureAwait(false);
            await RetryClipboardAccessAsync(() =>
            {
                var dataObject = new System.Windows.DataObject();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    dataObject.SetData(System.Windows.DataFormats.UnicodeText, text);
                    dataObject.SetData(System.Windows.DataFormats.Text, text);
                }

                if (imageEntry is not null)
                {
                    var imageDataObject = CreateImageDataObject(imageEntry.Image, imageEntry.PngBytes);
                    foreach (var format in imageDataObject.GetFormats())
                    {
                        dataObject.SetData(format, imageDataObject.GetData(format));
                    }
                }

                System.Windows.Clipboard.SetDataObject(dataObject, persist);
                if (persist)
                {
                    System.Windows.Clipboard.Flush();
                }

                return true;
            });

            return new ReplyActionResult(true, "图文内容已写入系统剪贴板。");
        }
        catch (Exception ex)
        {
            if (await WasClipboardWriteLikelySuccessfulAsync(
                    sequenceBeforeWrite,
                    requireText: !string.IsNullOrWhiteSpace(text),
                    requireImage: !string.IsNullOrWhiteSpace(imagePath)).ConfigureAwait(false))
            {
                return new ReplyActionResult(true, "图文内容已写入系统剪贴板。");
            }

            return new ReplyActionResult(
                false,
                $"写入图文剪贴板失败：{ex.Message}",
                FailureKind: ReplyActionFailureKind.ClipboardBusy,
                UserGuidance: "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
        }
    }

    public async Task<ReplyActionResult> SetTextAndFilesAsync(string text, IReadOnlyCollection<string> filePaths, bool persist = true)
    {
        if (string.IsNullOrWhiteSpace(text) && filePaths.Count == 0)
        {
            return new ReplyActionResult(
                false,
                "当前图文内容为空，无法写入剪贴板。",
                FailureKind: ReplyActionFailureKind.EmptyContent,
                UserGuidance: "请先输入文本内容或选择文件后再复制。");
        }

        try
        {
            await RetryClipboardAccessAsync(() =>
            {
                var dataObject = new System.Windows.DataObject();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    dataObject.SetData(System.Windows.DataFormats.UnicodeText, text);
                    dataObject.SetData(System.Windows.DataFormats.Text, text);
                }

                if (filePaths.Count > 0)
                {
                    var collection = new StringCollection();
                    collection.AddRange(filePaths.ToArray());
                    dataObject.SetFileDropList(collection);
                }

                System.Windows.Clipboard.SetDataObject(dataObject, persist);
                if (persist)
                {
                    System.Windows.Clipboard.Flush();
                }

                return true;
            });

            return new ReplyActionResult(true, "图文内容已写入系统剪贴板。");
        }
        catch (Exception ex)
        {
            return new ReplyActionResult(
                false,
                $"写入图文剪贴板失败：{ex.Message}",
                FailureKind: ReplyActionFailureKind.ClipboardBusy,
                UserGuidance: "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
        }
    }

    public async Task<ReplyActionResult> SetFileDropListAsync(IReadOnlyCollection<string> filePaths, bool persist = true)
    {
        if (filePaths.Count == 0)
        {
            return new ReplyActionResult(
                false,
                "未选择要发送的文件。",
                FailureKind: ReplyActionFailureKind.EmptyContent,
                UserGuidance: "请先选择至少一个文件后再复制。");
        }

        try
        {
            await RetryClipboardAccessAsync(
                () =>
                {
                    var collection = new StringCollection();
                    collection.AddRange(filePaths.ToArray());

                    var dataObject = new System.Windows.DataObject();
                    dataObject.SetFileDropList(collection);
                    System.Windows.Clipboard.SetDataObject(dataObject, persist);
                    if (persist)
                    {
                        System.Windows.Clipboard.Flush();
                    }

                    return true;
                },
                maxAttempts: FileDropRetryCount,
                dispatcherTimeoutMs: FileDropDispatcherTimeoutMs);

            return new ReplyActionResult(true, "文件已写入系统剪贴板。");
        }
        catch (Exception ex)
        {
            return new ReplyActionResult(
                false,
                $"写入文件剪贴板失败：{ex.Message}",
                FailureKind: ReplyActionFailureKind.ClipboardBusy,
                UserGuidance: "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
        }
    }

    public async Task<ReplyActionResult> RestoreSnapshotAsync(
        ClipboardSnapshot snapshot,
        ClipboardFingerprint? expectedCurrentFingerprint = null)
    {
        if (!snapshot.CaptureSucceeded)
        {
            return new ReplyActionResult(
                false,
                "未能备份原剪贴板内容。",
                FailureKind: ReplyActionFailureKind.ClipboardBusy,
                UserGuidance: "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
        }

        if (!snapshot.CanRestore)
        {
            return new ReplyActionResult(true, "未能完整备份原剪贴板内容，已跳过恢复。");
        }

        try
        {
            if (expectedCurrentFingerprint is null)
            {
                return new ReplyActionResult(true, "无法确认当前剪贴板是否仍是程序写入的内容，已跳过恢复。");
            }

            if (expectedCurrentFingerprint is not null)
            {
                var currentFingerprint = await CaptureCurrentFingerprintAsync();
                if (currentFingerprint is null ||
                    !string.Equals(currentFingerprint.Value, expectedCurrentFingerprint.Value, StringComparison.Ordinal))
                {
                    return new ReplyActionResult(true, "检测到剪贴板已被更新，跳过恢复以免覆盖新的内容。");
                }
            }

            await RetryClipboardAccessAsync(() =>
            {
                if (!snapshot.HasContent)
                {
                    System.Windows.Clipboard.Clear();
                }
                else if (snapshot.DataObject is not null)
                {
                    System.Windows.Clipboard.SetDataObject(snapshot.DataObject, true);
                    System.Windows.Clipboard.Flush();
                }

                return true;
            });

            return new ReplyActionResult(true, "已恢复原剪贴板内容。");
        }
        catch (Exception ex)
        {
            return new ReplyActionResult(
                false,
                $"恢复原剪贴板失败：{ex.Message}",
                FailureKind: ReplyActionFailureKind.ClipboardBusy,
                UserGuidance: "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
        }
    }

    private static async Task<T> RetryClipboardAccessAsync<T>(
        Func<T> action,
        int? maxAttempts = null,
        int? dispatcherTimeoutMs = null)
    {
        Exception? lastException = null;
        var attempts = maxAttempts ?? RetryCount;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                return await InvokeOnUiThreadAsync(action, dispatcherTimeoutMs);
            }
            catch (Exception ex)
            {
                if (!IsRetriableClipboardException(ex))
                {
                    throw;
                }

                lastException = ex;
                if (attempt >= attempts - 1)
                {
                    break;
                }

                await Task.Delay(GetRetryDelayMs(attempt));
            }
        }

        throw lastException ?? new InvalidOperationException("剪贴板访问失败。");
    }

    private static bool IsRetriableClipboardException(Exception ex)
    {
        return ex is COMException or ExternalException or InvalidOperationException or TimeoutException or TaskCanceledException or ObjectDisposedException;
    }

    private static int GetRetryDelayMs(int attempt)
    {
        var delay = RetryDelayMs + (attempt * 20);
        return Math.Min(delay, MaxRetryDelayMs);
    }

    private static async Task<T> InvokeOnUiThreadAsync<T>(Func<T> action, int? dispatcherTimeoutMs = null)
    {
        var dispatcher = GetClipboardDispatcher();

        if (dispatcher.CheckAccess())
        {
            return action();
        }

        var timeoutMs = dispatcherTimeoutMs ?? DispatcherOperationTimeoutMs;
        var operation = dispatcher.InvokeAsync(action, DispatcherPriority.Send);
        var completedTask = await Task.WhenAny(
                operation.Task,
                Task.Delay(timeoutMs))
            .ConfigureAwait(false);
        if (completedTask != operation.Task)
        {
            ResetClipboardDispatcher(dispatcher);
            throw new TimeoutException("剪贴板操作超时，已重置剪贴板线程。");
        }

        return await operation.Task.ConfigureAwait(false);
    }

    private static Dispatcher CreateClipboardDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "QuickReply Clipboard STA"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }

    private static Dispatcher GetClipboardDispatcher()
    {
        lock (ClipboardDispatcherSyncRoot)
        {
            if (_clipboardDispatcher is { HasShutdownFinished: false } dispatcher)
            {
                return dispatcher;
            }

            _clipboardDispatcher = CreateClipboardDispatcher();
            return _clipboardDispatcher;
        }
    }

    private static void ResetClipboardDispatcher(Dispatcher dispatcher)
    {
        lock (ClipboardDispatcherSyncRoot)
        {
            if (!ReferenceEquals(_clipboardDispatcher, dispatcher))
            {
                return;
            }

            _clipboardDispatcher = null;
        }

        try
        {
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        catch
        {
        }
    }

    private static async Task<bool> WasClipboardWriteLikelySuccessfulAsync(
        uint sequenceBeforeWrite,
        bool requireText,
        bool requireImage)
    {
        try
        {
            return await RetryClipboardAccessAsync(() =>
            {
                var currentSequence = NativeMethods.GetClipboardSequenceNumber();
                if (currentSequence == sequenceBeforeWrite)
                {
                    return false;
                }

                var dataObject = System.Windows.Clipboard.GetDataObject();
                if (dataObject is null)
                {
                    return false;
                }

                var formats = dataObject.GetFormats();
                if (formats.Length == 0)
                {
                    return false;
                }

                if (requireText && !ContainsAnyFormat(formats, System.Windows.DataFormats.UnicodeText, System.Windows.DataFormats.Text))
                {
                    return false;
                }

                if (requireImage && !ContainsAnyFormat(formats, System.Windows.DataFormats.Bitmap, "PNG", "image/png"))
                {
                    return false;
                }

                return true;
            });
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsAnyFormat(string[] formats, params string[] expectedFormats)
    {
        foreach (var expectedFormat in expectedFormats)
        {
            if (formats.Any(format => string.Equals(format, expectedFormat, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static ClipboardFingerprint CreateSequenceFingerprint()
    {
        return new ClipboardFingerprint($"seq:{NativeMethods.GetClipboardSequenceNumber()}");
    }

    private static System.Windows.DataObject? CloneClipboardData()
    {
        var dataObject = new System.Windows.DataObject();
        var hasSupportedContent = false;

        TryCloneTextFormat(System.Windows.TextDataFormat.UnicodeText, System.Windows.DataFormats.UnicodeText, dataObject, ref hasSupportedContent);
        TryCloneTextFormat(System.Windows.TextDataFormat.Text, System.Windows.DataFormats.Text, dataObject, ref hasSupportedContent);
        TryCloneTextFormat(System.Windows.TextDataFormat.Rtf, System.Windows.DataFormats.Rtf, dataObject, ref hasSupportedContent);
        TryCloneTextFormat(System.Windows.TextDataFormat.Html, System.Windows.DataFormats.Html, dataObject, ref hasSupportedContent);
        TryCloneFileDropList(dataObject, ref hasSupportedContent);
        // Image formats are intentionally not cloned: decoding/re-encoding a large bitmap on every
        // send cost 100-500 ms. Text and file-drop content is still backed up and restored.

        return hasSupportedContent ? dataObject : null;
    }

    private static void TryCloneTextFormat(
        System.Windows.TextDataFormat textFormat,
        string dataFormat,
        System.Windows.DataObject target,
        ref bool hasSupportedContent)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText(textFormat))
            {
                return;
            }

            target.SetData(dataFormat, System.Windows.Clipboard.GetText(textFormat));
            hasSupportedContent = true;
        }
        catch
        {
        }
    }

    private static void TryCloneFileDropList(System.Windows.DataObject target, ref bool hasSupportedContent)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsFileDropList())
            {
                return;
            }

            var original = System.Windows.Clipboard.GetFileDropList();
            if (original.Count == 0)
            {
                return;
            }

            var clone = new StringCollection();
            clone.AddRange(original.Cast<string>().ToArray());
            target.SetFileDropList(clone);
            hasSupportedContent = true;
        }
        catch
        {
        }
    }

    private static void TryCloneImage(System.Windows.DataObject target, ref bool hasSupportedContent)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsImage())
            {
                return;
            }

            var image = System.Windows.Clipboard.GetImage();
            if (image is null)
            {
                return;
            }

            image.Freeze();
            var pngBytes = CreatePngBytes(image);
            var imageDataObject = CreateImageDataObject(image, pngBytes);
            foreach (var format in imageDataObject.GetFormats())
            {
                target.SetData(format, imageDataObject.GetData(format));
            }

            hasSupportedContent = true;
        }
        catch
        {
        }
    }

    private static Task<CachedImageEntry> LoadImageAsync(string imagePath)
    {
        return Task.Run(() => GetOrLoadCachedImageEntry(imagePath));
    }

    private static CachedImageEntry GetOrLoadCachedImageEntry(string imagePath)
    {
        var fileInfo = new FileInfo(imagePath);
        var lastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
        var length = fileInfo.Length;

        if (ImageCache.TryGetValue(imagePath, out var cached) &&
            cached.Length == length &&
            cached.LastWriteTimeUtc == lastWriteTimeUtc)
        {
            return cached;
        }

        var image = LoadImage(imagePath);
        var pngBytes = CreatePngBytes(image);
        var entry = new CachedImageEntry(length, lastWriteTimeUtc, image, pngBytes);
        ImageCache[imagePath] = entry;

        if (ImageCache.Count > MaxCachedImages)
        {
            ImageCache.Clear();
            ImageCache[imagePath] = entry;
        }

        return entry;
    }

    private static System.Windows.DataObject CreateImageDataObject(BitmapSource image, byte[] pngBytes)
    {
        var dataObject = new System.Windows.DataObject();
        dataObject.SetImage(image);
        dataObject.SetData("PNG", CreatePngStream(pngBytes));
        dataObject.SetData("image/png", CreatePngStream(pngBytes));
        return dataObject;
    }

    private static byte[] CreatePngBytes(BitmapSource image)
    {
        var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static MemoryStream CreatePngStream(byte[] pngBytes)
    {
        return new MemoryStream(pngBytes, writable: false);
    }

    private static BitmapSource LoadImage(string imagePath)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var bitmapFrame = decoder.Frames[0];
        bitmapFrame.Freeze();
        return bitmapFrame;
    }

    private sealed class CachedImageEntry
    {
        public CachedImageEntry(long length, DateTime lastWriteTimeUtc, BitmapSource image, byte[] pngBytes)
        {
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
            Image = image;
            PngBytes = pngBytes;
        }

        public long Length { get; }

        public DateTime LastWriteTimeUtc { get; }

        public BitmapSource Image { get; }

        public byte[] PngBytes { get; }
    }
}
