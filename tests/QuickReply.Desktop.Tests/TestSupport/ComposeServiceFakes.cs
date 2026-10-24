using System.Windows;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests.TestSupport;

internal sealed class FakeExternalWorkbenchService : IExternalWorkbenchService
{
    public bool ActivateResult { get; set; } = true;

    public Func<bool>? OnTryActivateLastExternalWindow { get; set; }

    public Func<WindowDockTargetInfo?>? OnGetLastExternalWindowInfo { get; set; }

    public Func<IntPtr, WindowDockTargetInfo?>? OnGetWindowInfo { get; set; }

    public IntPtr LastHandle { get; set; } = IntPtr.Zero;

    public WindowDockTargetInfo? LastWindowInfo { get; set; }

    public void Start(Window window)
    {
    }

    public void Stop()
    {
    }

    public bool TryActivateLastExternalWindow()
    {
        return OnTryActivateLastExternalWindow?.Invoke() ?? ActivateResult;
    }

    public bool TryGetLastExternalWindowInfo(out WindowDockTargetInfo? windowInfo)
    {
        windowInfo = OnGetLastExternalWindowInfo?.Invoke() ?? LastWindowInfo;
        return windowInfo is not null;
    }

    public bool TryGetWindowInfo(IntPtr windowHandle, out WindowDockTargetInfo? windowInfo)
    {
        windowInfo = OnGetWindowInfo?.Invoke(windowHandle);
        if (windowInfo is null)
        {
            windowInfo = LastWindowInfo is not null && LastWindowInfo.Handle == windowHandle
                ? LastWindowInfo
                : null;
        }

        return windowInfo is not null;
    }

    public IntPtr GetLastExternalWindowHandle()
    {
        return LastHandle != IntPtr.Zero ? LastHandle : LastWindowInfo?.Handle ?? IntPtr.Zero;
    }

    public void RememberExternalWindow(IntPtr windowHandle)
    {
        LastHandle = windowHandle;
    }
}

internal sealed class FakeClipboardService : IClipboardService
{
    public ClipboardSnapshot SnapshotToReturn { get; set; } = new(captureSucceeded: false, hasContent: false, dataObject: null, fingerprint: null);

    public ClipboardFingerprint? CurrentFingerprint { get; set; } = new("fingerprint");

    public ReplyActionResult SetTextResult { get; set; } = new(true, "ok");

    public ReplyActionResult SetImageResult { get; set; } = new(true, "ok");

    public ReplyActionResult SetTextAndFilesResult { get; set; } = new(true, "ok");

    public ReplyActionResult SetFileDropListResult { get; set; } = new(true, "ok");

    public ReplyActionResult RestoreResult { get; set; } = new(true, "ok");

    public Func<string, bool, ReplyActionResult>? OnSetText { get; set; }

    public Func<IReadOnlyCollection<string>, bool, ReplyActionResult>? OnSetFileDropList { get; set; }

    public string? LastText { get; private set; }

    public IReadOnlyCollection<string> LastFiles { get; private set; } = [];

    public int SetTextCallCount { get; private set; }

    public int SetFileDropListCallCount { get; private set; }

    public int SetTextAndFilesCallCount { get; private set; }

    public int RestoreCallCount { get; private set; }

    public Task<ClipboardSnapshot> CaptureSnapshotAsync()
    {
        return Task.FromResult(SnapshotToReturn);
    }

    public Task<ClipboardFingerprint?> CaptureCurrentFingerprintAsync()
    {
        return Task.FromResult(CurrentFingerprint);
    }

    public Task<ReplyActionResult> SetTextAsync(string text, bool persist = true)
    {
        SetTextCallCount++;
        LastText = text;
        return Task.FromResult(OnSetText?.Invoke(text, persist) ?? SetTextResult);
    }

    public Task<ReplyActionResult> SetImageAsync(string imagePath, bool persist = true)
    {
        LastFiles = [imagePath];
        return Task.FromResult(SetImageResult);
    }

    public Task<ReplyActionResult> SetTextAndFilesAsync(string text, IReadOnlyCollection<string> filePaths, bool persist = true)
    {
        SetTextAndFilesCallCount++;
        LastText = text;
        LastFiles = filePaths.ToArray();
        return Task.FromResult(SetTextAndFilesResult);
    }

    public Task<ReplyActionResult> SetFileDropListAsync(IReadOnlyCollection<string> filePaths, bool persist = true)
    {
        SetFileDropListCallCount++;
        LastFiles = filePaths.ToArray();
        return Task.FromResult(OnSetFileDropList?.Invoke(filePaths, persist) ?? SetFileDropListResult);
    }

    public Task<ReplyActionResult> RestoreSnapshotAsync(ClipboardSnapshot snapshot, ClipboardFingerprint? expectedCurrentFingerprint = null)
    {
        RestoreCallCount++;
        return Task.FromResult(RestoreResult);
    }
}

internal sealed class FakeKeyboardInputSimulator : IKeyboardInputSimulator
{
    public Action? OnPasteShortcut { get; set; }

    public Action? OnSendKey { get; set; }

    public int PasteShortcutCount { get; private set; }

    public int EnterCount { get; private set; }

    public int CtrlEnterCount { get; private set; }

    public int AltSCount { get; private set; }

    public int CtrlACount { get; private set; }

    public int CtrlCCount { get; private set; }

    public int EscapeCount { get; private set; }

    public void SendPasteShortcut()
    {
        PasteShortcutCount++;
        OnPasteShortcut?.Invoke();
    }

    public void SendEnter()
    {
        EnterCount++;
        OnSendKey?.Invoke();
    }

    public void SendCtrlEnter()
    {
        CtrlEnterCount++;
        OnSendKey?.Invoke();
    }

    public void SendAltS()
    {
        AltSCount++;
        OnSendKey?.Invoke();
    }

    public void SendCtrlA()
    {
        CtrlACount++;
    }

    public void SendCtrlC()
    {
        CtrlCCount++;
    }

    public void SendEscape()
    {
        EscapeCount++;
    }
}

internal sealed class FakeWindowFocusService : IWindowFocusService
{
    public IntPtr ForegroundWindow { get; set; } = IntPtr.Zero;

    public HashSet<IntPtr> ValidHandles { get; } = [];

    public bool IsWindow(IntPtr windowHandle)
    {
        return ValidHandles.Contains(windowHandle);
    }

    public IntPtr GetForegroundWindow()
    {
        return ForegroundWindow;
    }

    public IntPtr NormalizeTopLevelWindow(IntPtr windowHandle)
    {
        return windowHandle;
    }
}

internal static class TestSettingsFactory
{
    public static AppSettings CreateFastSettings()
    {
        return new AppSettings
        {
            RestoreFocusDelayMs = 50,
            ActivationRetryDelayMs = 50,
            SendDelayMs = 50,
            ClipboardRestoreDelayMs = 50,
            TextPasteDelayMs = 50,
            ImagePasteDelayMs = 50,
            RichImageSendDelayMs = 50,
            RichImageClipboardRestoreDelayMs = 50
        };
    }
}

internal sealed class FakeDouyinVideoUploader : IDouyinVideoUploader
{
    public List<(string Path, bool SendAfterUpload, SendHotkeyMode Hotkey)> Calls { get; } = [];

    public Func<string, ReplyActionResult>? OnUpload { get; set; }

    public Action<string>? OnUploadStarted { get; set; }

    public Task<ReplyActionResult> UploadAsync(
        IntPtr targetWindowHandle,
        string videoPath,
        bool sendAfterUpload,
        SendHotkeyMode sendHotkeyMode,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((videoPath, sendAfterUpload, sendHotkeyMode));
        OnUploadStarted?.Invoke(videoPath);
        diagnostics["videoSendState"] = sendAfterUpload ? "submitted" : "staged";
        return Task.FromResult(OnUpload?.Invoke(videoPath) ?? new ReplyActionResult(
            true,
            sendAfterUpload ? DouyinVideoOpenDialogUploader.SubmittedMessage : DouyinVideoOpenDialogUploader.StagedMessage));
    }
}

internal sealed class FakeDouyinSendProgressVerifier : IDouyinSendProgressVerifier
{
    public DouyinSendVerifyOutcome Outcome { get; set; } = DouyinSendVerifyOutcome.Cleared;

    /// <summary>Default StillHasContent so unit tests that paste images can proceed to Enter.</summary>
    public DouyinSendVerifyOutcome ConfirmImageOutcome { get; set; } = DouyinSendVerifyOutcome.StillHasContent;

    public string Method { get; set; } = "fake";

    public string Detail { get; set; } = "ok";

    public int CallCount { get; private set; }

    public int ConfirmImageCallCount { get; private set; }

    public List<DouyinSendVerifyExpectation> Expectations { get; } = [];

    public Task<DouyinSendVerifyResult> VerifyComposerClearedAsync(
        IntPtr targetWindowHandle,
        DouyinSendVerifyExpectation expectation,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        Expectations.Add(expectation);
        diagnostics["douyinSendVerifyFake"] = true;
        return Task.FromResult(new DouyinSendVerifyResult(Outcome, Method, Detail));
    }

    public Task<DouyinSendVerifyResult> ConfirmComposerHasImageAsync(
        IntPtr targetWindowHandle,
        IReadOnlyList<string> imagePaths,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken = default)
    {
        ConfirmImageCallCount++;
        diagnostics["douyinImagePasteConfirmFake"] = true;
        return Task.FromResult(new DouyinSendVerifyResult(ConfirmImageOutcome, Method, Detail));
    }
}
