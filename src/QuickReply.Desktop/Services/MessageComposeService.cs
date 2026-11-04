using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Native;

namespace QuickReply.Desktop.Services;

public sealed class MessageComposeService : IMessageComposeService
{
    private const int TargetActivationAttemptCount = 2;
    private const int MaximumComposeAttempts = 2;

    private readonly IExternalWorkbenchService _externalWorkbenchService;
    private readonly IClipboardService _clipboardService;
    private readonly IKeyboardInputSimulator _keyboardInputSimulator;
    private readonly IWindowFocusService _windowFocusService;

    public MessageComposeService(
        IExternalWorkbenchService externalWorkbenchService,
        ClipboardService clipboardService,
        KeyboardInputSimulator keyboardInputSimulator)
        : this(
            externalWorkbenchService,
            (IClipboardService)clipboardService,
            (IKeyboardInputSimulator)keyboardInputSimulator,
            new WindowFocusService())
    {
    }

    internal MessageComposeService(
        IExternalWorkbenchService externalWorkbenchService,
        IClipboardService clipboardService,
        IKeyboardInputSimulator keyboardInputSimulator)
        : this(
            externalWorkbenchService,
            clipboardService,
            keyboardInputSimulator,
            new WindowFocusService())
    {
    }

    internal MessageComposeService(
        IExternalWorkbenchService externalWorkbenchService,
        IClipboardService clipboardService,
        IKeyboardInputSimulator keyboardInputSimulator,
        IWindowFocusService windowFocusService,
        IDouyinVideoUploader? douyinVideoUploader = null,
        DouyinVideoSendGuard? douyinVideoSendGuard = null,
        IDouyinSendProgressVerifier? douyinSendProgressVerifier = null,
        IDouyinImageSendDialogHandler? douyinImageSendDialogHandler = null)
    {
        _douyinImageSendDialogHandler = douyinImageSendDialogHandler ?? new DouyinImageSendDialogHandler();
        _externalWorkbenchService = externalWorkbenchService;
        _clipboardService = clipboardService;
        _keyboardInputSimulator = keyboardInputSimulator;
        _windowFocusService = windowFocusService;
        _douyinVideoUploader = douyinVideoUploader ?? new DouyinVideoOpenDialogUploader(keyboardInputSimulator, _douyinImageSendDialogHandler);
        _douyinVideoSendGuard = douyinVideoSendGuard ?? SharedDouyinVideoSendGuard;
        _douyinSendProgressVerifier = douyinSendProgressVerifier
            ?? new DouyinSendProgressVerifier(keyboardInputSimulator, clipboardService);
    }

    private static readonly DouyinVideoSendGuard SharedDouyinVideoSendGuard = new();
    private readonly IDouyinVideoUploader _douyinVideoUploader;
    private readonly DouyinVideoSendGuard _douyinVideoSendGuard;
    private readonly IDouyinSendProgressVerifier _douyinSendProgressVerifier;
    private readonly IDouyinImageSendDialogHandler _douyinImageSendDialogHandler;
    private readonly object _pendingClipboardRestoreSync = new();
    private PendingClipboardRestore? _pendingClipboardRestore;

    public async Task<ReplyActionResult> CopyToClipboardAsync(string text, IReadOnlyCollection<string>? imagePaths = null)
    {
        var normalizedImagePaths = NormalizePaths(imagePaths ?? []);
        var hasText = !string.IsNullOrWhiteSpace(text);
        var hasImages = normalizedImagePaths.Length > 0;

        if (!hasText && !hasImages)
        {
            return CreateFailureResult(
                ReplyActionFailureKind.EmptyContent,
                "当前图文内容为空，未写入剪贴板。",
                "请先输入文本内容或选择图片后再复制。");
        }

        var missingImagePath = normalizedImagePaths.FirstOrDefault(path => !File.Exists(path));
        if (missingImagePath is not null)
        {
            return CreateFailureResult(
                ReplyActionFailureKind.MissingInput,
                $"未找到图片文件：{missingImagePath}",
                "请确认图片文件仍存在，或重新选择图片后再试。");
        }

        if (normalizedImagePaths.Length <= 1)
        {
            if (hasText && hasImages)
            {
                return await AnnotateClipboardResultAsync(
                    _clipboardService.SetTextAndFilesAsync(text, normalizedImagePaths),
                    "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
            }

            if (hasImages)
            {
                return await AnnotateClipboardResultAsync(
                    _clipboardService.SetFileDropListAsync(normalizedImagePaths),
                    "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
            }

            return await AnnotateClipboardResultAsync(
                _clipboardService.SetTextAsync(text),
                "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
        }

        if (!hasText)
        {
            return await AnnotateClipboardResultAsync(
                _clipboardService.SetFileDropListAsync(normalizedImagePaths),
                "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
        }

        return await AnnotateClipboardResultAsync(
            _clipboardService.SetTextAndFilesAsync(text, normalizedImagePaths),
            "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
    }

    public async Task<ReplyActionResult> CopyFilesToClipboardAsync(IReadOnlyCollection<string> filePaths)
    {
        var normalizedPaths = NormalizePaths(filePaths);
        if (normalizedPaths.Length == 0)
        {
            return CreateFailureResult(
                ReplyActionFailureKind.EmptyContent,
                "未选择要发送的文件。",
                "请先选择至少一个文件后再发送。");
        }

        return await AnnotateClipboardResultAsync(
            _clipboardService.SetFileDropListAsync(normalizedPaths),
            "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
    }

    internal const int DouyinImageHeadStartMs = 1200;
    internal const int DouyinPostTextBeforeImageSettleMs = DouyinComposerInputHelper.PostTextSendSettleMs;

    public async Task<ReplyActionResult> ExecuteAsync(
        string text,
        AppSettings settings,
        ComposeActionMode mode,
        IReadOnlyCollection<string>? imagePaths = null)
    {
        var normalizedImagePaths = NormalizePaths(imagePaths ?? []);
        var hasText = !string.IsNullOrWhiteSpace(text);
        var hasImages = normalizedImagePaths.Length > 0;

        if (!hasText && !hasImages)
        {
            return CreateFailureResult(
                ReplyActionFailureKind.EmptyContent,
                mode == ComposeActionMode.Send
                    ? "当前图文内容为空，无法发送。"
                    : "当前图文内容为空，无法输入。",
                hasImages
                    ? "请确认图片仍存在后再试。"
                    : "请先输入文本或选择图片后再试。");
        }

        var missingImagePath = normalizedImagePaths.FirstOrDefault(path => !File.Exists(path));
        if (missingImagePath is not null)
        {
            return CreateFailureResult(
                ReplyActionFailureKind.MissingInput,
                $"未找到图片文件：{missingImagePath}",
                "请确认图片文件仍存在，或重新选择图片后再试。");
        }

        if (!hasImages)
        {
            return await ExecuteWithAdaptiveRetryAsync(
                "text_only",
                mode,
                settings,
                attemptContext => ExecuteTextOnlyAsync(text, settings, mode, attemptContext));
        }

        if (!hasText)
        {
            return await ExecuteWithAdaptiveRetryAsync(
                "files",
                mode,
                settings,
                attemptContext => ExecuteFilesCoreAsync(normalizedImagePaths, settings, mode, attemptContext));
        }

        return await ExecuteWithAdaptiveRetryAsync(
            "text_with_file_images",
            mode,
            settings,
            attemptContext => ExecuteTextWithFileBackedImagesAsync(text, normalizedImagePaths, settings, mode, attemptContext));
    }

    private async Task<ReplyActionResult> ExecuteWithAdaptiveRetryAsync(
        string composeKind,
        ComposeActionMode mode,
        AppSettings settings,
        Func<ComposeAttemptContext, Task<ReplyActionResult>> executeAttemptAsync)
    {
        var firstAttemptContext = CreateAttemptContext(composeKind, settings, attemptIndex: 1, attemptReason: "initial");
        var firstResult = await executeAttemptAsync(firstAttemptContext).ConfigureAwait(false);
        if (firstResult.Success)
        {
            return CompleteComposeOperation(
                firstAttemptContext,
                mode,
                firstResult,
                ComposeOperationTelemetry.Create(
                    composeKind,
                    firstAttemptContext.HostProfile.Name,
                    attemptCount: 1,
                    retryTriggered: false,
                    retryReason: string.Empty,
                    retryOutcome: "not_needed",
                    failureCategory: "none"));
        }

        var firstFailureCategory = MapFailureCategory(firstResult);
        // Douyin failures must not paste, click, or send a second time. The user retries by hand.
        if (firstAttemptContext.HostProfile.Kind == SendHostKind.DouyinWorkbench)
        {
            LogDouyinSendStopped(firstAttemptContext, firstResult, firstFailureCategory);
            return CompleteComposeOperation(
                firstAttemptContext,
                mode,
                firstResult,
                ComposeOperationTelemetry.Create(
                    composeKind,
                    firstAttemptContext.HostProfile.Name,
                    attemptCount: 1,
                    retryTriggered: false,
                    retryReason: string.Empty,
                    retryOutcome: "douyin_no_retry",
                    failureCategory: firstFailureCategory));
        }

        if (MaximumComposeAttempts <= 1 ||
            !TryBuildRetryDecision(firstResult, out var retryDecision))
        {
            return CompleteComposeOperation(
                firstAttemptContext,
                mode,
                firstResult,
                ComposeOperationTelemetry.Create(
                    composeKind,
                    firstAttemptContext.HostProfile.Name,
                    attemptCount: 1,
                    retryTriggered: false,
                    retryReason: string.Empty,
                    retryOutcome: "not_applicable",
                    failureCategory: firstFailureCategory));
        }

        await Task.Delay(firstAttemptContext.HostProfile.RetryBackoffMs).ConfigureAwait(false);

        var retryContextPreparation = PrepareRetryAttemptContext(firstAttemptContext, settings, retryDecision);
        if (retryContextPreparation.BlockedResult is not null)
        {
            return CompleteComposeOperation(
                firstAttemptContext,
                mode,
                retryContextPreparation.BlockedResult.Value,
                ComposeOperationTelemetry.Create(
                    composeKind,
                    firstAttemptContext.HostProfile.Name,
                    attemptCount: 1,
                    retryTriggered: false,
                    retryReason: retryDecision.RetryReason,
                    retryOutcome: "cancelled_target_changed",
                    failureCategory: MapFailureCategory(retryContextPreparation.BlockedResult.Value),
                    targetChangedBeforeRetry: retryContextPreparation.TargetChangedBeforeRetry));
        }

        var retryAttemptContext = retryContextPreparation.AttemptContext!;
        var retryResult = await executeAttemptAsync(retryAttemptContext).ConfigureAwait(false);
        var retryOutcome = retryResult.Success ? "recovered" : "failed";
        var retryTelemetry = ComposeOperationTelemetry.Create(
            composeKind,
            retryAttemptContext.HostProfile.Name,
            attemptCount: retryAttemptContext.AttemptIndex,
            retryTriggered: true,
            retryReason: retryDecision.RetryReason,
            retryOutcome: retryOutcome,
            failureCategory: retryResult.Success ? "none" : MapFailureCategory(retryResult),
            targetChangedBeforeRetry: retryContextPreparation.TargetChangedBeforeRetry);

        if (retryResult.Success)
        {
            return CompleteComposeOperation(
                retryAttemptContext,
                mode,
                new ReplyActionResult(true, $"首次尝试未稳定完成，已自动补救后成功：{retryResult.Message}"),
                retryTelemetry);
        }

        return CompleteComposeOperation(
            retryAttemptContext,
            mode,
            new ReplyActionResult(
                false,
                $"已自动补救 1 次，但仍失败：{retryResult.Message}",
                FailureKind: ResolveFailureKind(retryResult),
                UserGuidance: retryResult.UserGuidance),
            retryTelemetry);
    }

    private async Task<ReplyActionResult> ExecuteTextWithFileBackedImagesAsync(
        string text,
        IReadOnlyList<string> imagePaths,
        AppSettings settings,
        ComposeActionMode mode,
        ComposeAttemptContext attemptContext)
    {
        if (attemptContext.HostProfile.Kind == SendHostKind.DouyinWorkbench &&
            (imagePaths.Any(IsVideoPath) || UsesDouyinToolbarForImages(mode, imagePaths)))
        {
            return await ExecuteDouyinPayloadWithVideosAsync(text, imagePaths, settings, mode, attemptContext);
        }

        var diagnostics = CreateComposeDiagnosticsPayload("text_with_file_images", mode);
        var stopwatch = Stopwatch.StartNew();
        PopulateAttemptDiagnostics(diagnostics, attemptContext);
        var isDouyin = attemptContext.HostProfile.Kind == SendHostKind.DouyinWorkbench;
        var phraseImageSendMode = Enum.IsDefined(settings.PhraseImageSendMode)
            ? settings.PhraseImageSendMode
            : PhraseImageSendMode.ImageThenTextThenSend;
        var imageFirst = phraseImageSendMode != PhraseImageSendMode.TextThenImageThenSend;
        // Douyin swallows the text when image+text share one Enter, so it always sends them as
        // two messages, in the order chosen in settings. "图片先发" also forces two messages elsewhere.
        var useSeparateMessages = mode == ComposeActionMode.Send &&
            (isDouyin || phraseImageSendMode == PhraseImageSendMode.ImageImmediateThenTextSend);
        diagnostics["selectedSendPath"] = (useSeparateMessages, imageFirst) switch
        {
            (true, true) => isDouyin ? "douyin_image_send_then_text_send" : "image_send_then_text_send",
            (true, false) => isDouyin ? "douyin_text_send_then_image_send" : "text_send_then_image_send",
            (false, true) => attemptContext.HostProfile.PreferConservativeRichPath
                ? "safe_file_drop_then_text_send"
                : "file_drop_then_text_send",
            (false, false) => "text_then_file_drop_send"
        };
        diagnostics["richImageSeparateMessages"] = useSeparateMessages;

        var activationResult = await EnsureTargetWindowReadyAsync(settings, diagnostics, stopwatch, attemptContext);
        if (!activationResult.Success)
        {
            diagnostics["result"] = activationResult.FailureCode;
            diagnostics["totalMs"] = stopwatch.ElapsedMilliseconds;
            SearchOverlayDiagnostics.Log("message_compose", "compose_rich_completed", diagnostics);
            return CreateTargetWindowUnavailableResult(activationResult.UserMessage);
        }

        diagnostics["activateWindowMs"] = stopwatch.ElapsedMilliseconds;
        diagnostics["snapshotMs"] = stopwatch.ElapsedMilliseconds;
        diagnostics["effectiveRestoreFocusDelayMs"] = GetEffectiveRichRestoreFocusDelayMs(settings, attemptContext.HostProfile);
        diagnostics["hasText"] = true;
        diagnostics["imageCount"] = imagePaths.Count;
        diagnostics["phraseImageSendMode"] = settings.PhraseImageSendMode.ToString();
        diagnostics["effectivePhraseImageSendMode"] = phraseImageSendMode.ToString();
        diagnostics["imageTransferStrategy"] = "file_drop_clipboard";

        var clipboardSnapshot = await CaptureClipboardSnapshotAsync(diagnostics, stopwatch);

        ClipboardFingerprint? clipboardPayloadFingerprint = null;
        var sentStageCount = 0;

        async Task<ReplyActionResult> FailAsync(string resultCode, ReplyActionResult failure)
        {
            if (clipboardSnapshot.CaptureSucceeded && clipboardPayloadFingerprint is not null)
            {
                await TryRestoreClipboardAsync(clipboardSnapshot, clipboardPayloadFingerprint);
            }

            diagnostics["result"] = resultCode;
            diagnostics["message"] = failure.Message;
            diagnostics["richImageSentStageCount"] = sentStageCount;
            diagnostics["totalMs"] = stopwatch.ElapsedMilliseconds;
            SearchOverlayDiagnostics.Log("message_compose", "compose_rich_completed", diagnostics);
            // Once one message is out, a retry would resend it – make the failure non-retryable.
            var failureKind = sentStageCount > 0
                ? ReplyActionFailureKind.ManualConfirmationRequired
                : ResolveFailureKind(failure);
            var prefix = sentStageCount > 0 ? "图文已发出第一条，第二条失败：" : "发送图文内容失败：";
            return CreateFailureResult(
                failureKind,
                $"{prefix}{failure.Message}",
                sentStageCount > 0 ? "请检查会话里已发出的内容，再手动补发剩余部分。" : failure.UserGuidance);
        }

        var douyinImagePasteConfirmed = false;
        var douyinImageSentViaDialog = false;

        async Task<ReplyActionResult?> PasteImagesAsync(bool prepareClipboardOnly = false)
        {
            // Write file_drop once; on Douyin text→image the write can start during the post-text settle.
            if (clipboardPayloadFingerprint is null || diagnostics["lastClipboardWriteKind"] as string != "file_drop")
            {
                var fileDropWrite = await TryWriteClipboardPayloadAsync(
                    () => _clipboardService.SetFileDropListAsync(imagePaths, persist: false),
                    "file_drop",
                    diagnostics,
                    stopwatch,
                    ReplyActionFailureKind.ClipboardBusy,
                    "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。",
                    imagePaths[0]);
                if (fileDropWrite.FailureResult is not null)
                {
                    return await FailAsync("clipboard_write_failed", fileDropWrite.FailureResult.Value);
                }

                clipboardPayloadFingerprint = fileDropWrite.Fingerprint;
            }

            if (prepareClipboardOnly)
            {
                return null;
            }

            // One paste only. A missed confirm must not click or paste again.
            diagnostics["douyinImagePasteAttempt"] = 1;
            diagnostics["douyinImagePasteMaxAttempts"] = DouyinComposerInputHelper.MaxImagePasteAttempts;
            var watchForImageDialog = isDouyin && mode == ComposeActionMode.Send;
            DouyinSendButtonSnapshot buttonsBeforePaste = default;
            if (isDouyin)
            {
                // Stop instead of pulling Douyin back to the front when the user switched away.
                if (DidForegroundLeaveTarget(activationResult.TargetWindowHandle, diagnostics, "before_composer_focus"))
                {
                    return await FailAsync(
                        "foreground_left",
                        CreateFailureResult(
                            ReplyActionFailureKind.FocusLost,
                            "焦点已离开抖店窗口，图片没有粘贴。",
                            "请回到原来的抖店会话后再发一次。本次没有自动重试，也没有再抢焦点。"));
                }

                DouyinComposerInputHelper.TryFocusComposer(activationResult.TargetWindowHandle, diagnostics);
                await Task.Delay(DouyinComposerInputHelper.PostFocusSettleMs).ConfigureAwait(false);
                if (watchForImageDialog)
                {
                    buttonsBeforePaste = await Task.Run(
                            () => _douyinImageSendDialogHandler.CaptureSendButtons(activationResult.TargetWindowHandle))
                        .ConfigureAwait(false);
                }
            }

            var pasteFailure = await TryPasteClipboardPayloadAsync(
                activationResult.TargetWindowHandle,
                "file_drop",
                attemptContext.HostProfile.FilePasteStableMs,
                GetEffectiveImagePasteDelayMs(settings, attemptContext.HostProfile),
                attemptContext.HostProfile.PollIntervalMs,
                diagnostics,
                stopwatch,
                settleMsDiagnosticKey: "effectiveImageSettleDelayMs");
            if (pasteFailure is not null)
            {
                if (isDouyin && diagnostics.ContainsKey("lastPasteKind"))
                {
                    return await FailAsync(
                        "send_failed",
                        CreateFailureResult(
                            ReplyActionFailureKind.FocusLost,
                            "图片只粘贴了一次，随后焦点离开了抖店窗口，已停止，没有再粘贴也没有按发送键。",
                            "请先看输入框里有没有这张图片。有的话手动发送；没有的话再发一次。本次不会自动重试。"));
                }

                return await FailAsync("send_failed", pasteFailure.Value);
            }

            if (!isDouyin)
            {
                return null;
            }

            if (watchForImageDialog)
            {
                var dialogOutcome = await _douyinImageSendDialogHandler.TryConfirmAsync(
                    activationResult.TargetWindowHandle,
                    buttonsBeforePaste,
                    diagnostics).ConfigureAwait(false);
                diagnostics["douyinImageDialogOutcome"] = dialogOutcome.ToString();
                if (dialogOutcome == DouyinImageDialogOutcome.Confirmed)
                {
                    douyinImagePasteConfirmed = true;
                    douyinImageSentViaDialog = true;
                    diagnostics["douyinImagePasteConfirmed"] = true;
                    return null;
                }

                if (dialogOutcome == DouyinImageDialogOutcome.ConfirmFailed)
                {
                    return await FailAsync(
                        "image_dialog_unconfirmed",
                        CreateFailureResult(
                            ReplyActionFailureKind.ManualConfirmationRequired,
                            "抖店弹出了发送图片的确认框，但没能自动点上发送，已停止。",
                            "请在抖店的确认框里手动点发送，再把剩下的内容补上。本次不会自动重试。"));
                }
            }

            var confirm = await _douyinSendProgressVerifier.ConfirmComposerHasImageAsync(
                activationResult.TargetWindowHandle,
                imagePaths,
                diagnostics).ConfigureAwait(false);
            diagnostics["douyinImagePasteConfirmOutcome"] = confirm.Outcome.ToString();
            diagnostics["douyinImagePasteConfirmDetail"] = confirm.Detail;
            if (confirm.Outcome == DouyinSendVerifyOutcome.StillHasContent)
            {
                douyinImagePasteConfirmed = true;
                diagnostics["douyinImagePasteConfirmed"] = true;
                return null;
            }

            // Chromium usually hides the preview from UIA. Accept that once and send once.
            // Cleared means the composer looked empty: do not press Enter, and do not paste again.
            if (confirm.Outcome == DouyinSendVerifyOutcome.Indeterminate)
            {
                douyinImagePasteConfirmed = true;
                diagnostics["douyinImagePasteConfirmed"] = true;
                diagnostics["douyinImagePasteSoftConfirm"] = true;
                return null;
            }

            return await FailAsync(
                "image_paste_unconfirmed",
                CreateFailureResult(
                    ReplyActionFailureKind.ManualConfirmationRequired,
                    "图片未能贴进抖店输入框，已停止。没有再次粘贴，也没有按发送键。",
                    "请看一下抖店输入框：图片如果已经在里面，手动点发送即可；如果不在，再发一次。本次不会自动重试。"));
        }

        async Task<ReplyActionResult?> PasteTextStepAsync()
        {
            var textPaste = await PasteTextAsync(
                text,
                settings,
                activationResult.TargetWindowHandle,
                diagnostics,
                stopwatch,
                attemptContext.HostProfile);
            if (textPaste.FailureResult is not null)
            {
                return await FailAsync("send_failed", textPaste.FailureResult.Value);
            }

            clipboardPayloadFingerprint = textPaste.Fingerprint;
            return null;
        }

        async Task<ReplyActionResult?> SendStepAsync(
            bool composerHasImages,
            string delayKey,
            DouyinSendVerifyExpectation? verifyExpectation = null,
            bool imagePasteConfirmed = false)
        {
            // The image already went out through Douyin's confirm dialog. Enter here would hit
            // an empty composer, and the verify probe would only risk cancelling something.
            if (isDouyin && composerHasImages && douyinImageSentViaDialog)
            {
                diagnostics["douyinImageSendViaDialogSkippedHotkey"] = true;
                sentStageCount++;
                diagnostics["richImageSentStageCount"] = sentStageCount;
                return null;
            }

            // Never empty-Enter an image stage when Douyin paste was not confirmed.
            if (isDouyin &&
                composerHasImages &&
                verifyExpectation is { SentImagePaths: { Count: > 0 } } &&
                !imagePasteConfirmed)
            {
                return await FailAsync(
                    "image_paste_unconfirmed",
                    CreateFailureResult(
                        ReplyActionFailureKind.ManualConfirmationRequired,
                        "图片未确认贴入输入框，已跳过发送键。",
                        "请看一下抖店输入框。本次不会自动重试。"));
            }

            var sendFailure = await TrySendConfiguredHotkeyAsync(
                activationResult.TargetWindowHandle,
                settings.SendHotkeyMode,
                attemptContext.HostProfile.RichSendStableMs,
                composerHasImages
                    ? GetEffectiveRichSendDelayMs(settings, attemptContext.HostProfile, hasImages: true)
                    : GetEffectiveRichSendDelayMs(settings, attemptContext.HostProfile, hasImages: false),
                attemptContext.HostProfile.PollIntervalMs,
                diagnostics,
                stopwatch,
                delayMsDiagnosticKey: delayKey);
            if (sendFailure is not null)
            {
                return await FailAsync("send_failed", sendFailure.Value);
            }

            if (isDouyin && verifyExpectation is { } expectation)
            {
                var verify = await _douyinSendProgressVerifier.VerifyComposerClearedAsync(
                    activationResult.TargetWindowHandle,
                    expectation,
                    diagnostics).ConfigureAwait(false);
                diagnostics["douyinSendVerifyOutcome"] = verify.Outcome.ToString();
                diagnostics["douyinSendVerifyMethod"] = verify.Method;
                if (_douyinSendProgressVerifier.ClipboardFingerprintAfterLastProbe is { } probeFingerprint)
                {
                    // The probe put our payload back on the clipboard; the queued restore must
                    // compare against that state or it would skip and leave the user's clipboard lost.
                    clipboardPayloadFingerprint = probeFingerprint;
                }
                if (verify.Outcome == DouyinSendVerifyOutcome.StillHasContent)
                {
                    return await FailAsync(
                        "send_not_observed",
                        CreateFailureResult(
                            ReplyActionFailureKind.ManualConfirmationRequired,
                            "已按发送键，但输入框里的内容还在，抖店似乎没有真正发出。",
                            "请确认抖店聊天输入框已聚焦、发送热键与抖店设置一致，再手动点发送或重试。"));
                }

                if (verify.Outcome == DouyinSendVerifyOutcome.Indeterminate)
                {
                    // Image stage: empty_selection* is now Indeterminate (ambiguous). If we confirmed
                    // the paste before Enter, accept only that empty-ambiguous case as success.
                    var imageStage = expectation.SentImagePaths is { Count: > 0 };
                    var emptyAmbiguous = verify.Detail.Contains("empty_selection", StringComparison.OrdinalIgnoreCase)
                        || verify.Detail.Contains("image_uncertain", StringComparison.OrdinalIgnoreCase);
                    if (imageStage && imagePasteConfirmed && emptyAmbiguous)
                    {
                        diagnostics["douyinSendVerifyAcceptedViaPasteConfirm"] = true;
                    }
                    else
                    {
                        return await FailAsync(
                            "send_unconfirmed",
                            CreateFailureResult(
                                ReplyActionFailureKind.ManualConfirmationRequired,
                                "已按发送键，但无法确认会话侧是否有进展（输入框未检出清空）。",
                                "请看一眼抖店会话：若消息已出现可忽略；若没有，请手动发送或重试。不要把这次当成已成功。"));
                    }
                }
            }

            sentStageCount++;
            diagnostics["richImageSentStageCount"] = sentStageCount;
            return null;
        }

        var firstFailure = imageFirst ? await PasteImagesAsync() : await PasteTextStepAsync();
        if (firstFailure is not null)
        {
            return firstFailure.Value;
        }

        if (useSeparateMessages)
        {
            var firstSendFailure = await SendStepAsync(
                composerHasImages: imageFirst,
                delayKey: imageFirst ? "effectiveRichImageSendDelayMs" : "effectiveRichSendDelayMs",
                verifyExpectation: imageFirst
                    ? new DouyinSendVerifyExpectation(SentText: null, SentImagePaths: imagePaths)
                    : new DouyinSendVerifyExpectation(SentText: text, SentImagePaths: null),
                imagePasteConfirmed: imageFirst && douyinImagePasteConfirmed);
            if (firstSendFailure is not null)
            {
                return firstSendFailure.Value;
            }

            if (isDouyin && imageFirst)
            {
                // 2026-10-23 live test: the text landed above the image. Douyin uploads the image
                // asynchronously after Enter; pasting the text right away lets the text message
                // overtake it. Give the image a head start before the second message.
                diagnostics["douyinImageHeadStartMs"] = DouyinImageHeadStartMs;
                await Task.Delay(DouyinImageHeadStartMs).ConfigureAwait(false);
                if (DidForegroundLeaveTarget(activationResult.TargetWindowHandle, diagnostics, "image_head_start"))
                {
                    diagnostics["douyinSecondStageAborted"] = "foreground_left_during_image_head_start";
                    return await FailAsync(
                        "foreground_left",
                        CreateFailureResult(
                            ReplyActionFailureKind.FocusLost,
                            "等待期间焦点离开了抖店窗口，文字没有再粘贴。",
                            "图片如果已经发出，只需手动把文字补上。本次没有自动重试，也没有再抢焦点。"));
                }
            }

            if (isDouyin && !imageFirst)
            {
                // TextThenImage: brief settle after text Enter, prepare file_drop early, then
                // one composer focus inside PasteImagesAsync before the single Ctrl+V.
                diagnostics["douyinPostTextBeforeImageSettleMs"] = DouyinPostTextBeforeImageSettleMs;
                var prepTask = PasteImagesAsync(prepareClipboardOnly: true);
                await Task.Delay(DouyinPostTextBeforeImageSettleMs).ConfigureAwait(false);
                var prepFailure = await prepTask.ConfigureAwait(false);
                if (prepFailure is not null)
                {
                    return prepFailure.Value;
                }

                if (DidForegroundLeaveTarget(activationResult.TargetWindowHandle, diagnostics, "before_image_paste"))
                {
                    diagnostics["douyinSecondStageAborted"] = "foreground_left_before_image_paste";
                    return await FailAsync(
                        "foreground_left",
                        CreateFailureResult(
                            ReplyActionFailureKind.FocusLost,
                            "焦点已离开抖店窗口，图片没有再粘贴。",
                            "请确认当前还停在原来的抖店会话后再手动补发图片。本次没有自动重试，也没有再抢焦点。"));
                }
            }
        }

        var secondFailure = imageFirst ? await PasteTextStepAsync() : await PasteImagesAsync();
        if (secondFailure is not null)
        {
            return secondFailure.Value;
        }

        if (mode == ComposeActionMode.Send)
        {
            DouyinSendVerifyExpectation? finalExpectation = null;
            if (isDouyin)
            {
                finalExpectation = useSeparateMessages
                    ? (imageFirst
                        ? new DouyinSendVerifyExpectation(SentText: text, SentImagePaths: null)
                        : new DouyinSendVerifyExpectation(SentText: null, SentImagePaths: imagePaths))
                    : new DouyinSendVerifyExpectation(SentText: text, SentImagePaths: imagePaths);
            }

            var finalHasImages = !useSeparateMessages || !imageFirst;
            var finalSendFailure = await SendStepAsync(
                composerHasImages: finalHasImages,
                delayKey: "effectiveRichSendDelayMs",
                verifyExpectation: finalExpectation,
                imagePasteConfirmed: finalHasImages && douyinImagePasteConfirmed);
            if (finalSendFailure is not null)
            {
                return finalSendFailure.Value;
            }
        }

        var effectiveClipboardRestoreDelayMs = GetEffectiveRichImageClipboardRestoreDelayMs(settings, attemptContext.HostProfile);
        var willRestoreClipboard = clipboardSnapshot.CanRestore && clipboardPayloadFingerprint is not null;
        if (willRestoreClipboard)
        {
            QueueClipboardRestore(clipboardSnapshot, effectiveClipboardRestoreDelayMs, clipboardPayloadFingerprint);
        }

        diagnostics["queuedClipboardRestoreDelayMs"] = willRestoreClipboard ? effectiveClipboardRestoreDelayMs : null;
        diagnostics["totalMs"] = stopwatch.ElapsedMilliseconds;
        diagnostics["result"] = "success";
        SearchOverlayDiagnostics.Log("message_compose", "compose_rich_completed", diagnostics);
        return BuildRichTextResult(mode, hasText: true, imagePaths.Count, clipboardSnapshot, restoreDeferred: willRestoreClipboard);
    }

    public async Task<ReplyActionResult> ExecuteFilesAsync(
        IReadOnlyCollection<string> filePaths,
        AppSettings settings,
        ComposeActionMode mode)
    {
        return await ExecuteWithAdaptiveRetryAsync(
            "files",
            mode,
            settings,
            attemptContext => ExecuteFilesCoreAsync(filePaths, settings, mode, attemptContext));
    }

    private async Task<ReplyActionResult> ExecuteFilesCoreAsync(
        IReadOnlyCollection<string> filePaths,
        AppSettings settings,
        ComposeActionMode mode,
        ComposeAttemptContext attemptContext)
    {
        var normalizedPaths = NormalizePaths(filePaths);
        if (normalizedPaths.Length == 0)
        {
            return CreateFailureResult(
                ReplyActionFailureKind.EmptyContent,
                "未选择要发送的文件。",
                "请先选择至少一个文件后再发送。");
        }

        var missingPath = normalizedPaths.FirstOrDefault(path => !File.Exists(path));
        if (missingPath is not null)
        {
            return CreateFailureResult(
                ReplyActionFailureKind.MissingInput,
                $"文件不存在：{missingPath}",
                "请确认文件仍存在，或重新选择文件后再试。");
        }

        var filePayload = ComposeFilePayloadClassifier.Classify(normalizedPaths);
        var transferPlan = ComposeFilePayloadClassifier.BuildTransferPlan(
            filePayload,
            attemptContext.HostProfile,
            settings);

        // Douyin ignores a FileDrop'd video: every video goes through the toolbar uploader.
        if (attemptContext.HostProfile.Kind == SendHostKind.DouyinWorkbench &&
            (filePayload.ContainsVideo || UsesDouyinToolbarForImages(mode, normalizedPaths)))
        {
            return await ExecuteDouyinPayloadWithVideosAsync(string.Empty, normalizedPaths, settings, mode, attemptContext);
        }

        var diagnostics = CreateComposeDiagnosticsPayload("files", mode);
        var stopwatch = Stopwatch.StartNew();
        PopulateAttemptDiagnostics(diagnostics, attemptContext);
        diagnostics["selectedSendPath"] = transferPlan.StrategyName;
        diagnostics["filePayloadKind"] = filePayload.Kind.ToString();
        diagnostics["fileCount"] = filePayload.FileCount;
        diagnostics["fileContainsImage"] = filePayload.ContainsImage;
        diagnostics["fileContainsVideo"] = filePayload.ContainsVideo;
        diagnostics["fileContainsOther"] = filePayload.ContainsOtherFile;
        diagnostics["fileExtensions"] = string.Join(",", filePayload.Extensions);

        var clipboardSnapshot = await CaptureClipboardSnapshotAsync(diagnostics, stopwatch);

        var fileDropWrite = await TryWriteClipboardPayloadAsync(
            () => _clipboardService.SetFileDropListAsync(normalizedPaths, persist: false),
            "file_drop",
            diagnostics,
            stopwatch,
            ReplyActionFailureKind.ClipboardBusy,
            "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。",
            normalizedPaths[0]);
        if (fileDropWrite.FailureResult is not null)
        {
            diagnostics["result"] = "clipboard_write_failed";
            diagnostics["message"] = fileDropWrite.FailureResult.Value.Message;
            SearchOverlayDiagnostics.Log("message_compose", "compose_files_completed", diagnostics);
            return fileDropWrite.FailureResult.Value;
        }

        diagnostics["clipboardWriteMs"] = stopwatch.ElapsedMilliseconds;
        var clipboardPayloadFingerprint = fileDropWrite.Fingerprint;

        var activationResult = await EnsureTargetWindowReadyAsync(settings, diagnostics, stopwatch, attemptContext);
        if (!activationResult.Success)
        {
            await TryRestoreClipboardAsync(clipboardSnapshot, clipboardPayloadFingerprint);
            diagnostics["result"] = activationResult.FailureCode;
            SearchOverlayDiagnostics.Log("message_compose", "compose_files_completed", diagnostics);
            return CreateTargetWindowUnavailableResult(activationResult.UserMessage);
        }

        diagnostics["activateWindowMs"] = stopwatch.ElapsedMilliseconds;
        diagnostics["effectiveRestoreFocusDelayMs"] = GetEffectiveRichRestoreFocusDelayMs(settings, attemptContext.HostProfile);
        var watchForImageDialog = mode == ComposeActionMode.Send &&
            attemptContext.HostProfile.Kind == SendHostKind.DouyinWorkbench &&
            filePayload.ContainsImage;
        var buttonsBeforePaste = watchForImageDialog
            ? await Task.Run(() => _douyinImageSendDialogHandler.CaptureSendButtons(activationResult.TargetWindowHandle))
                .ConfigureAwait(false)
            : default;
        var pasteFailure = await TryPasteClipboardPayloadAsync(
            activationResult.TargetWindowHandle,
            "file_drop",
            transferPlan.PasteStableMs,
            transferPlan.PasteTimeoutMs,
            attemptContext.HostProfile.PollIntervalMs,
            diagnostics,
            stopwatch,
            settleMsDiagnosticKey: "effectiveImageSettleDelayMs");
        if (pasteFailure is not null)
        {
            await TryRestoreClipboardAsync(clipboardSnapshot, clipboardPayloadFingerprint);
            diagnostics["result"] = "send_hotkey_skipped_target_not_foreground";
            diagnostics["totalMs"] = stopwatch.ElapsedMilliseconds;
            SearchOverlayDiagnostics.Log("message_compose", "compose_files_completed", diagnostics);
            return pasteFailure.Value;
        }

        var sentViaDialog = false;
        if (watchForImageDialog)
        {
            var dialogOutcome = await _douyinImageSendDialogHandler.TryConfirmAsync(
                activationResult.TargetWindowHandle,
                buttonsBeforePaste,
                diagnostics).ConfigureAwait(false);
            diagnostics["douyinImageDialogOutcome"] = dialogOutcome.ToString();
            if (dialogOutcome == DouyinImageDialogOutcome.ConfirmFailed)
            {
                await TryRestoreClipboardAsync(clipboardSnapshot, clipboardPayloadFingerprint);
                diagnostics["result"] = "image_dialog_unconfirmed";
                diagnostics["totalMs"] = stopwatch.ElapsedMilliseconds;
                SearchOverlayDiagnostics.Log("message_compose", "compose_files_completed", diagnostics);
                return CreateFailureResult(
                    ReplyActionFailureKind.ManualConfirmationRequired,
                    "抖店弹出了发送图片的确认框，但没能自动点上发送，已停止。",
                    "请在抖店的确认框里手动点发送。本次不会自动重试。");
            }

            sentViaDialog = dialogOutcome == DouyinImageDialogOutcome.Confirmed;
        }

        if (mode == ComposeActionMode.Send && !sentViaDialog)
        {
            if (transferPlan.SkipAutomaticSendHotkey)
            {
                diagnostics["sendHotkeySkipped"] = true;
                diagnostics["sendHotkeySkippedReason"] = transferPlan.SkipAutomaticSendHotkeyReason;
                diagnostics["manualConfirmationRequired"] = true;
                diagnostics["manualConfirmationReason"] = transferPlan.SkipAutomaticSendHotkeyReason;
                diagnostics["manualConfirmationStage"] = transferPlan.ManualConfirmationStage;
                diagnostics["manualConfirmationUserGuidance"] = transferPlan.ManualConfirmationUserGuidance;
                diagnostics["manualConfirmationOperatorState"] = transferPlan.ManualConfirmationOperatorState;
                diagnostics["manualConfirmationNextAction"] = transferPlan.ManualConfirmationNextAction;

                var effectiveClipboardRestoreDelayMs = GetEffectiveClipboardRestoreDelayMs(settings, attemptContext.HostProfile);
                var willRestoreClipboard = clipboardSnapshot.CanRestore && clipboardPayloadFingerprint is not null;
                if (willRestoreClipboard)
                {
                    QueueClipboardRestore(clipboardSnapshot, effectiveClipboardRestoreDelayMs, clipboardPayloadFingerprint);
                }

                diagnostics["queuedClipboardRestoreDelayMs"] = willRestoreClipboard ? effectiveClipboardRestoreDelayMs : null;
                diagnostics["totalMs"] = stopwatch.ElapsedMilliseconds;
                diagnostics["result"] = "manual_confirmation_required";
                SearchOverlayDiagnostics.Log("message_compose", "compose_files_completed", diagnostics);
                return CreateFailureResult(
                    ReplyActionFailureKind.ManualConfirmationRequired,
                    string.IsNullOrWhiteSpace(transferPlan.ManualConfirmationMessage)
                        ? $"已将 {normalizedPaths.Length} 个文件粘贴到当前窗口，请确认上传预览后再手动发送。"
                        : transferPlan.ManualConfirmationMessage,
                    string.IsNullOrWhiteSpace(transferPlan.ManualConfirmationUserGuidance)
                        ? "请确认目标窗口是否出现上传预览；若没有预览，请改用工作台上传按钮手动选择文件。"
                        : transferPlan.ManualConfirmationUserGuidance);
            }

            var sendFailure = await TrySendConfiguredHotkeyAsync(
                activationResult.TargetWindowHandle,
                settings.SendHotkeyMode,
                transferPlan.SendStableMs,
                transferPlan.SendTimeoutMs,
                attemptContext.HostProfile.PollIntervalMs,
                diagnostics,
                stopwatch,
                delayMsDiagnosticKey: "effectiveSendDelayMs");
            if (sendFailure is not null)
            {
                await TryRestoreClipboardAsync(clipboardSnapshot, clipboardPayloadFingerprint);
                diagnostics["result"] = "send_hotkey_skipped_target_not_foreground";
                diagnostics["totalMs"] = stopwatch.ElapsedMilliseconds;
                SearchOverlayDiagnostics.Log("message_compose", "compose_files_completed", diagnostics);
                return sendFailure.Value;
            }
        }

        var restoreDelayMs = GetEffectiveClipboardRestoreDelayMs(settings, attemptContext.HostProfile);
        var restoreClipboard = clipboardSnapshot.CanRestore && clipboardPayloadFingerprint is not null;
        if (restoreClipboard)
        {
            QueueClipboardRestore(clipboardSnapshot, restoreDelayMs, clipboardPayloadFingerprint);
        }

        diagnostics["queuedClipboardRestoreDelayMs"] = restoreClipboard ? restoreDelayMs : null;
        diagnostics["totalMs"] = stopwatch.ElapsedMilliseconds;
        diagnostics["result"] = "success";
        SearchOverlayDiagnostics.Log("message_compose", "compose_files_completed", diagnostics);
        return BuildFileResult(mode, normalizedPaths.Length, clipboardSnapshot, restoreDeferred: restoreClipboard);
    }

    private static bool IsVideoPath(string path)
    {
        return ComposeFilePayloadClassifier.Classify([path]).ContainsVideo;
    }

    /// <summary>
    /// Douyin payload that contains at least one video: text and images go first as separate
    /// messages (per 图文发送顺序), then each video is uploaded one at a time through the toolbar
    /// "发送视频" open-file dialog. A video is reported as "已提交" (submitted), never as delivered.
    /// </summary>
    private async Task<ReplyActionResult> ExecuteDouyinPayloadWithVideosAsync(
        string text,
        IReadOnlyList<string> paths,
        AppSettings settings,
        ComposeActionMode mode,
        ComposeAttemptContext attemptContext)
    {
        // Images go through the toolbar 图片 button when sending: a pasted image only opens Douyin's
        // "send this image?" dialog, which the paste path kept missing. Images before videos.
        var toolbarImages = UsesDouyinToolbarForImages(mode, paths);
        var videoPaths = paths
            .Where(path => IsVideoPath(path) || (toolbarImages && IsImagePath(path)))
            .OrderBy(IsVideoPath)
            .ToArray();
        var otherPaths = paths.Where(path => !videoPaths.Contains(path)).ToArray();
        var hasText = !string.IsNullOrWhiteSpace(text);
        var messages = new List<string>();
        var earlierPartsDone = false;
        var imageFirst = settings.PhraseImageSendMode != PhraseImageSendMode.TextThenImageThenSend;
        var leadingAfterMedia = toolbarImages && imageFirst && videoPaths.Any(IsImagePath);

        async Task<ReplyActionResult?> SendLeadingAsync()
        {
            if (!hasText && otherPaths.Length == 0)
            {
                return null;
            }

            var leading = hasText && otherPaths.Length > 0
                ? await ExecuteTextWithFileBackedImagesAsync(text, otherPaths, settings, mode, attemptContext)
                : hasText
                    ? await ExecuteTextOnlyAsync(text, settings, mode, attemptContext)
                    : await ExecuteFilesCoreAsync(otherPaths, settings, mode, attemptContext);
            if (!leading.Success)
            {
                return earlierPartsDone
                    ? CreateFailureResult(
                        ReplyActionFailureKind.ManualConfirmationRequired,
                        $"{string.Join("；", messages)}；文字没有发出：{leading.Message}",
                        "图片已经发出，请手动补上文字。本次不会自动重试。")
                    : leading;
            }

            messages.Add(leading.Message);
            earlierPartsDone = true;
            return null;
        }

        if (!leadingAfterMedia && await SendLeadingAsync() is { } leadingFailure)
        {
            return leadingFailure;
        }

        var sendAfterUpload = mode == ComposeActionMode.Send;
        for (var index = 0; index < videoPaths.Length; index++)
        {
            var videoPath = videoPaths[index];
            if (IsImagePath(videoPath) && !IsVideoPath(videoPath))
            {
                var imageResult = await UploadDouyinImageAsync(videoPath, settings, mode, attemptContext);
                if (!imageResult.Success)
                {
                    return earlierPartsDone
                        ? CreateFailureResult(
                            ReplyActionFailureKind.ManualConfirmationRequired,
                            $"{string.Join("；", messages)}；图片 {Path.GetFileName(videoPath)} 失败：{imageResult.Message}",
                            imageResult.UserGuidance)
                        : imageResult;
                }

                messages.Add(imageResult.Message);
                earlierPartsDone = true;
                continue;
            }

            var diagnostics = CreateComposeDiagnosticsPayload("files", mode);
            var stopwatch = Stopwatch.StartNew();
            PopulateAttemptDiagnostics(diagnostics, attemptContext);
            diagnostics["selectedSendPath"] = "douyin_video_toolbar_open_dialog";
            diagnostics["fileContainsVideo"] = true;
            diagnostics["douyinVideoIndex"] = index;
            diagnostics["douyinVideoCount"] = videoPaths.Length;

            var activation = await EnsureTargetWindowReadyAsync(settings, diagnostics, stopwatch, attemptContext);
            ReplyActionResult videoResult;
            if (!activation.Success)
            {
                videoResult = CreateTargetWindowUnavailableResult(activation.UserMessage);
            }
            else
            {
                var decision = _douyinVideoSendGuard.TryBegin(activation.TargetWindowHandle, videoPath);
                diagnostics["douyinVideoGuard"] = decision.ToString();
                if (decision == DouyinVideoSendGuard.GuardDecision.Duplicate)
                {
                    videoResult = CreateFailureResult(
                        ReplyActionFailureKind.ManualConfirmationRequired,
                        $"同一个视频 {DouyinVideoSendGuard.DefaultDuplicateWindow.TotalSeconds:0} 秒内刚提交过，已跳过以免重复发送：{Path.GetFileName(videoPath)}",
                        "如确实要再发一次，请稍等半分钟后再试。");
                }
                else if (decision == DouyinVideoSendGuard.GuardDecision.Busy)
                {
                    videoResult = CreateFailureResult(
                        ReplyActionFailureKind.ManualConfirmationRequired,
                        "上一个视频还在提交中，已跳过本次视频。",
                        "请等上一个视频提交完成后再发送。");
                }
                else
                {
                    var submitted = false;
                    try
                    {
                        videoResult = await _douyinVideoUploader.UploadAsync(
                            activation.TargetWindowHandle,
                            videoPath,
                            sendAfterUpload,
                            settings.SendHotkeyMode,
                            diagnostics).ConfigureAwait(false);
                        submitted = videoResult.Success && sendAfterUpload;
                    }
                    finally
                    {
                        _douyinVideoSendGuard.Complete(activation.TargetWindowHandle, videoPath, submitted);
                    }
                }
            }

            diagnostics["totalMs"] = stopwatch.ElapsedMilliseconds;
            diagnostics["result"] = videoResult.Success ? "success" : "failed";
            diagnostics["message"] = videoResult.Message;
            SearchOverlayDiagnostics.Log("message_compose", "compose_files_completed", diagnostics);

            if (!videoResult.Success)
            {
                if (!earlierPartsDone)
                {
                    return videoResult;
                }

                // Earlier text/images/videos are already out: never let the retry loop resend them.
                return CreateFailureResult(
                    ReplyActionFailureKind.ManualConfirmationRequired,
                    $"{string.Join("；", messages)}；第 {index + 1} 个视频失败：{videoResult.Message}",
                    videoResult.UserGuidance);
            }

            messages.Add(videoPaths.Length > 1 ? $"第 {index + 1} 个视频：{videoResult.Message}" : videoResult.Message);
            earlierPartsDone = true;
        }

        if (leadingAfterMedia && await SendLeadingAsync() is { } trailingFailure)
        {
            return trailingFailure;
        }

        return new ReplyActionResult(true, string.Join("；", messages));
    }

    /// <summary>Douyin images go through the toolbar 图片 button when sending (not when only inputting).</summary>
    internal bool UseDouyinToolbarForImages { get; set; } = true;

    private bool UsesDouyinToolbarForImages(ComposeActionMode mode, IEnumerable<string> paths)
    {
        return UseDouyinToolbarForImages && mode == ComposeActionMode.Send && paths.Any(IsImagePath);
    }

    private static bool IsImagePath(string path)
    {
        var payload = ComposeFilePayloadClassifier.Classify([path]);
        return payload.ContainsImage && !payload.ContainsVideo;
    }

    private async Task<ReplyActionResult> UploadDouyinImageAsync(
        string imagePath,
        AppSettings settings,
        ComposeActionMode mode,
        ComposeAttemptContext attemptContext)
    {
        var diagnostics = CreateComposeDiagnosticsPayload("files", mode);
        var stopwatch = Stopwatch.StartNew();
        PopulateAttemptDiagnostics(diagnostics, attemptContext);
        diagnostics["selectedSendPath"] = "douyin_image_toolbar_open_dialog";
        diagnostics["fileContainsImage"] = true;

        var activation = await EnsureTargetWindowReadyAsync(settings, diagnostics, stopwatch, attemptContext);
        var result = activation.Success
            ? await _douyinVideoUploader.UploadImageAsync(
                activation.TargetWindowHandle,
                imagePath,
                settings.SendHotkeyMode,
                diagnostics).ConfigureAwait(false)
            : CreateTargetWindowUnavailableResult(activation.UserMessage);

        diagnostics["totalMs"] = stopwatch.ElapsedMilliseconds;
        diagnostics["result"] = result.Success ? "success" : "failed";
        diagnostics["message"] = result.Message;
        SearchOverlayDiagnostics.Log("message_compose", "compose_files_completed", diagnostics);
        return result;
    }

    private async Task<ReplyActionResult> ExecuteTextOnlyAsync(
        string text,
        AppSettings settings,
        ComposeActionMode mode,
        ComposeAttemptContext attemptContext)
    {
        var diagnostics = CreateComposeDiagnosticsPayload("text_only", mode);
        var stopwatch = Stopwatch.StartNew();
        var normalizedText = NormalizeLineSeparators(text);
        PopulateAttemptDiagnostics(diagnostics, attemptContext);
        diagnostics["selectedSendPath"] = attemptContext.HostProfile.PreferFastTextPath
            ? "fast_text_paste_send"
            : "clipboard_text_paste_send";

        var activationResult = await EnsureTargetWindowReadyAsync(settings, diagnostics, stopwatch, attemptContext);
        if (!activationResult.Success)
        {
            diagnostics["result"] = activationResult.FailureCode;
            SearchOverlayDiagnostics.Log("message_compose", "compose_text_completed", diagnostics);
            return CreateTargetWindowUnavailableResult(activationResult.UserMessage);
        }

        diagnostics["activateWindowMs"] = stopwatch.ElapsedMilliseconds;
        diagnostics["effectiveRestoreFocusDelayMs"] = GetEffectiveTextOnlyRestoreFocusDelayMs(settings, attemptContext.HostProfile);

        var clipboardSnapshot = await CaptureClipboardSnapshotAsync(diagnostics, stopwatch);

        var textWrite = await TryWriteClipboardPayloadAsync(
            () => _clipboardService.SetTextAsync(normalizedText, persist: false),
            "text",
            diagnostics,
            stopwatch,
            ReplyActionFailureKind.ClipboardBusy,
            "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
        if (textWrite.FailureResult is not null)
        {
            diagnostics["result"] = "clipboard_write_failed";
            diagnostics["message"] = textWrite.FailureResult.Value.Message;
            SearchOverlayDiagnostics.Log("message_compose", "compose_text_completed", diagnostics);
            return textWrite.FailureResult.Value;
        }

        diagnostics["inputStrategy"] = "clipboard_paste";
        diagnostics["clipboardWriteMs"] = stopwatch.ElapsedMilliseconds;
        var clipboardPayloadFingerprint = textWrite.Fingerprint;
        var pasteFailure = await TryPasteClipboardPayloadAsync(
            activationResult.TargetWindowHandle,
            "text",
            attemptContext.HostProfile.TextPasteStableMs,
            GetEffectiveTextPasteDelayMs(settings, attemptContext.HostProfile),
            attemptContext.HostProfile.PollIntervalMs,
            diagnostics,
            stopwatch,
            settleMsDiagnosticKey: "effectiveTextSettleDelayMs");
        if (pasteFailure is not null)
        {
            await TryRestoreClipboardAsync(clipboardSnapshot, clipboardPayloadFingerprint);
            diagnostics["result"] = "send_hotkey_skipped_target_not_foreground";
            diagnostics["totalMs"] = stopwatch.ElapsedMilliseconds;
            SearchOverlayDiagnostics.Log("message_compose", "compose_text_completed", diagnostics);
            return pasteFailure.Value;
        }

        if (mode == ComposeActionMode.Send)
        {
            var sendFailure = await TrySendConfiguredHotkeyAsync(
                activationResult.TargetWindowHandle,
                settings.SendHotkeyMode,
                attemptContext.HostProfile.TextSendStableMs,
                GetEffectiveTextOnlySendDelayMs(settings, attemptContext.HostProfile),
                attemptContext.HostProfile.PollIntervalMs,
                diagnostics,
                stopwatch,
                delayMsDiagnosticKey: "effectiveSendDelayMs");
            if (sendFailure is not null)
            {
                await TryRestoreClipboardAsync(clipboardSnapshot, clipboardPayloadFingerprint);
                diagnostics["result"] = "send_hotkey_skipped_target_not_foreground";
                diagnostics["totalMs"] = stopwatch.ElapsedMilliseconds;
                SearchOverlayDiagnostics.Log("message_compose", "compose_text_completed", diagnostics);
                return sendFailure.Value;
            }
        }

        var effectiveClipboardRestoreDelayMs = GetEffectiveTextOnlyClipboardRestoreDelayMs(settings, attemptContext.HostProfile);
        var willRestoreClipboard = clipboardSnapshot.CanRestore && clipboardPayloadFingerprint is not null;
        if (willRestoreClipboard)
        {
            QueueClipboardRestore(clipboardSnapshot, effectiveClipboardRestoreDelayMs, clipboardPayloadFingerprint);
        }

        diagnostics["queuedClipboardRestoreDelayMs"] = willRestoreClipboard ? effectiveClipboardRestoreDelayMs : null;
        diagnostics["totalMs"] = stopwatch.ElapsedMilliseconds;
        diagnostics["result"] = "success";
        SearchOverlayDiagnostics.Log("message_compose", "compose_text_completed", diagnostics);
        return BuildTextResult(mode, clipboardSnapshot, restoreDeferred: willRestoreClipboard);
    }

    private async Task<ClipboardStepResult> PasteTextAsync(
        string text,
        AppSettings settings,
        IntPtr targetWindowHandle,
        Dictionary<string, object?> diagnostics,
        Stopwatch stopwatch,
        SendHostProfile hostProfile)
    {
        var writeResult = await TryWriteClipboardPayloadAsync(
            () => _clipboardService.SetTextAsync(NormalizeLineSeparators(text), persist: false),
            "text",
            diagnostics,
            stopwatch,
            ReplyActionFailureKind.ClipboardBusy,
            "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
        if (writeResult.FailureResult is not null)
        {
            return new ClipboardStepResult(writeResult.FailureResult.Value, null);
        }

        var clipboardPayloadFingerprint = writeResult.Fingerprint;
        var pasteFailure = await TryPasteClipboardPayloadAsync(
            targetWindowHandle,
            "text",
            hostProfile.TextPasteStableMs,
            GetEffectiveTextPasteDelayMs(settings, hostProfile),
            hostProfile.PollIntervalMs,
            diagnostics,
            stopwatch,
            settleMsDiagnosticKey: "effectiveTextSettleDelayMs");
        if (pasteFailure is not null)
        {
            return new ClipboardStepResult(pasteFailure.Value, clipboardPayloadFingerprint);
        }

        return new ClipboardStepResult(null, clipboardPayloadFingerprint);
    }

    private async Task<ClipboardSnapshot> CaptureClipboardSnapshotAsync(
        IDictionary<string, object?> diagnostics,
        Stopwatch stopwatch)
    {
        // A send that starts while the previous send's restore is still queued would back up the
        // previous phrase. Take over that restore instead: it still holds the user's own clipboard.
        var pending = TakePendingClipboardRestore();
        if (pending is not null)
        {
            var currentFingerprint = await _clipboardService.CaptureCurrentFingerprintAsync().ConfigureAwait(false);
            if (currentFingerprint is not null &&
                string.Equals(currentFingerprint.Value, pending.ExpectedFingerprint.Value, StringComparison.Ordinal))
            {
                diagnostics["snapshotReusedFromPendingRestore"] = true;
                diagnostics["snapshotMs"] = stopwatch.ElapsedMilliseconds;
                diagnostics["snapshotCaptured"] = pending.Snapshot.CaptureSucceeded;
                diagnostics["snapshotHasContent"] = pending.Snapshot.HasContent;
                diagnostics["snapshotCanRestore"] = pending.Snapshot.CanRestore;
                return pending.Snapshot;
            }
        }

        var snapshot = await _clipboardService.CaptureSnapshotAsync().ConfigureAwait(false);
        diagnostics["snapshotMs"] = stopwatch.ElapsedMilliseconds;
        diagnostics["snapshotCaptured"] = snapshot.CaptureSucceeded;
        diagnostics["snapshotHasContent"] = snapshot.HasContent;
        diagnostics["snapshotCanRestore"] = snapshot.CanRestore;
        return snapshot;
    }

    private async Task<ClipboardWriteStepResult> TryWriteClipboardPayloadAsync(
        Func<Task<ReplyActionResult>> writeAsync,
        string clipboardKind,
        IDictionary<string, object?> diagnostics,
        Stopwatch stopwatch,
        ReplyActionFailureKind defaultFailureKind,
        string guidance,
        string? lastImagePath = null)
    {
        var writeResult = AnnotateFailure(
            await writeAsync().ConfigureAwait(false),
            defaultFailureKind,
            guidance);
        diagnostics["lastClipboardWriteKind"] = clipboardKind;
        diagnostics["lastClipboardWriteDurationMs"] = stopwatch.ElapsedMilliseconds;
        if (!string.IsNullOrWhiteSpace(lastImagePath))
        {
            diagnostics["lastImagePath"] = lastImagePath;
        }

        if (!writeResult.Success)
        {
            return new ClipboardWriteStepResult(writeResult, null);
        }

        var fingerprint = await _clipboardService.CaptureCurrentFingerprintAsync().ConfigureAwait(false);
        diagnostics["lastClipboardFingerprintDurationMs"] = stopwatch.ElapsedMilliseconds;
        diagnostics["lastClipboardFingerprintCaptured"] = fingerprint is not null;
        return new ClipboardWriteStepResult(null, fingerprint);
    }

    private async Task<ReplyActionResult?> TryPasteClipboardPayloadAsync(
        IntPtr targetWindowHandle,
        string clipboardKind,
        int settleMs,
        int recoveryTimeoutMs,
        int pollIntervalMs,
        IDictionary<string, object?> diagnostics,
        Stopwatch stopwatch,
        string settleMsDiagnosticKey)
    {
        // Pre-paste: no settle; only wait (up to the recovery timeout) if focus is not already there.
        var stableForeground = await WaitForTargetForegroundFastAsync(
            targetWindowHandle,
            requiredStableMs: 0,
            recoveryTimeoutMs,
            pollIntervalMs).ConfigureAwait(false);
        diagnostics["pasteForegroundWaitMs"] = stableForeground.ElapsedMs;
        if (!stableForeground.Success)
        {
            return CreateFocusLostResult();
        }

        _keyboardInputSimulator.SendPasteShortcut();
        diagnostics["lastPasteKind"] = clipboardKind;
        diagnostics["lastPasteIssuedAtMs"] = stopwatch.ElapsedMilliseconds;
        diagnostics["pasteMs"] = stopwatch.ElapsedMilliseconds;

        // Post-paste: short fixed settle so the host can consume the clipboard, then a focus check.
        var settleResult = await SettleThenEnsureForegroundAsync(
            targetWindowHandle,
            settleMs,
            recoveryTimeoutMs,
            pollIntervalMs).ConfigureAwait(false);
        diagnostics[settleMsDiagnosticKey] = settleResult.ElapsedMs;
        diagnostics["pasteSettleMs"] = settleResult.ElapsedMs;
        if (!settleResult.Success)
        {
            return CreateFocusLostResult();
        }

        return null;
    }

    private async Task<ReplyActionResult?> TrySendConfiguredHotkeyAsync(
        IntPtr targetWindowHandle,
        SendHotkeyMode mode,
        int settleMs,
        int recoveryTimeoutMs,
        int pollIntervalMs,
        IDictionary<string, object?> diagnostics,
        Stopwatch stopwatch,
        string delayMsDiagnosticKey)
    {
        var stableForeground = await SettleThenEnsureForegroundAsync(
            targetWindowHandle,
            settleMs,
            recoveryTimeoutMs,
            pollIntervalMs).ConfigureAwait(false);
        diagnostics["sendForegroundWaitMs"] = stableForeground.ElapsedMs;
        diagnostics[delayMsDiagnosticKey] = stableForeground.ElapsedMs;
        if (!stableForeground.Success)
        {
            return CreateFocusLostResult();
        }

        SendConfiguredHotkey(mode);
        diagnostics["sendHotkeyMs"] = stopwatch.ElapsedMilliseconds;
        return null;
    }

    private static ReplyActionResult CreateFocusLostResult()
    {
        return CreateFailureResult(
            ReplyActionFailureKind.FocusLost,
            "目标输入窗口焦点已丢失，请先点一下聊天输入框，再重试。",
            "请先重新点击聊天输入框，再执行发送。");
    }

    private async Task<(bool Success, int ElapsedMs)> SettleThenEnsureForegroundAsync(
        IntPtr targetWindowHandle,
        int settleMs,
        int recoveryTimeoutMs,
        int pollIntervalMs)
    {
        var stopwatch = Stopwatch.StartNew();
        if (settleMs > 0)
        {
            await Task.Delay(settleMs).ConfigureAwait(false);
        }

        var result = await WaitForTargetForegroundFastAsync(
            targetWindowHandle,
            requiredStableMs: 0,
            recoveryTimeoutMs,
            pollIntervalMs).ConfigureAwait(false);
        return (result.Success, (int)stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Returns immediately when the target already owns the foreground. Only when it does not,
    /// falls back to the slower stability wait bounded by the recovery timeout.
    /// </summary>
    private async Task<(bool Success, int ElapsedMs)> WaitForTargetForegroundFastAsync(
        IntPtr targetWindowHandle,
        int requiredStableMs,
        int recoveryTimeoutMs,
        int pollIntervalMs)
    {
        if (IsTargetWindowForeground(targetWindowHandle) && requiredStableMs <= 0)
        {
            return (true, 0);
        }

        return await WaitForTargetForegroundStabilityAsync(
            targetWindowHandle,
            Math.Max(requiredStableMs, 0),
            recoveryTimeoutMs,
            pollIntervalMs).ConfigureAwait(false);
    }

    private void SendConfiguredHotkey(SendHotkeyMode mode)
    {
        switch (mode)
        {
            case SendHotkeyMode.Enter:
                _keyboardInputSimulator.SendEnter();
                break;
            case SendHotkeyMode.CtrlEnter:
                _keyboardInputSimulator.SendCtrlEnter();
                break;
            case SendHotkeyMode.AltS:
                _keyboardInputSimulator.SendAltS();
                break;
        }
    }

    private async Task<ReplyActionResult> TryRestoreClipboardAsync(
        ClipboardSnapshot snapshot,
        ClipboardFingerprint? expectedCurrentFingerprint = null)
    {
        if (!snapshot.CaptureSucceeded)
        {
            return CreateFailureResult(
                ReplyActionFailureKind.ClipboardBusy,
                "未能备份原剪贴板内容。",
                "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
        }

        if (!snapshot.CanRestore)
        {
            return new ReplyActionResult(true, "未能完整备份原剪贴板内容，已跳过恢复。");
        }

        return await AnnotateClipboardResultAsync(
            _clipboardService.RestoreSnapshotAsync(snapshot, expectedCurrentFingerprint),
            "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。");
    }

    private void QueueClipboardRestore(
        ClipboardSnapshot snapshot,
        int delayMs,
        ClipboardFingerprint? expectedCurrentFingerprint = null)
    {
        if (!snapshot.CaptureSucceeded || !snapshot.CanRestore || expectedCurrentFingerprint is null)
        {
            return;
        }

        var pending = new PendingClipboardRestore(snapshot, expectedCurrentFingerprint);
        lock (_pendingClipboardRestoreSync)
        {
            _pendingClipboardRestore = pending;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }

                lock (_pendingClipboardRestoreSync)
                {
                    // A newer send took this restore over; it will restore the same snapshot later.
                    if (!ReferenceEquals(_pendingClipboardRestore, pending))
                    {
                        return;
                    }

                    _pendingClipboardRestore = null;
                }

                await TryRestoreClipboardAsync(snapshot, expectedCurrentFingerprint).ConfigureAwait(false);
            }
            catch
            {
            }
        });
    }

    private PendingClipboardRestore? TakePendingClipboardRestore()
    {
        lock (_pendingClipboardRestoreSync)
        {
            var pending = _pendingClipboardRestore;
            _pendingClipboardRestore = null;
            return pending;
        }
    }

    private sealed record PendingClipboardRestore(ClipboardSnapshot Snapshot, ClipboardFingerprint ExpectedFingerprint);

    private static int GetEffectiveTextOnlyRestoreFocusDelayMs(AppSettings settings, SendHostProfile hostProfile)
    {
        return Math.Max(hostProfile.ActivationTimeoutMs, settings.RestoreFocusDelayMs);
    }

    private static int GetEffectiveTextOnlySendDelayMs(AppSettings settings, SendHostProfile hostProfile)
    {
        return Math.Max(hostProfile.TextSendTimeoutMs, settings.SendDelayMs);
    }

    private static int GetEffectiveTextOnlyClipboardRestoreDelayMs(AppSettings settings, SendHostProfile hostProfile)
    {
        return Math.Max(hostProfile.ClipboardRestoreDelayMs, settings.ClipboardRestoreDelayMs);
    }

    private static int GetEffectiveRichRestoreFocusDelayMs(AppSettings settings, SendHostProfile hostProfile)
    {
        return Math.Max(hostProfile.ActivationTimeoutMs, settings.RestoreFocusDelayMs);
    }

    private static int GetEffectiveRichSendDelayMs(AppSettings settings, SendHostProfile hostProfile, bool hasImages)
    {
        var fallback = hasImages ? settings.RichImageSendDelayMs : settings.SendDelayMs;
        return Math.Max(hostProfile.RichSendTimeoutMs, fallback);
    }

    private static int GetEffectiveRichImageClipboardRestoreDelayMs(AppSettings settings, SendHostProfile hostProfile)
    {
        return Math.Max(hostProfile.ClipboardRestoreDelayMs, settings.RichImageClipboardRestoreDelayMs);
    }

    private static int GetEffectiveTextPasteDelayMs(AppSettings settings, SendHostProfile hostProfile)
    {
        return Math.Max(hostProfile.TextPasteTimeoutMs, settings.TextPasteDelayMs);
    }

    private static int GetEffectiveImagePasteDelayMs(AppSettings settings, SendHostProfile hostProfile)
    {
        return Math.Max(hostProfile.FilePasteTimeoutMs, settings.ImagePasteDelayMs);
    }

    private static string NormalizeLineSeparators(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u2028', '\n')
            .Replace('\u2029', '\n');
    }

    private static Dictionary<string, object?> CreateComposeDiagnosticsPayload(string composeKind, ComposeActionMode mode)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["composeKind"] = composeKind,
            ["actionMode"] = mode.ToString()
        };
    }

    private async Task<(bool Success, IntPtr TargetWindowHandle, string FailureCode, string UserMessage)> EnsureTargetWindowReadyAsync(
        AppSettings settings,
        Dictionary<string, object?> diagnostics,
        Stopwatch stopwatch,
        ComposeAttemptContext attemptContext)
    {
        var targetWindowHandle = NormalizeTargetWindowHandle(
            attemptContext.TargetWindowInfo?.Handle ?? _externalWorkbenchService.GetLastExternalWindowHandle());
        diagnostics["targetWindowHandle"] = SearchOverlayDiagnostics.FormatHandle(targetWindowHandle);

        if (targetWindowHandle == IntPtr.Zero || !_windowFocusService.IsWindow(targetWindowHandle))
        {
            return (false, IntPtr.Zero, "target_window_missing", "未找到目标输入窗口。请先点击聊天输入框，再回到工具执行操作。");
        }

        var activationAttempts = attemptContext.HostProfile.Kind == SendHostKind.DouyinWorkbench
            ? 1
            : TargetActivationAttemptCount;
        diagnostics["activationAttemptBudget"] = activationAttempts;
        for (var attempt = 1; attempt <= activationAttempts; attempt++)
        {
            var refreshedTargetWindowInfo = ResolveLatestTargetWindowInfo(attemptContext.TargetWindowInfo);
            var refreshedTargetWindowHandle = NormalizeTargetWindowHandle(
                refreshedTargetWindowInfo?.Handle ?? _externalWorkbenchService.GetLastExternalWindowHandle());
            diagnostics["targetRefreshAttempt"] = attempt;
            diagnostics["targetWindowHandleRefreshed"] = SearchOverlayDiagnostics.FormatHandle(refreshedTargetWindowHandle);

            if (attemptContext.AttemptIndex > 1 &&
                targetWindowHandle != IntPtr.Zero &&
                refreshedTargetWindowHandle != IntPtr.Zero &&
                refreshedTargetWindowHandle != targetWindowHandle)
            {
                diagnostics["targetWindowChangedBeforeRetry"] = true;
                return (false, refreshedTargetWindowHandle, "target_window_changed", "检测到目标输入窗口已变化，已取消自动补救。请重新回到目标聊天窗口后再试。");
            }

            if (refreshedTargetWindowHandle != IntPtr.Zero)
            {
                targetWindowHandle = refreshedTargetWindowHandle;
                diagnostics["targetWindowHandle"] = SearchOverlayDiagnostics.FormatHandle(targetWindowHandle);
            }

            if (IsTargetWindowForeground(targetWindowHandle))
            {
                diagnostics["windowActivated"] = true;
                diagnostics["activationAttempt"] = attempt;
                diagnostics["activationSkippedAlreadyForeground"] = true;
                diagnostics["activationWaitMs"] = 0;
                return (true, targetWindowHandle, string.Empty, string.Empty);
            }

            var activated = _externalWorkbenchService.TryActivateLastExternalWindow();
            diagnostics["windowActivated"] = activated;
            diagnostics["activationAttempt"] = attempt;

            if (!activated)
            {
                continue;
            }

            var activationWaitResult = await WaitForTargetForegroundFastAsync(
                targetWindowHandle,
                attemptContext.HostProfile.ActivationStableMs,
                attempt == 1
                    ? GetEffectiveRichRestoreFocusDelayMs(settings, attemptContext.HostProfile)
                    : Math.Max(attemptContext.HostProfile.ActivationTimeoutMs, settings.ActivationRetryDelayMs),
                attemptContext.HostProfile.PollIntervalMs).ConfigureAwait(false);
            diagnostics["activationWaitMs"] = activationWaitResult.ElapsedMs;
            var foregroundWindowHandle = _windowFocusService.NormalizeTopLevelWindow(_windowFocusService.GetForegroundWindow());
            diagnostics["foregroundAfterActivation"] = SearchOverlayDiagnostics.FormatHandle(foregroundWindowHandle);
            diagnostics["foregroundCheckAtMs"] = stopwatch.ElapsedMilliseconds;

            if (activationWaitResult.Success && foregroundWindowHandle == targetWindowHandle)
            {
                return (true, targetWindowHandle, string.Empty, string.Empty);
            }
        }

        return (false, targetWindowHandle, "target_window_not_foreground", "未能稳定切回目标输入窗口。请先点一下聊天输入框，再重试。");
    }

    private static ReplyActionResult CreateTargetWindowUnavailableResult(string message)
    {
        return CreateFailureResult(InferFailureKind(message), message, InferFailureGuidance(message));
    }

    private static int GetEffectiveClipboardRestoreDelayMs(AppSettings settings, SendHostProfile hostProfile)
    {
        return Math.Max(hostProfile.ClipboardRestoreDelayMs, settings.ClipboardRestoreDelayMs);
    }

    private async Task<(bool Success, int ElapsedMs)> WaitForTargetForegroundStabilityAsync(
        IntPtr targetWindowHandle,
        int requiredStableMs,
        int timeoutMs,
        int pollIntervalMs)
    {
        requiredStableMs = Math.Max(0, requiredStableMs);
        timeoutMs = Math.Max(requiredStableMs, timeoutMs);
        pollIntervalMs = Math.Clamp(pollIntervalMs, 10, 50);

        var stopwatch = Stopwatch.StartNew();
        long? stableSinceMs = IsTargetWindowForeground(targetWindowHandle) ? 0 : null;

        while (stopwatch.ElapsedMilliseconds <= timeoutMs)
        {
            if (IsTargetWindowForeground(targetWindowHandle))
            {
                stableSinceMs ??= stopwatch.ElapsedMilliseconds;
                if (stopwatch.ElapsedMilliseconds - stableSinceMs.Value >= requiredStableMs)
                {
                    return (true, (int)stopwatch.ElapsedMilliseconds);
                }
            }
            else
            {
                stableSinceMs = null;
            }

            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
        }

        return (false, (int)stopwatch.ElapsedMilliseconds);
    }

    private ComposeAttemptContext CreateAttemptContext(
        string composeKind,
        AppSettings settings,
        int attemptIndex,
        string attemptReason,
        WindowDockTargetInfo? preferredTargetWindowInfo = null)
    {
        var targetWindowInfo = ResolveLatestTargetWindowInfo(preferredTargetWindowInfo);

        var hostProfile = SendHostProfileResolver.Resolve(targetWindowInfo);
        return new ComposeAttemptContext(
            composeKind,
            attemptIndex,
            attemptReason,
            hostProfile,
            targetWindowInfo,
            settings);
    }

    private static void PopulateAttemptDiagnostics(
        IDictionary<string, object?> diagnostics,
        ComposeAttemptContext attemptContext)
    {
        diagnostics["attemptIndex"] = attemptContext.AttemptIndex;
        diagnostics["attemptReason"] = attemptContext.AttemptReason;
        diagnostics["hostProfile"] = attemptContext.HostProfile.Name;
        if (attemptContext.HostProfile.Kind == SendHostKind.DouyinWorkbench)
        {
            diagnostics["processElevated"] = IsProcessElevated();
            diagnostics["douyinAutomaticRetry"] = false;
        }

        if (attemptContext.TargetWindowInfo is not null)
        {
            diagnostics["targetProcessName"] = attemptContext.TargetWindowInfo.ProcessName;
            diagnostics["targetWindowClassName"] = attemptContext.TargetWindowInfo.WindowClassName;
            diagnostics["targetWindowTitle"] = attemptContext.TargetWindowInfo.Title;
        }
    }

    private ReplyActionResult CompleteComposeOperation(
        ComposeAttemptContext attemptContext,
        ComposeActionMode mode,
        ReplyActionResult result,
        ComposeOperationTelemetry telemetry)
    {
        var completedResult = result with
        {
            ComposeTelemetry = telemetry
        };

        LogComposeOperationCompleted(attemptContext, mode, completedResult);
        return completedResult;
    }

    private void LogComposeOperationCompleted(
        ComposeAttemptContext attemptContext,
        ComposeActionMode mode,
        ReplyActionResult result)
    {
        var payload = CreateComposeDiagnosticsPayload(attemptContext.ComposeKind, mode);
        PopulateAttemptDiagnostics(payload, attemptContext);
        var composeTelemetry = result.ComposeTelemetry;
        payload["finalSuccess"] = result.Success;
        payload["finalMessage"] = result.Message;
        payload["attemptCount"] = composeTelemetry?.AttemptCount ?? attemptContext.AttemptIndex;
        payload["retryTriggered"] = composeTelemetry?.RetryTriggered ?? false;
        payload["retryReason"] = composeTelemetry?.RetryReason ?? string.Empty;
        payload["retryOutcome"] = composeTelemetry?.RetryOutcome ?? "unknown";
        payload["failureCategory"] = composeTelemetry?.FailureCategory ?? (result.Success ? "none" : MapFailureCategory(result));
        payload["targetChangedBeforeRetry"] = composeTelemetry?.TargetChangedBeforeRetry ?? false;
        SearchOverlayDiagnostics.Log("message_compose", "compose_operation_completed", payload);
    }

    private static bool TryBuildRetryDecision(ReplyActionResult result, out ComposeRetryDecision retryDecision)
    {
        var failureKind = ResolveFailureKind(result);
        var failureCategory = result.Success ? "none" : MapFailureCategory(result);
        switch (failureKind)
        {
            case ReplyActionFailureKind.FocusLost:
                retryDecision = new ComposeRetryDecision("focus_lost", failureCategory);
                return true;
            case ReplyActionFailureKind.ActivationFailed:
                retryDecision = new ComposeRetryDecision("activation", failureCategory);
                return true;
            case ReplyActionFailureKind.ClipboardBusy:
                retryDecision = new ComposeRetryDecision("clipboard", failureCategory);
                return true;
            default:
                retryDecision = new ComposeRetryDecision(string.Empty, failureCategory);
                return false;
        }
    }

    private static string MapFailureCategory(ReplyActionResult result)
    {
        return ResolveFailureKind(result) switch
        {
            ReplyActionFailureKind.None => "none",
            ReplyActionFailureKind.EmptyContent => "empty",
            ReplyActionFailureKind.MissingInput => "missing_input",
            ReplyActionFailureKind.MissingTargetWindow => "missing_target",
            ReplyActionFailureKind.ActivationFailed => "activation",
            ReplyActionFailureKind.FocusLost => "focus_lost",
            ReplyActionFailureKind.ClipboardBusy => "clipboard",
            ReplyActionFailureKind.TargetChanged => "target_changed",
            ReplyActionFailureKind.ManualConfirmationRequired => "manual_confirm",
            ReplyActionFailureKind.ExternalTool => "external_tool",
            _ => "other"
        };
    }

    private bool IsTargetWindowForeground(IntPtr targetWindowHandle)
    {
        return targetWindowHandle != IntPtr.Zero &&
               NormalizeTargetWindowHandle(_windowFocusService.GetForegroundWindow()) ==
               NormalizeTargetWindowHandle(targetWindowHandle);
    }

    private static string[] NormalizePaths(IEnumerable<string> filePaths)
    {
        return filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ReplyActionResult BuildTextResult(
        ComposeActionMode mode,
        ClipboardSnapshot clipboardSnapshot,
        bool restoreDeferred = false,
        ReplyActionResult? restoreResult = null)
    {
        var actionText = mode == ComposeActionMode.Send
            ? "已粘贴并发送"
            : "已输入到当前窗口";

        if (!clipboardSnapshot.CaptureSucceeded)
        {
            return new ReplyActionResult(true, $"{actionText}，但未能备份原剪贴板，当前剪贴板内容已被本次短语覆盖。");
        }

        if (!clipboardSnapshot.CanRestore)
        {
            return new ReplyActionResult(true, $"{actionText}，但未能完整备份原剪贴板，当前剪贴板可能仍保留本次内容。");
        }

        if (restoreDeferred)
        {
            return new ReplyActionResult(true, $"{actionText}，剪贴板将在后台自动恢复。");
        }

        var completedRestoreResult = restoreResult ?? new ReplyActionResult(false, "未执行剪贴板恢复。");
        return completedRestoreResult.Success
            ? new ReplyActionResult(true, $"{actionText}，并恢复原剪贴板。")
            : new ReplyActionResult(true, $"{actionText}，但{completedRestoreResult.Message}");
    }

    private static ReplyActionResult BuildDirectTextResult(ComposeActionMode mode)
    {
        return mode == ComposeActionMode.Send
            ? new ReplyActionResult(true, "已直接输入并发送。")
            : new ReplyActionResult(true, "已直接输入到当前窗口。");
    }

    private static ReplyActionResult BuildFileResult(
        ComposeActionMode mode,
        int fileCount,
        ClipboardSnapshot clipboardSnapshot,
        bool restoreDeferred = false,
        ReplyActionResult? restoreResult = null)
    {
        var actionText = mode == ComposeActionMode.Send
            ? $"已粘贴并发送 {fileCount} 个文件"
            : $"已将 {fileCount} 个文件粘贴到当前窗口";

        if (!clipboardSnapshot.CaptureSucceeded)
        {
            return new ReplyActionResult(true, $"{actionText}，但未能备份原剪贴板，当前剪贴板内容已被本次文件覆盖。");
        }

        if (!clipboardSnapshot.CanRestore)
        {
            return new ReplyActionResult(true, $"{actionText}，但未能完整备份原剪贴板，当前剪贴板可能仍保留本次内容。");
        }

        if (restoreDeferred)
        {
            return new ReplyActionResult(true, $"{actionText}，剪贴板将在后台自动恢复。");
        }

        var completedRestoreResult = restoreResult ?? new ReplyActionResult(false, "未执行剪贴板恢复。");
        return completedRestoreResult.Success
            ? new ReplyActionResult(true, $"{actionText}，并恢复原剪贴板。")
            : new ReplyActionResult(true, $"{actionText}，但{completedRestoreResult.Message}");
    }

    private static ReplyActionResult BuildRichTextResult(
        ComposeActionMode mode,
        bool hasText,
        int imageCount,
        ClipboardSnapshot clipboardSnapshot,
        bool restoreDeferred = false,
        ReplyActionResult? restoreResult = null)
    {
        var payloadText = hasText
            ? imageCount > 1 ? $"图文短语（含 {imageCount} 张图片）" : "图文短语"
            : imageCount > 1 ? $"图片（共 {imageCount} 张）" : "图片";
        var actionText = mode == ComposeActionMode.Send
            ? $"已发送{payloadText}"
            : $"已输入{payloadText}";

        if (!clipboardSnapshot.CaptureSucceeded)
        {
            return new ReplyActionResult(true, $"{actionText}，但未能备份原剪贴板，当前剪贴板内容已被覆盖。");
        }

        if (!clipboardSnapshot.CanRestore)
        {
            return new ReplyActionResult(true, $"{actionText}，但未能完整备份原剪贴板，当前剪贴板可能仍保留本次内容。");
        }

        if (restoreDeferred)
        {
            return new ReplyActionResult(true, $"{actionText}，剪贴板将在后台自动恢复。");
        }

        var completedRestoreResult = restoreResult ?? new ReplyActionResult(false, "未执行剪贴板恢复。");
        return completedRestoreResult.Success
            ? new ReplyActionResult(true, $"{actionText}，并恢复原剪贴板。")
            : new ReplyActionResult(true, $"{actionText}，但{completedRestoreResult.Message}");
    }

    private sealed record ComposeAttemptContext(
        string ComposeKind,
        int AttemptIndex,
        string AttemptReason,
        SendHostProfile HostProfile,
        WindowDockTargetInfo? TargetWindowInfo,
        AppSettings Settings);

    private readonly record struct ComposeRetryDecision(
        string RetryReason,
        string FailureCategory);

    private readonly record struct RetryAttemptPreparation(
        ComposeAttemptContext? AttemptContext,
        ReplyActionResult? BlockedResult,
        bool TargetChangedBeforeRetry);

    private readonly record struct ClipboardStepResult(
        ReplyActionResult? FailureResult,
        ClipboardFingerprint? Fingerprint);

    private readonly record struct ClipboardWriteStepResult(
        ReplyActionResult? FailureResult,
        ClipboardFingerprint? Fingerprint);

    private RetryAttemptPreparation PrepareRetryAttemptContext(
        ComposeAttemptContext firstAttemptContext,
        AppSettings settings,
        ComposeRetryDecision retryDecision)
    {
        var refreshedTargetWindowInfo = ResolveLatestTargetWindowInfo(firstAttemptContext.TargetWindowInfo);
        var initialTargetWindowHandle = NormalizeTargetWindowHandle(firstAttemptContext.TargetWindowInfo?.Handle ?? IntPtr.Zero);
        var refreshedTargetWindowHandle = NormalizeTargetWindowHandle(
            refreshedTargetWindowInfo?.Handle ?? _externalWorkbenchService.GetLastExternalWindowHandle());
        var targetChangedBeforeRetry = initialTargetWindowHandle != IntPtr.Zero &&
                                       refreshedTargetWindowHandle != IntPtr.Zero &&
                                       initialTargetWindowHandle != refreshedTargetWindowHandle;
        if (targetChangedBeforeRetry)
        {
            return new RetryAttemptPreparation(
                null,
                CreateFailureResult(
                    ReplyActionFailureKind.TargetChanged,
                    "检测到目标输入窗口已变化，已取消自动补救。请重新回到目标聊天窗口后再试。",
                    "请重新切回目标聊天输入框后再执行发送。"),
                true);
        }

        return new RetryAttemptPreparation(
            CreateAttemptContext(
                firstAttemptContext.ComposeKind,
                settings,
                attemptIndex: 2,
                attemptReason: retryDecision.RetryReason,
                preferredTargetWindowInfo: refreshedTargetWindowInfo),
            null,
            false);
    }

    private WindowDockTargetInfo? ResolveLatestTargetWindowInfo(WindowDockTargetInfo? fallbackWindowInfo)
    {
        if (_externalWorkbenchService.TryGetLastExternalWindowInfo(out var latestWindowInfo) &&
            latestWindowInfo is not null)
        {
            return latestWindowInfo;
        }

        var lastHandle = _externalWorkbenchService.GetLastExternalWindowHandle();
        if (lastHandle != IntPtr.Zero &&
            _externalWorkbenchService.TryGetWindowInfo(lastHandle, out var windowInfoByHandle) &&
            windowInfoByHandle is not null)
        {
            return windowInfoByHandle;
        }

        return fallbackWindowInfo;
    }

    private IntPtr NormalizeTargetWindowHandle(IntPtr windowHandle)
    {
        return _windowFocusService.NormalizeTopLevelWindow(windowHandle);
    }

    private static ReplyActionResult CreateFailureResult(
        ReplyActionFailureKind failureKind,
        string message,
        string guidance = "")
    {
        return new ReplyActionResult(false, message, FailureKind: failureKind, UserGuidance: guidance);
    }

    private static ReplyActionResult AnnotateFailure(
        ReplyActionResult result,
        ReplyActionFailureKind failureKind,
        string guidance = "")
    {
        if (result.Success)
        {
            return result;
        }

        return result with
        {
            FailureKind = result.FailureKind == ReplyActionFailureKind.None ? failureKind : result.FailureKind,
            UserGuidance = string.IsNullOrWhiteSpace(result.UserGuidance) ? guidance : result.UserGuidance
        };
    }

    private static async Task<ReplyActionResult> AnnotateClipboardResultAsync(
        Task<ReplyActionResult> resultTask,
        string clipboardGuidance)
    {
        var result = await resultTask.ConfigureAwait(false);
        return AnnotateFailure(result, ResolveFailureKind(result, ReplyActionFailureKind.ClipboardBusy), clipboardGuidance);
    }

    private static ReplyActionFailureKind ResolveFailureKind(
        ReplyActionResult result,
        ReplyActionFailureKind fallback = ReplyActionFailureKind.Unexpected)
    {
        if (result.Success)
        {
            return ReplyActionFailureKind.None;
        }

        return result.FailureKind != ReplyActionFailureKind.None
            ? result.FailureKind
            : InferFailureKind(result.Message, fallback);
    }

    private static ReplyActionFailureKind InferFailureKind(
        string message,
        ReplyActionFailureKind fallback = ReplyActionFailureKind.Unexpected)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return fallback;
        }

        if (message.Contains("未找到目标输入窗口", StringComparison.Ordinal))
        {
            return ReplyActionFailureKind.MissingTargetWindow;
        }

        if (message.Contains("未能稳定切回目标输入窗口", StringComparison.Ordinal))
        {
            return ReplyActionFailureKind.ActivationFailed;
        }

        if (message.Contains("目标输入窗口已变化", StringComparison.Ordinal))
        {
            return ReplyActionFailureKind.TargetChanged;
        }

        if (message.Contains("焦点已丢失", StringComparison.Ordinal) ||
            message.Contains("先点一下聊天输入框", StringComparison.Ordinal))
        {
            return ReplyActionFailureKind.FocusLost;
        }

        if (message.Contains("OpenClipboard", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("CLIPBRD", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("占用", StringComparison.Ordinal) ||
            message.Contains("Insufficient memory", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("超时", StringComparison.Ordinal))
        {
            return ReplyActionFailureKind.ClipboardBusy;
        }

        if (message.Contains("文件不存在", StringComparison.Ordinal) ||
            message.Contains("未找到图片文件", StringComparison.Ordinal))
        {
            return ReplyActionFailureKind.MissingInput;
        }

        return fallback;
    }

    private static string InferFailureGuidance(string message)
    {
        return InferFailureKind(message) switch
        {
            ReplyActionFailureKind.MissingTargetWindow => "请先点击目标聊天输入框，再回到工具执行发送。",
            ReplyActionFailureKind.ActivationFailed => "请先切回目标聊天输入框后重试。",
            ReplyActionFailureKind.FocusLost => "请重新点击聊天输入框后重试。",
            ReplyActionFailureKind.ClipboardBusy => "请稍等片刻后重试，必要时先关闭正在占用剪贴板的程序。",
            ReplyActionFailureKind.MissingInput => "请确认图片或文件仍存在，或重新选择后再试。",
            ReplyActionFailureKind.TargetChanged => "请重新确认当前聊天窗口后再重试。",
            _ => string.Empty
        };
    }

    /// <summary>
    /// True when the user has moved to a different app. Our own windows and an empty
    /// foreground do not count: those happen while the overlay hides and must not abort the send.
    /// </summary>
    private bool DidForegroundLeaveTarget(
        IntPtr targetWindowHandle,
        IDictionary<string, object?> diagnostics,
        string stage)
    {
        var foreground = NormalizeTargetWindowHandle(_windowFocusService.GetForegroundWindow());
        var target = NormalizeTargetWindowHandle(targetWindowHandle);
        diagnostics["douyinForegroundAt_" + stage] = SearchOverlayDiagnostics.FormatHandle(foreground);
        if (foreground == IntPtr.Zero || foreground == target)
        {
            return false;
        }

        uint processId;
        try
        {
            NativeMethods.GetWindowThreadProcessId(foreground, out processId);
        }
        catch (Exception ex)
        {
            diagnostics["douyinForegroundProcessQueryError"] = ex.GetType().Name;
            return true;
        }

        diagnostics["douyinForegroundProcessIdAt_" + stage] = processId;
        // Only our own overlay/main window is ignored. An unknown or other-process
        // foreground means the user left the chat; do not pull focus back.
        return processId != (uint)Environment.ProcessId;
    }

    private static void LogDouyinSendStopped(
        ComposeAttemptContext attemptContext,
        ReplyActionResult result,
        string failureCategory)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["step"] = InferDouyinFailureStep(result),
            ["failureKind"] = ResolveFailureKind(result).ToString(),
            ["failureCategory"] = failureCategory,
            ["message"] = result.Message,
            ["userGuidance"] = result.UserGuidance,
            ["composeKind"] = attemptContext.ComposeKind,
            ["hostProfile"] = attemptContext.HostProfile.Name,
            ["attemptIndex"] = attemptContext.AttemptIndex,
            ["automaticRetry"] = false,
            ["processElevated"] = IsProcessElevated(),
            ["targetWindowTitle"] = attemptContext.TargetWindowInfo?.Title ?? string.Empty,
            ["targetProcessName"] = attemptContext.TargetWindowInfo?.ProcessName ?? string.Empty
        };
        SearchOverlayDiagnostics.Log("message_compose", "douyin_send_stopped", payload);
    }

    private static string InferDouyinFailureStep(ReplyActionResult result)
    {
        var message = result.Message ?? string.Empty;
        if (message.Contains("视频", StringComparison.Ordinal))
        {
            return "video_upload";
        }

        if (message.Contains("图片", StringComparison.Ordinal))
        {
            return "image_paste";
        }

        if (message.Contains("焦点", StringComparison.Ordinal))
        {
            return "focus";
        }

        if (message.Contains("没有真正发出", StringComparison.Ordinal) ||
            message.Contains("无法确认", StringComparison.Ordinal))
        {
            return "send_verify";
        }

        var kind = ResolveFailureKind(result);
        return kind switch
        {
            ReplyActionFailureKind.FocusLost => "focus",
            ReplyActionFailureKind.ActivationFailed or ReplyActionFailureKind.MissingTargetWindow => "activate_window",
            ReplyActionFailureKind.ClipboardBusy => "clipboard",
            ReplyActionFailureKind.TargetChanged => "target_changed",
            _ => "send"
        };
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
