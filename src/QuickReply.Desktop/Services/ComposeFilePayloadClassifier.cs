using System.IO;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal enum ComposeFilePayloadKind
{
    File,
    Image,
    Video,
    MixedMedia,
    MixedFiles
}

internal sealed record ComposeFilePayloadProfile(
    ComposeFilePayloadKind Kind,
    int FileCount,
    bool ContainsImage,
    bool ContainsVideo,
    bool ContainsOtherFile,
    string[] Extensions);

internal sealed record ComposeFileTransferPlan(
    ComposeFilePayloadProfile Payload,
    string StrategyName,
    bool SkipAutomaticSendHotkey,
    string SkipAutomaticSendHotkeyReason,
    string ManualConfirmationStage,
    string ManualConfirmationMessage,
    string ManualConfirmationUserGuidance,
    string ManualConfirmationOperatorState,
    string ManualConfirmationNextAction,
    int PasteStableMs,
    int PasteTimeoutMs,
    int SendStableMs,
    int SendTimeoutMs);

internal static class ComposeFilePayloadClassifier
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp",
        ".gif",
        ".jpeg",
        ".jpg",
        ".png",
        ".webp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi",
        ".flv",
        ".m4v",
        ".mkv",
        ".mov",
        ".mp4",
        ".mpeg",
        ".mpg",
        ".webm",
        ".wmv"
    };

    public static ComposeFilePayloadProfile Classify(IReadOnlyCollection<string> filePaths)
    {
        var rawExtensions = filePaths
            .Select(path => Path.GetExtension(path)?.Trim() ?? string.Empty)
            .ToArray();
        var extensions = rawExtensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var containsImage = extensions.Any(ImageExtensions.Contains);
        var containsVideo = extensions.Any(VideoExtensions.Contains);
        var containsOtherFile = rawExtensions.Any(extension =>
            string.IsNullOrWhiteSpace(extension) ||
            (!ImageExtensions.Contains(extension) && !VideoExtensions.Contains(extension)));
        var kind = (containsImage, containsVideo, containsOtherFile) switch
        {
            (true, false, false) => ComposeFilePayloadKind.Image,
            (false, true, false) => ComposeFilePayloadKind.Video,
            (true, true, false) => ComposeFilePayloadKind.MixedMedia,
            (false, false, _) => ComposeFilePayloadKind.File,
            _ => ComposeFilePayloadKind.MixedFiles
        };

        return new ComposeFilePayloadProfile(
            kind,
            filePaths.Count,
            containsImage,
            containsVideo,
            containsOtherFile,
            extensions);
    }

    public static ComposeFileTransferPlan BuildTransferPlan(
        ComposeFilePayloadProfile payload,
        SendHostProfile hostProfile,
        AppSettings settings)
    {
        var pasteStableMs = hostProfile.FilePasteStableMs;
        var pasteTimeoutMs = Math.Max(hostProfile.FilePasteTimeoutMs, settings.ImagePasteDelayMs);
        var sendStableMs = hostProfile.RichSendStableMs;
        var sendTimeoutMs = Math.Max(hostProfile.RichSendTimeoutMs, settings.SendDelayMs);
        var strategyName = hostProfile.PreferConservativeRichPath ? "safe_file_drop_send" : "file_drop_send";
        var skipAutomaticSendHotkey = false;
        var skipAutomaticSendHotkeyReason = string.Empty;
        var manualConfirmationStage = string.Empty;
        var manualConfirmationMessage = string.Empty;
        var manualConfirmationUserGuidance = string.Empty;
        var manualConfirmationOperatorState = string.Empty;
        var manualConfirmationNextAction = string.Empty;

        // Only Douyin/抖店 videos skip auto-send this round; other hosts keep existing behavior.
        if (payload.ContainsVideo && hostProfile.Kind == SendHostKind.DouyinWorkbench)
        {
            var manualConfirmationDecision = ComposeVideoManualConfirmationPolicy.Evaluate(hostProfile.Kind);
            if (manualConfirmationDecision.RequiresManualConfirmation)
            {
                strategyName = manualConfirmationDecision.StrategyName;
                skipAutomaticSendHotkey = true;
                skipAutomaticSendHotkeyReason = manualConfirmationDecision.Reason;
                manualConfirmationStage = manualConfirmationDecision.StageName;
                manualConfirmationMessage = manualConfirmationDecision.Message;
                manualConfirmationUserGuidance = manualConfirmationDecision.UserGuidance;
                manualConfirmationOperatorState = manualConfirmationDecision.OperatorState;
                manualConfirmationNextAction = manualConfirmationDecision.NextAction;
                pasteStableMs = Math.Max(pasteStableMs, hostProfile.ActivationStableMs);
            }
        }

        return new ComposeFileTransferPlan(
            payload,
            strategyName,
            skipAutomaticSendHotkey,
            skipAutomaticSendHotkeyReason,
            manualConfirmationStage,
            manualConfirmationMessage,
            manualConfirmationUserGuidance,
            manualConfirmationOperatorState,
            manualConfirmationNextAction,
            pasteStableMs,
            pasteTimeoutMs,
            sendStableMs,
            sendTimeoutMs);
    }
}
