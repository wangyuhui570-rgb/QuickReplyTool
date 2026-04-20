namespace QuickReply.Desktop.Services;

public sealed class ClipboardFingerprint
{
    public ClipboardFingerprint(string value)
    {
        Value = value;
    }

    public string Value { get; }
}
