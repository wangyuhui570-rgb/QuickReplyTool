using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;

namespace QuickReply.Desktop.Tests;

public sealed class DouyinVideoComposeTests
{
    [Fact]
    public async Task SingleVideo_Send_UsesToolbarUploaderAndReportsSubmitted()
    {
        using var scope = new TestAppDataScope();
        var video = scope.CreateFile("videos\\a.mp4", "video");
        var service = CreateDouyinService(301, out var clipboard, out var keyboard, out var uploader, out _);

        var result = await service.ExecuteFilesAsync([video], TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send);

        Assert.True(result.Success, result.Message);
        Assert.Contains("已提交", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("已发出", result.Message, StringComparison.Ordinal);
        var call = Assert.Single(uploader.Calls);
        Assert.True(call.SendAfterUpload);
        Assert.Equal(0, clipboard.SetFileDropListCallCount);
        Assert.Equal(0, keyboard.PasteShortcutCount);
    }

    [Fact]
    public async Task MultipleVideos_AreUploadedOneByOne()
    {
        using var scope = new TestAppDataScope();
        var first = scope.CreateFile("videos\\a.mp4", "video");
        var second = scope.CreateFile("videos\\b.mov", "video");
        var service = CreateDouyinService(302, out var clipboard, out _, out var uploader, out _);

        var result = await service.ExecuteFilesAsync([first, second], TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, uploader.Calls.Count);
        Assert.EndsWith("a.mp4", uploader.Calls[0].Path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("b.mov", uploader.Calls[1].Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, clipboard.SetFileDropListCallCount);
    }

    [Fact]
    public async Task VideoWithImage_SendsImageAsOwnMessageThenUploadsVideo()
    {
        using var scope = new TestAppDataScope();
        var image = scope.CreateFile("images\\a.png", "image");
        var video = scope.CreateFile("videos\\a.mp4", "video");
        var events = new List<string>();
        var service = CreateDouyinService(303, out var clipboard, out var keyboard, out var uploader, out _);
        clipboard.OnSetFileDropList = (files, _) =>
        {
            Assert.DoesNotContain(files, f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
            events.Add("image");
            return new ReplyActionResult(true, "ok");
        };
        keyboard.OnSendKey = () => events.Add("send");
        uploader.OnUploadStarted = _ => events.Add("video");

        var result = await service.ExecuteFilesAsync([video, image], TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send);

        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { "image", "send", "video" }, events);
    }

    [Fact]
    public async Task TextImageAndVideo_FollowsImageFirstDefault_ThenVideo()
    {
        using var scope = new TestAppDataScope();
        var image = scope.CreateFile("images\\a.png", "image");
        var video = scope.CreateFile("videos\\a.mp4", "video");
        var events = new List<string>();
        var service = CreateDouyinService(304, out var clipboard, out var keyboard, out var uploader, out _);
        clipboard.OnSetFileDropList = (_, _) => { events.Add("image"); return new ReplyActionResult(true, "ok"); };
        clipboard.OnSetText = (_, _) => { events.Add("text"); return new ReplyActionResult(true, "ok"); };
        keyboard.OnSendKey = () => events.Add("send");
        uploader.OnUploadStarted = _ => events.Add("video");

        var result = await service.ExecuteAsync("文字", TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send, [image, video]);

        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { "image", "send", "text", "send", "video" }, events);
    }

    [Fact]
    public async Task SameVideoTwiceWithinWindow_SecondIsBlocked()
    {
        using var scope = new TestAppDataScope();
        var video = scope.CreateFile("videos\\a.mp4", "video");
        var guard = new DouyinVideoSendGuard(() => DateTimeOffset.Parse("2026-10-23T10:00:00+08:00"));
        var service = CreateDouyinService(305, out _, out _, out var uploader, out _, guard);

        var first = await service.ExecuteFilesAsync([video], TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send);
        var second = await service.ExecuteFilesAsync([video], TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send);

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal(ReplyActionFailureKind.ManualConfirmationRequired, second.FailureKind);
        Assert.Contains("重复", second.Message, StringComparison.Ordinal);
        Assert.Single(uploader.Calls);
    }

    [Fact]
    public async Task SameVideoAfterWindow_IsAllowed()
    {
        using var scope = new TestAppDataScope();
        var video = scope.CreateFile("videos\\a.mp4", "video");
        var now = DateTimeOffset.Parse("2026-10-23T10:00:00+08:00");
        var guard = new DouyinVideoSendGuard(() => now);
        var service = CreateDouyinService(306, out _, out _, out var uploader, out _, guard);

        await service.ExecuteFilesAsync([video], TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send);
        now = now.AddSeconds(31);
        var again = await service.ExecuteFilesAsync([video], TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send);

        Assert.True(again.Success, again.Message);
        Assert.Equal(2, uploader.Calls.Count);
    }

    [Fact]
    public async Task FailedUpload_IsNotRecordedAsDuplicate_AndIsNotRetried()
    {
        using var scope = new TestAppDataScope();
        var video = scope.CreateFile("videos\\a.mp4", "video");
        var guard = new DouyinVideoSendGuard();
        var service = CreateDouyinService(307, out _, out _, out var uploader, out _, guard);
        uploader.OnUpload = _ => new ReplyActionResult(false, "未出现文件选择框", FailureKind: ReplyActionFailureKind.ManualConfirmationRequired);

        var first = await service.ExecuteFilesAsync([video], TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send);
        uploader.OnUpload = null;
        var second = await service.ExecuteFilesAsync([video], TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send);

        Assert.False(first.Success);
        Assert.True(second.Success, second.Message);
        Assert.Equal(2, uploader.Calls.Count);
    }

    [Fact]
    public async Task VideoFailureAfterTextSent_IsNonRetryableAndKeepsTextSentOnce()
    {
        using var scope = new TestAppDataScope();
        var video = scope.CreateFile("videos\\a.mp4", "video");
        var service = CreateDouyinService(308, out var clipboard, out var keyboard, out var uploader, out _);
        uploader.OnUpload = _ => new ReplyActionResult(false, "窗口过小", FailureKind: ReplyActionFailureKind.ActivationFailed);

        var result = await service.ExecuteAsync("文字", TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send, [video]);

        Assert.False(result.Success);
        Assert.Equal(ReplyActionFailureKind.ManualConfirmationRequired, result.FailureKind);
        Assert.Equal(1, clipboard.SetTextCallCount);
        Assert.Equal(1, keyboard.EnterCount);
        Assert.Single(uploader.Calls);
    }

    [Fact]
    public async Task PasteMode_StagesVideoWithoutSending()
    {
        using var scope = new TestAppDataScope();
        var video = scope.CreateFile("videos\\a.mp4", "video");
        var service = CreateDouyinService(309, out _, out var keyboard, out var uploader, out _);

        var result = await service.ExecuteFilesAsync([video], TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Paste);

        Assert.True(result.Success, result.Message);
        Assert.False(Assert.Single(uploader.Calls).SendAfterUpload);
        Assert.Equal(0, keyboard.EnterCount);
    }

    [Fact]
    public async Task NonDouyinVideo_StillUsesFileDrop()
    {
        using var scope = new TestAppDataScope();
        var video = scope.CreateFile("videos\\a.mp4", "video");
        var clipboard = new FakeClipboardService();
        var keyboard = new FakeKeyboardInputSimulator();
        var workbench = new FakeExternalWorkbenchService();
        var windowFocus = new FakeWindowFocusService();
        var uploader = new FakeDouyinVideoUploader();
        var handle = new IntPtr(310);
        Arrange(workbench, windowFocus, handle, CreateWindowInfo(handle, "微信", "WeChatMainWndForPC", "WeChat"));
        var service = new MessageComposeService(workbench, clipboard, keyboard, windowFocus, uploader, new DouyinVideoSendGuard());

        var result = await service.ExecuteFilesAsync([video], TestSettingsFactory.CreateFastSettings(), ComposeActionMode.Send);

        Assert.True(result.Success, result.Message);
        Assert.Empty(uploader.Calls);
        Assert.Equal(1, clipboard.SetFileDropListCallCount);
    }

    [Theory]
    [InlineData(PhraseImageSendMode.ImageThenTextThenSend, new[] { "image", "send", "text", "send" })]
    [InlineData(PhraseImageSendMode.ImageImmediateThenTextSend, new[] { "image", "send", "text", "send" })]
    [InlineData(PhraseImageSendMode.TextThenImageThenSend, new[] { "text", "send", "image", "send" })]
    public async Task DouyinTextWithImage_RespectsPhraseImageSendMode(PhraseImageSendMode sendMode, string[] expected)
    {
        using var scope = new TestAppDataScope();
        var image = scope.CreateFile("images\\a.png", "image");
        var events = new List<string>();
        var service = CreateDouyinService(311, out var clipboard, out var keyboard, out _, out _);
        clipboard.OnSetFileDropList = (_, _) => { events.Add("image"); return new ReplyActionResult(true, "ok"); };
        clipboard.OnSetText = (_, _) => { events.Add("text"); return new ReplyActionResult(true, "ok"); };
        keyboard.OnSendKey = () => events.Add("send");
        var settings = TestSettingsFactory.CreateFastSettings();
        settings.PhraseImageSendMode = sendMode;

        var result = await service.ExecuteAsync("文字", settings, ComposeActionMode.Send, [image]);

        Assert.True(result.Success, result.Message);
        Assert.Equal(expected, events);
    }

    [Theory]
    [InlineData(PhraseImageSendMode.ImageThenTextThenSend, new[] { "image", "text", "send" })]
    [InlineData(PhraseImageSendMode.TextThenImageThenSend, new[] { "text", "image", "send" })]
    [InlineData(PhraseImageSendMode.ImageImmediateThenTextSend, new[] { "image", "send", "text", "send" })]
    public async Task NonDouyinTextWithImage_RespectsPhraseImageSendMode(PhraseImageSendMode sendMode, string[] expected)
    {
        using var scope = new TestAppDataScope();
        var image = scope.CreateFile("images\\a.png", "image");
        var events = new List<string>();
        var clipboard = new FakeClipboardService();
        var keyboard = new FakeKeyboardInputSimulator();
        var workbench = new FakeExternalWorkbenchService();
        var windowFocus = new FakeWindowFocusService();
        var handle = new IntPtr(312);
        Arrange(workbench, windowFocus, handle, CreateWindowInfo(handle, "微信", "WeChatMainWndForPC", "WeChat"));
        var service = new MessageComposeService(workbench, clipboard, keyboard, windowFocus);
        clipboard.OnSetFileDropList = (_, _) => { events.Add("image"); return new ReplyActionResult(true, "ok"); };
        clipboard.OnSetText = (_, _) => { events.Add("text"); return new ReplyActionResult(true, "ok"); };
        keyboard.OnSendKey = () => events.Add("send");
        var settings = TestSettingsFactory.CreateFastSettings();
        settings.PhraseImageSendMode = sendMode;

        var result = await service.ExecuteAsync("文字", settings, ComposeActionMode.Send, [image]);

        Assert.True(result.Success, result.Message);
        Assert.Equal(expected, events);
    }

    [Fact]
    public void AppSettings_DefaultPhraseImageSendMode_IsImageFirst()
    {
        Assert.Equal(PhraseImageSendMode.ImageThenTextThenSend, new AppSettings().PhraseImageSendMode);
    }

    [Fact]
    public void Guard_BusyWhileUploadInProgress()
    {
        var guard = new DouyinVideoSendGuard();
        var handle = new IntPtr(1);

        Assert.Equal(DouyinVideoSendGuard.GuardDecision.Allowed, guard.TryBegin(handle, "C:\\v\\a.mp4"));
        Assert.Equal(DouyinVideoSendGuard.GuardDecision.Busy, guard.TryBegin(handle, "C:\\v\\b.mp4"));
        guard.Complete(handle, "C:\\v\\a.mp4", submitted: true);
        Assert.Equal(DouyinVideoSendGuard.GuardDecision.Allowed, guard.TryBegin(handle, "C:\\v\\b.mp4"));
    }

    [Fact]
    public void DouyinTransferPlan_NoLongerRequiresManualConfirmation()
    {
        var payload = ComposeFilePayloadClassifier.Classify(["C:\\v\\a.mp4"]);
        var plan = ComposeFilePayloadClassifier.BuildTransferPlan(payload, SendHostProfile.DouyinWorkbench, new AppSettings());

        Assert.False(plan.SkipAutomaticSendHotkey);
        Assert.DoesNotContain("manual_confirm", plan.StrategyName, StringComparison.Ordinal);
    }

    private static MessageComposeService CreateDouyinService(
        int handleValue,
        out FakeClipboardService clipboard,
        out FakeKeyboardInputSimulator keyboard,
        out FakeDouyinVideoUploader uploader,
        out FakeWindowFocusService windowFocus,
        DouyinVideoSendGuard? guard = null)
    {
        clipboard = new FakeClipboardService();
        keyboard = new FakeKeyboardInputSimulator();
        uploader = new FakeDouyinVideoUploader();
        var workbench = new FakeExternalWorkbenchService();
        windowFocus = new FakeWindowFocusService();
        var handle = new IntPtr(handleValue);
        Arrange(workbench, windowFocus, handle, CreateWindowInfo(handle, "飞鸽客服", "Chrome_WidgetWin_1", "doudian"));
        return new MessageComposeService(workbench, clipboard, keyboard, windowFocus, uploader, guard ?? new DouyinVideoSendGuard());
    }

    private static void Arrange(FakeExternalWorkbenchService workbench, FakeWindowFocusService windowFocus, IntPtr handle, WindowDockTargetInfo info)
    {
        workbench.LastHandle = handle;
        workbench.LastWindowInfo = info;
        windowFocus.ValidHandles.Add(handle);
        windowFocus.ForegroundWindow = handle;
        workbench.OnTryActivateLastExternalWindow = () =>
        {
            windowFocus.ForegroundWindow = handle;
            return true;
        };
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
            Height = 900
        };
    }
}
