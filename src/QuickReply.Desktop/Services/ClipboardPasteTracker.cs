namespace QuickReply.Desktop.Services;

/// <summary>
/// Tracks one payload this app put on the clipboard. OLE renders clipboard data on demand, so the
/// first <c>GetData</c> after our Ctrl+V tells us the target app has actually read the payload,
/// and only then is it safe to replace the clipboard with the next part of the message.
/// </summary>
internal sealed class ClipboardPasteTracker
{
    private readonly TaskCompletionSource _consumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<long> _clock;
    private volatile bool _pasteIssued;
    private volatile bool _readBeforePaste;
    private long _pasteIssuedAt;
    private long _lastReadAfterPasteAt;
    private int _readsAfterPaste;

    public ClipboardPasteTracker(Func<long>? clock = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public uint Sequence { get; set; }

    public bool PasteIssued => _pasteIssued;

    /// <summary>Someone (often a clipboard manager or the chat app itself) read it before our paste.</summary>
    public bool ReadBeforePaste => _readBeforePaste;

    /// <summary>Completes on the first read after our Ctrl+V. Not proof the paste itself read it.</summary>
    public Task Consumed => _consumed.Task;

    public int ReadsAfterPaste => Volatile.Read(ref _readsAfterPaste);

    public long MillisecondsSincePaste => _pasteIssued ? _clock() - Interlocked.Read(ref _pasteIssuedAt) : 0;

    /// <summary>Null until something reads the payload after the paste.</summary>
    public long? MillisecondsSinceLastRead =>
        ReadsAfterPaste == 0 ? null : _clock() - Interlocked.Read(ref _lastReadAfterPasteAt);

    public void NotePasteIssued()
    {
        Interlocked.Exchange(ref _pasteIssuedAt, _clock());
        _pasteIssued = true;
    }

    public void OnDataRequested()
    {
        if (_pasteIssued)
        {
            Interlocked.Exchange(ref _lastReadAfterPasteAt, _clock());
            Interlocked.Increment(ref _readsAfterPaste);
            _consumed.TrySetResult();
        }
        else
        {
            _readBeforePaste = true;
        }
    }
}

/// <summary>
/// When the clipboard may be replaced after a paste. The first read after our Ctrl+V is often not
/// the paste: chat apps (千牛, 咚咚) read the clipboard as soon as it changes, and that notification
/// can be handled just after the keystroke was sent. Their editor then reads again when it handles
/// the Ctrl+V, sometimes hundreds of milliseconds later, and Win32 readers are served from a cache
/// we never see. Replacing the clipboard 30 ms after the paste (2.1.22) made the text paste pick up
/// the image, so the message went out as two images. Hence a minimum gap, plus a quiet period after
/// the last read we did see.
/// </summary>
internal static class PasteGapPolicy
{
    public const int MinimumGapMs = 450;
    public const int QuietAfterReadMs = 150;
    public const int UnobservedFallbackMs = 700;

    public static bool MayReplace(long sincePasteMs, long? sinceLastReadMs, bool readBeforePaste, int timeoutMs)
    {
        if (sincePasteMs >= timeoutMs)
        {
            return true;
        }

        if (sincePasteMs < MinimumGapMs)
        {
            return false;
        }

        if (sinceLastReadMs is { } quiet)
        {
            return quiet >= QuietAfterReadMs;
        }

        // Nothing read it after the paste. If something read it before, later reads may be served
        // from a cache we cannot see; otherwise the app simply has not got to the paste yet.
        return readBeforePaste && sincePasteMs >= UnobservedFallbackMs;
    }
}

/// <summary>Clipboard data object that reports every data request to its tracker.</summary>
internal sealed class TrackingDataObject : System.Windows.IDataObject
{
    private readonly System.Windows.IDataObject _inner;
    private readonly ClipboardPasteTracker _tracker;

    public TrackingDataObject(System.Windows.IDataObject inner, ClipboardPasteTracker tracker)
    {
        _inner = inner;
        _tracker = tracker;
    }

    public object GetData(string format)
    {
        _tracker.OnDataRequested();
        return _inner.GetData(format);
    }

    public object GetData(Type format)
    {
        _tracker.OnDataRequested();
        return _inner.GetData(format);
    }

    public object GetData(string format, bool autoConvert)
    {
        _tracker.OnDataRequested();
        return _inner.GetData(format, autoConvert);
    }

    public bool GetDataPresent(string format) => _inner.GetDataPresent(format);

    public bool GetDataPresent(Type format) => _inner.GetDataPresent(format);

    public bool GetDataPresent(string format, bool autoConvert) => _inner.GetDataPresent(format, autoConvert);

    public string[] GetFormats() => _inner.GetFormats();

    public string[] GetFormats(bool autoConvert) => _inner.GetFormats(autoConvert);

    public void SetData(object data) => _inner.SetData(data);

    public void SetData(string format, object data) => _inner.SetData(format, data);

    public void SetData(Type format, object data) => _inner.SetData(format, data);

    public void SetData(string format, object data, bool autoConvert) => _inner.SetData(format, data, autoConvert);
}
