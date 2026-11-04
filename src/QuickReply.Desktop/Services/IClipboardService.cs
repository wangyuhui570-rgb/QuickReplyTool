namespace QuickReply.Desktop.Services;

internal interface IClipboardService
{
    Task<ClipboardSnapshot> CaptureSnapshotAsync();

    Task<ClipboardFingerprint?> CaptureCurrentFingerprintAsync();

    Task<ReplyActionResult> SetTextAsync(string text, bool persist = true);

    Task<ReplyActionResult> SetImageAsync(string imagePath, bool persist = true);

    Task<ReplyActionResult> SetTextAndFilesAsync(string text, IReadOnlyCollection<string> filePaths, bool persist = true);

    Task<ReplyActionResult> SetFileDropListAsync(IReadOnlyCollection<string> filePaths, bool persist = true);

    /// <summary>Call right before Ctrl+V: reads of the current payload from now on count as the paste.</summary>
    void NotePasteIssued()
    {
    }

    /// <summary>
    /// Waits until the target app has read the payload pasted last. Null when nothing is pending;
    /// false when the read could not be observed within <paramref name="timeoutMs"/>.
    /// </summary>
    Task<bool?> WaitForPasteConsumedAsync(int timeoutMs) => Task.FromResult<bool?>(null);

    Task<ReplyActionResult> RestoreSnapshotAsync(
        ClipboardSnapshot snapshot,
        ClipboardFingerprint? expectedCurrentFingerprint = null);
}
