namespace QuickReply.Desktop.Services;

/// <summary>
/// Tracks one payload this app put on the clipboard. OLE renders clipboard data on demand, so the
/// first <c>GetData</c> after our Ctrl+V tells us the target app has actually read the payload,
/// and only then is it safe to replace the clipboard with the next part of the message.
/// </summary>
internal sealed class ClipboardPasteTracker
{
    private readonly TaskCompletionSource _consumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _pasteIssued;
    private volatile bool _readBeforePaste;

    public uint Sequence { get; set; }

    public bool PasteIssued => _pasteIssued;

    /// <summary>Someone (often a clipboard manager) read it before our paste: reads can no longer be observed.</summary>
    public bool ReadBeforePaste => _readBeforePaste;

    public Task Consumed => _consumed.Task;

    public void NotePasteIssued() => _pasteIssued = true;

    public void OnDataRequested()
    {
        if (_pasteIssued)
        {
            _consumed.TrySetResult();
        }
        else
        {
            _readBeforePaste = true;
        }
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
