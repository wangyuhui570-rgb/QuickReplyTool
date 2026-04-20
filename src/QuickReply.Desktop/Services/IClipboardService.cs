namespace QuickReply.Desktop.Services;

internal interface IClipboardService
{
    Task<ClipboardSnapshot> CaptureSnapshotAsync();

    Task<ClipboardFingerprint?> CaptureCurrentFingerprintAsync();

    Task<ReplyActionResult> SetTextAsync(string text, bool persist = true);

    Task<ReplyActionResult> SetImageAsync(string imagePath, bool persist = true);

    Task<ReplyActionResult> SetTextAndFilesAsync(string text, IReadOnlyCollection<string> filePaths, bool persist = true);

    Task<ReplyActionResult> SetFileDropListAsync(IReadOnlyCollection<string> filePaths, bool persist = true);

    Task<ReplyActionResult> RestoreSnapshotAsync(
        ClipboardSnapshot snapshot,
        ClipboardFingerprint? expectedCurrentFingerprint = null);
}
