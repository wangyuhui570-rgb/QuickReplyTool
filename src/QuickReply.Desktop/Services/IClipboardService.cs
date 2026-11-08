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
    /// Waits until the payload pasted last may be replaced (see <see cref="PasteGapPolicy"/>). Null when
    /// nothing is pending; otherwise whether any read after the paste was seen.
    /// </summary>
    Task<bool?> WaitForPasteConsumedAsync(int timeoutMs) => Task.FromResult<bool?>(null);

    /// <summary>What was seen of the payload pasted last, for the send log.</summary>
    PasteWaitSnapshot? DescribeLastPaste() => null;

    Task<ReplyActionResult> RestoreSnapshotAsync(
        ClipboardSnapshot snapshot,
        ClipboardFingerprint? expectedCurrentFingerprint = null);
}

public sealed record PasteWaitSnapshot(bool ReadBeforePaste, int ReadsAfterPaste, long MillisecondsSincePaste);
