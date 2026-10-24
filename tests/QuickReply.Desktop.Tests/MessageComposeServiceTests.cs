using System.Diagnostics;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;

namespace QuickReply.Desktop.Tests;

public sealed class MessageComposeServiceTests
{
    [Fact]
    public async Task CopyToClipboardAsync_ReturnsFailureForEmptyPayload()
    {
        var service = CreateService(out _, out _, out _, out _);

        var result = await service.CopyToClipboardAsync(string.Empty);

        Assert.False(result.Success);
        Assert.Equal("当前图文内容为空，未写入剪贴板。", result.Message);
        Assert.Equal(ReplyActionFailureKind.EmptyContent, result.FailureKind);
    }

    [Fact]
    public async Task CopyToClipboardAsync_UsesTextAndFilesForSingleImage()
    {
        using var scope = new TestAppDataScope();
        var imagePath = scope.CreateFile("images\\sample.png", "image");
        var service = CreateService(out var clipboard, out _, out _, out _);

        var result = await service.CopyToClipboardAsync("你好", [imagePath]);

        Assert.True(result.Success);
        Assert.Equal(1, clipboard.SetTextAndFilesCallCount);
        Assert.Equal("你好", clipboard.LastText);
        Assert.Equal([imagePath], clipboard.LastFiles);
    }

    [Fact]
    public async Task CopyFilesToClipboardAsync_ReturnsFailureWhenNothingSelected()
    {
        var service = CreateService(out _, out _, out _, out _);

        var result = await service.CopyFilesToClipboardAsync([]);

        Assert.False(result.Success);
        Assert.Equal("未选择要发送的文件。", result.Message);
        Assert.Equal(ReplyActionFailureKind.EmptyContent, result.FailureKind);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailureWhenImageIsMissing()
    {
        var service = CreateService(out _, out _, out _, out _);

        var result = await service.ExecuteAsync(
            "你好",
            TestSettingsFactory.CreateFastSettings(),
            ComposeActionMode.Send,
            ["C:\\does-not-exist\\missing.png"]);

        Assert.False(result.Success);
        Assert.Contains("未找到图片文件", result.Message, StringComparison.Ordinal);
        Assert.Equal(ReplyActionFailureKind.MissingInput, result.FailureKind);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailureWhenTargetWindowIsMissing()
    {
        var service = CreateService(out var clipboard, out _, out var workbench, out _);
        workbench.LastHandle = IntPtr.Zero;

        var result = await service.ExecuteAsync(
            "hello",
            TestSettingsFactory.CreateFastSettings(),
            ComposeActionMode.Input);

        Assert.False(result.Success);
        Assert.Equal("未找到目标输入窗口。请先点击聊天输入框，再回到工具执行操作。", result.Message);
        Assert.Equal(ReplyActionFailureKind.MissingTargetWindow, result.FailureKind);
        Assert.Equal(0, clipboard.SetTextCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailureWhenWindowActivationNeverSucceeds()
    {
        var service = CreateService(out var clipboard, out _, out var workbench, out var windowFocus);
        var handle = new IntPtr(301);
        workbench.LastHandle = handle;
        workbench.ActivateResult = false;
        windowFocus.ValidHandles.Add(handle);

        var result = await service.ExecuteAsync(
            "hello",
            TestSettingsFactory.CreateFastSettings(),
            ComposeActionMode.Input);

        Assert.False(result.Success);
        Assert.Equal("已自动补救 1 次，但仍失败：未能稳定切回目标输入窗口。请先点一下聊天输入框，再重试。", result.Message);
        Assert.Equal(ReplyActionFailureKind.ActivationFailed, result.FailureKind);
        Assert.NotNull(result.ComposeTelemetry);
        Assert.True(result.ComposeTelemetry.Value.RetryTriggered);
        Assert.Equal("activation", result.ComposeTelemetry.Value.RetryReason);
        Assert.Equal("failed", result.ComposeTelemetry.Value.RetryOutcome);
        Assert.Equal(0, clipboard.SetTextCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsClipboardWriteFailureWithoutPasting()
    {
        var service = CreateService(out var clipboard, out var keyboard, out var workbench, out var windowFocus);
        var handle = new IntPtr(302);
        workbench.LastHandle = handle;
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;
        clipboard.SetTextResult = new ReplyActionResult(false, "剪贴板忙碌");

        var result = await service.ExecuteAsync(
            "hello",
            TestSettingsFactory.CreateFastSettings(),
            ComposeActionMode.Input);

        Assert.False(result.Success);
        Assert.Equal("已自动补救 1 次，但仍失败：剪贴板忙碌", result.Message);
        Assert.Equal(ReplyActionFailureKind.ClipboardBusy, result.FailureKind);
        Assert.NotNull(result.ComposeTelemetry);
        Assert.True(result.ComposeTelemetry.Value.RetryTriggered);
        Assert.Equal("clipboard", result.ComposeTelemetry.Value.RetryReason);
        Assert.Equal(2, clipboard.SetTextCallCount);
        Assert.Equal(0, keyboard.PasteShortcutCount);
    }

    [Fact]
    public async Task ExecuteAsync_TextOnlySend_SkipsActivationWhenTargetAlreadyForeground()
    {
        var service = CreateService(out _, out var keyboard, out var workbench, out var windowFocus);
        var handle = new IntPtr(120);
        workbench.LastHandle = handle;
        workbench.LastWindowInfo = CreateWindowInfo(handle, "微信", "WeChatMainWndForPC", "WeChat");
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;
        var activationCalls = 0;
        workbench.OnTryActivateLastExternalWindow = () =>
        {
            activationCalls++;
            return true;
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.ExecuteAsync("hello", TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send);
        stopwatch.Stop();

        Assert.True(result.Success);
        Assert.Equal(0, activationCalls);
        Assert.Equal(1, keyboard.PasteShortcutCount);
        Assert.Equal(1, keyboard.EnterCount);
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"happy-path text send took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task ExecuteAsync_TextOnlySend_PastesThenUsesConfiguredHotkey()
    {
        var service = CreateService(out var clipboard, out var keyboard, out var workbench, out var windowFocus);
        var handle = new IntPtr(101);
        workbench.LastHandle = handle;
        workbench.LastWindowInfo = CreateWindowInfo(handle, "微信", "WeChatMainWndForPC", "WeChat");
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;

        var settings = TestSettingsFactory.CreateFastSettings();
        settings.SendHotkeyMode = SendHotkeyMode.CtrlEnter;

        var result = await service.ExecuteAsync("hello\r\nworld", settings, ComposeActionMode.Send);

        Assert.True(result.Success);
        Assert.NotNull(result.ComposeTelemetry);
        Assert.Equal("generic_desktop_input", result.ComposeTelemetry.Value.HostProfile);
        Assert.Equal(1, result.ComposeTelemetry.Value.AttemptCount);
        Assert.False(result.ComposeTelemetry.Value.RetryTriggered);
        Assert.Equal("hello\nworld", clipboard.LastText);
        Assert.Equal(1, clipboard.SetTextCallCount);
        Assert.Equal(1, keyboard.PasteShortcutCount);
        Assert.Equal(1, keyboard.CtrlEnterCount);
        Assert.Equal(0, keyboard.EnterCount);
    }

    [Fact]
    public async Task ExecuteAsync_TextOnlySend_RetriesOnceAfterFocusLossAndSucceeds()
    {
        var service = CreateService(out _, out var keyboard, out var workbench, out var windowFocus);
        var handle = new IntPtr(103);
        workbench.LastHandle = handle;
        workbench.LastWindowInfo = CreateWindowInfo(handle, "拼多多商家工作台", "Chrome_WidgetWin_1", "PDDWorkbench");
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;
        workbench.OnTryActivateLastExternalWindow = () =>
        {
            windowFocus.ForegroundWindow = handle;
            return true;
        };

        keyboard.OnPasteShortcut = () =>
        {
            if (keyboard.PasteShortcutCount == 1)
            {
                windowFocus.ForegroundWindow = IntPtr.Zero;
                return;
            }

            windowFocus.ForegroundWindow = handle;
        };

        var result = await service.ExecuteAsync("hello", TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send);

        Assert.True(result.Success);
        Assert.Contains("已自动补救后成功", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.ComposeTelemetry);
        Assert.True(result.ComposeTelemetry.Value.RetryTriggered);
        Assert.Equal("focus_lost", result.ComposeTelemetry.Value.RetryReason);
        Assert.Equal("recovered", result.ComposeTelemetry.Value.RetryOutcome);
        Assert.Equal(2, result.ComposeTelemetry.Value.AttemptCount);
        Assert.Equal(2, keyboard.PasteShortcutCount);
    }

    [Fact]
    public async Task ExecuteAsync_TextOnlySend_RetriesOnceAfterClipboardBusyAndSucceeds()
    {
        var service = CreateService(out var clipboard, out var keyboard, out var workbench, out var windowFocus);
        var handle = new IntPtr(104);
        workbench.LastHandle = handle;
        workbench.LastWindowInfo = CreateWindowInfo(handle, "Edge", "Chrome_WidgetWin_1", "msedge");
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;

        clipboard.OnSetText = (_, _) => clipboard.SetTextCallCount == 1
            ? new ReplyActionResult(false, "写入系统剪贴板失败：OpenClipboard 失败")
            : new ReplyActionResult(true, "ok");

        var result = await service.ExecuteAsync("hello", TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send);

        Assert.True(result.Success);
        Assert.Contains("已自动补救后成功", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.ComposeTelemetry);
        Assert.True(result.ComposeTelemetry.Value.RetryTriggered);
        Assert.Equal("clipboard", result.ComposeTelemetry.Value.RetryReason);
        Assert.Equal("recovered", result.ComposeTelemetry.Value.RetryOutcome);
        Assert.Equal(2, result.ComposeTelemetry.Value.AttemptCount);
        Assert.Equal(2, clipboard.SetTextCallCount);
        Assert.Equal(1, keyboard.PasteShortcutCount);
    }

    [Fact]
    public async Task ExecuteAsync_CancelsRetryWhenTargetWindowChangedBeforeRecovery()
    {
        var service = CreateService(out _, out var keyboard, out var workbench, out var windowFocus);
        var originalHandle = new IntPtr(106);
        var changedHandle = new IntPtr(107);
        var originalWindow = CreateWindowInfo(originalHandle, "拼多多商家工作台", "Chrome_WidgetWin_1", "PDDWorkbench");
        var changedWindow = CreateWindowInfo(changedHandle, "微信", "WeChatMainWndForPC", "WeChat");
        var lastWindowInfoCallCount = 0;

        workbench.LastHandle = originalHandle;
        workbench.OnGetLastExternalWindowInfo = () =>
        {
            lastWindowInfoCallCount++;
            return lastWindowInfoCallCount >= 3 ? changedWindow : originalWindow;
        };
        workbench.OnTryActivateLastExternalWindow = () =>
        {
            windowFocus.ForegroundWindow = originalHandle;
            return true;
        };
        windowFocus.ValidHandles.Add(originalHandle);
        windowFocus.ValidHandles.Add(changedHandle);
        windowFocus.ForegroundWindow = originalHandle;
        keyboard.OnPasteShortcut = () => windowFocus.ForegroundWindow = IntPtr.Zero;

        var result = await service.ExecuteAsync("hello", TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send);

        Assert.False(result.Success);
        Assert.Equal("检测到目标输入窗口已变化，已取消自动补救。请重新回到目标聊天窗口后再试。", result.Message);
        Assert.Equal(ReplyActionFailureKind.TargetChanged, result.FailureKind);
        Assert.NotNull(result.ComposeTelemetry);
        Assert.False(result.ComposeTelemetry.Value.RetryTriggered);
        Assert.Equal("focus_lost", result.ComposeTelemetry.Value.RetryReason);
        Assert.Equal("cancelled_target_changed", result.ComposeTelemetry.Value.RetryOutcome);
        Assert.True(result.ComposeTelemetry.Value.TargetChangedBeforeRetry);
        Assert.Equal("target_changed", result.ComposeTelemetry.Value.FailureCategory);
        Assert.Equal(1, keyboard.PasteShortcutCount);
    }

    [Fact]
    public async Task ExecuteAsync_TextOnlyInput_SkipsClipboardRestoreWhenSnapshotCannotBeRestored()
    {
        var service = CreateService(out var clipboard, out _, out var workbench, out var windowFocus);
        var handle = new IntPtr(102);
        workbench.LastHandle = handle;
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;
        clipboard.SnapshotToReturn = new ClipboardSnapshot(
            captureSucceeded: true,
            hasContent: true,
            dataObject: null,
            fingerprint: new ClipboardFingerprint("original"),
            canRestore: false);

        var settings = TestSettingsFactory.CreateFastSettings();
        settings.ClipboardRestoreDelayMs = 1;

        var result = await service.ExecuteAsync("hello", settings, ComposeActionMode.Input);

        await Task.Delay(80);

        Assert.True(result.Success);
        Assert.Equal("已输入到当前窗口，但未能完整备份原剪贴板，当前剪贴板可能仍保留本次内容。", result.Message);
        Assert.Equal(0, clipboard.RestoreCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_TextOnlySend_BrowserHost_UsesAdaptiveWaitInsteadOfFullFallbackDelay()
    {
        var service = CreateService(out _, out _, out var workbench, out var windowFocus);
        var handle = new IntPtr(105);
        workbench.LastHandle = handle;
        workbench.LastWindowInfo = CreateWindowInfo(handle, "Edge", "Chrome_WidgetWin_1", "msedge");
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;

        var settings = TestSettingsFactory.CreateFastSettings();
        settings.TextPasteDelayMs = 1200;
        settings.SendDelayMs = 1200;

        var stopwatch = Stopwatch.StartNew();
        var result = await service.ExecuteAsync("hello", settings, ComposeActionMode.Send);
        stopwatch.Stop();

        Assert.True(result.Success);
        Assert.True(stopwatch.ElapsedMilliseconds < 700, $"Expected adaptive send to finish quickly, actual {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task ExecuteFilesAsync_PastesFilesWhenWindowIsReady()
    {
        using var scope = new TestAppDataScope();
        var filePath = scope.CreateFile("files\\manual.pdf", "pdf");
        var service = CreateService(out var clipboard, out var keyboard, out var workbench, out var windowFocus);
        var handle = new IntPtr(202);
        workbench.LastHandle = handle;
        workbench.LastWindowInfo = CreateWindowInfo(handle, "微信", "WeChatMainWndForPC", "WeChat");
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;

        var result = await service.ExecuteFilesAsync(
            [filePath],
            TestSettingsFactory.CreateFastSettings(),
            ComposeActionMode.Input);

        Assert.True(result.Success);
        Assert.Equal(1, clipboard.SetFileDropListCallCount);
        Assert.Equal(1, keyboard.PasteShortcutCount);
        Assert.Equal(0, keyboard.EnterCount);
    }

    [Fact]
    public async Task ExecuteFilesAsync_Send_SkipsClipboardRestoreWhenSnapshotCannotBeRestored()
    {
        using var scope = new TestAppDataScope();
        var filePath = scope.CreateFile("files\\attachment.png", "image");
        var service = CreateService(out var clipboard, out var keyboard, out var workbench, out var windowFocus);
        var handle = new IntPtr(203);
        workbench.LastHandle = handle;
        workbench.LastWindowInfo = CreateWindowInfo(handle, "拼多多商家工作台", "Chrome_WidgetWin_1", "PDDWorkbench");
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;
        workbench.OnTryActivateLastExternalWindow = () =>
        {
            windowFocus.ForegroundWindow = handle;
            return true;
        };
        clipboard.SnapshotToReturn = new ClipboardSnapshot(
            captureSucceeded: true,
            hasContent: true,
            dataObject: null,
            fingerprint: new ClipboardFingerprint("original"),
            canRestore: false);
        keyboard.OnPasteShortcut = () => windowFocus.ForegroundWindow = IntPtr.Zero;

        var result = await service.ExecuteFilesAsync(
            [filePath],
            TestSettingsFactory.CreateFastSettings(),
            ComposeActionMode.Send);

        Assert.False(result.Success);
        Assert.Equal("已自动补救 1 次，但仍失败：目标输入窗口焦点已丢失，请先点一下聊天输入框，再重试。", result.Message);
        Assert.Equal(ReplyActionFailureKind.FocusLost, result.FailureKind);
        Assert.Equal(0, clipboard.RestoreCallCount);
        Assert.Equal(2, keyboard.PasteShortcutCount);
    }

    [Fact]
    public async Task ExecuteFilesAsync_RetriesOnceAfterClipboardBusyAndSucceeds()
    {
        using var scope = new TestAppDataScope();
        var filePath = scope.CreateFile("files\\manual.pdf", "pdf");
        var service = CreateService(out var clipboard, out var keyboard, out var workbench, out var windowFocus);
        var handle = new IntPtr(204);
        workbench.LastHandle = handle;
        workbench.LastWindowInfo = CreateWindowInfo(handle, "微信", "WeChatMainWndForPC", "WeChat");
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;
        clipboard.OnSetFileDropList = (_, _) => clipboard.SetFileDropListCallCount == 1
            ? new ReplyActionResult(false, "写入文件剪贴板失败：OpenClipboard 失败")
            : new ReplyActionResult(true, "ok");

        var result = await service.ExecuteFilesAsync(
            [filePath],
            TestSettingsFactory.CreateFastSettings(),
            ComposeActionMode.Input);

        Assert.True(result.Success);
        Assert.Contains("已自动补救后成功", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.ComposeTelemetry);
        Assert.True(result.ComposeTelemetry.Value.RetryTriggered);
        Assert.Equal("clipboard", result.ComposeTelemetry.Value.RetryReason);
        Assert.Equal("recovered", result.ComposeTelemetry.Value.RetryOutcome);
        Assert.Equal(2, clipboard.SetFileDropListCallCount);
        Assert.Equal(1, keyboard.PasteShortcutCount);
    }

    [Fact]
    public async Task ExecuteAsync_TextWithImage_RetriesOnceAfterClipboardBusyAndSucceeds()
    {
        using var scope = new TestAppDataScope();
        var imagePath = scope.CreateFile("images\\sample.png", "image");
        var service = CreateService(out var clipboard, out var keyboard, out var workbench, out var windowFocus);
        var handle = new IntPtr(205);
        workbench.LastHandle = handle;
        workbench.LastWindowInfo = CreateWindowInfo(handle, "微信", "WeChatMainWndForPC", "WeChat");
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;
        clipboard.OnSetFileDropList = (_, _) => clipboard.SetFileDropListCallCount == 1
            ? new ReplyActionResult(false, "写入文件剪贴板失败：OpenClipboard 失败")
            : new ReplyActionResult(true, "ok");

        var result = await service.ExecuteAsync(
            "图文消息",
            TestSettingsFactory.CreateFastSettings(),
            ComposeActionMode.Input,
            [imagePath]);

        Assert.True(result.Success);
        Assert.Contains("已自动补救后成功", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.ComposeTelemetry);
        Assert.True(result.ComposeTelemetry.Value.RetryTriggered);
        Assert.Equal("clipboard", result.ComposeTelemetry.Value.RetryReason);
        Assert.Equal("recovered", result.ComposeTelemetry.Value.RetryOutcome);
        Assert.Equal(2, clipboard.SetFileDropListCallCount);
        Assert.Equal(1, clipboard.SetTextCallCount);
        Assert.Equal(2, keyboard.PasteShortcutCount);
    }

    [Fact]
    public async Task ExecuteAsync_TextWithImage_ReturnsStructuredFocusFailureWhenImagePasteLosesForeground()
    {
        using var scope = new TestAppDataScope();
        var imagePath = scope.CreateFile("images\\sample.png", "image");
        var service = CreateService(out var clipboard, out var keyboard, out var workbench, out var windowFocus);
        var handle = new IntPtr(206);
        workbench.LastHandle = handle;
        workbench.LastWindowInfo = CreateWindowInfo(handle, "拼多多商家工作台", "Chrome_WidgetWin_1", "PDDWorkbench");
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;
        workbench.OnTryActivateLastExternalWindow = () =>
        {
            windowFocus.ForegroundWindow = handle;
            return true;
        };
        keyboard.OnPasteShortcut = () => windowFocus.ForegroundWindow = IntPtr.Zero;

        var result = await service.ExecuteAsync(
            "图文消息",
            TestSettingsFactory.CreateFastSettings(),
            ComposeActionMode.Send,
            [imagePath]);

        Assert.False(result.Success);
        Assert.Equal(ReplyActionFailureKind.FocusLost, result.FailureKind);
        Assert.Contains("发送图文内容失败", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.ComposeTelemetry);
        Assert.True(result.ComposeTelemetry.Value.RetryTriggered);
        Assert.Equal("focus_lost", result.ComposeTelemetry.Value.RetryReason);
        Assert.Equal("failed", result.ComposeTelemetry.Value.RetryOutcome);
        Assert.Equal(2, keyboard.PasteShortcutCount);
        Assert.Equal(0, clipboard.RestoreCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_DouyinTextWithImage_SendsImageAndTextAsSeparateMessages()
    {
        using var scope = new TestAppDataScope();
        var imagePath = scope.CreateFile("images\\sample.png", "image");
        var service = CreateService(out var clipboard, out var keyboard, out var workbench, out var windowFocus);
        var handle = new IntPtr(207);
        workbench.LastHandle = handle;
        workbench.LastWindowInfo = CreateWindowInfo(handle, "飞鸽客服", "Chrome_WidgetWin_1", "doudian");
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;
        workbench.OnTryActivateLastExternalWindow = () =>
        {
            windowFocus.ForegroundWindow = handle;
            return true;
        };

        var result = await service.ExecuteAsync(
            "图文测试文字",
            TestSettingsFactory.CreateFastSettings(),
            ComposeActionMode.Send,
            [imagePath]);

        Assert.True(result.Success);
        Assert.Equal(1, clipboard.SetFileDropListCallCount);
        Assert.Equal(1, clipboard.SetTextCallCount);
        Assert.Equal(2, keyboard.PasteShortcutCount);
        Assert.Equal(2, keyboard.EnterCount);
        Assert.NotNull(result.ComposeTelemetry);
        Assert.Equal("douyin_workbench", result.ComposeTelemetry.Value.HostProfile);
    }

    [Fact]
    public async Task ExecuteAsync_NonDouyinTextWithImage_StillUsesSingleFinalSend()
    {
        using var scope = new TestAppDataScope();
        var imagePath = scope.CreateFile("images\\sample.png", "image");
        var service = CreateService(out var clipboard, out var keyboard, out var workbench, out var windowFocus);
        var handle = new IntPtr(208);
        workbench.LastHandle = handle;
        workbench.LastWindowInfo = CreateWindowInfo(handle, "微信", "WeChatMainWndForPC", "WeChat");
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;
        workbench.OnTryActivateLastExternalWindow = () =>
        {
            windowFocus.ForegroundWindow = handle;
            return true;
        };

        var result = await service.ExecuteAsync(
            "图文测试文字",
            TestSettingsFactory.CreateFastSettings(),
            ComposeActionMode.Send,
            [imagePath]);

        Assert.True(result.Success);
        Assert.Equal(1, clipboard.SetFileDropListCallCount);
        Assert.Equal(1, clipboard.SetTextCallCount);
        Assert.Equal(2, keyboard.PasteShortcutCount);
        Assert.Equal(1, keyboard.EnterCount);
    }

    private static MessageComposeService CreateService(
        out FakeClipboardService clipboard,
        out FakeKeyboardInputSimulator keyboard,
        out FakeExternalWorkbenchService workbench,
        out FakeWindowFocusService windowFocus)
    {
        clipboard = new FakeClipboardService();
        keyboard = new FakeKeyboardInputSimulator();
        workbench = new FakeExternalWorkbenchService();
        windowFocus = new FakeWindowFocusService();

        return new MessageComposeService(
            workbench,
            clipboard,
            keyboard,
            windowFocus,
            douyinSendProgressVerifier: new FakeDouyinSendProgressVerifier());
    }

    private static WindowDockTargetInfo CreateWindowInfo(IntPtr handle, string title, string className, string processName)
    {
        return new WindowDockTargetInfo
        {
            Handle = handle,
            Title = title,
            WindowClassName = className,
            ProcessName = processName,
            ProcessPath = $"C:\\Program Files\\{processName}\\{processName}.exe",
            Left = 0,
            Top = 0,
            Width = 1280,
            Height = 800
        };
    }
}
