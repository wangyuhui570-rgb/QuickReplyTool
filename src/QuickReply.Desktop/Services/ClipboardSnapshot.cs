namespace QuickReply.Desktop.Services;

public sealed class ClipboardSnapshot
{
    public ClipboardSnapshot(
        bool captureSucceeded,
        bool hasContent,
        System.Windows.IDataObject? dataObject,
        ClipboardFingerprint? fingerprint,
        bool canRestore = true)
    {
        CaptureSucceeded = captureSucceeded;
        HasContent = hasContent;
        DataObject = dataObject;
        Fingerprint = fingerprint;
        CanRestore = canRestore;
    }

    public bool CaptureSucceeded { get; }

    public bool HasContent { get; }

    public ClipboardFingerprint? Fingerprint { get; }

    public bool CanRestore { get; }

    internal System.Windows.IDataObject? DataObject { get; }
}
