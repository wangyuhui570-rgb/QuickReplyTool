using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class ScreenshotToolHealthEvaluatorTests
{
    private static ScreenshotToolStatus Status(bool registered = true, long heartbeatAgeMs = 100) =>
        new("Alt+A", registered, registered ? "Alt+A" : string.Empty, registered ? string.Empty : "快捷键 Alt+A 已被其他软件占用", registered ? 0 : 1409, heartbeatAgeMs);

    [Fact]
    public void Evaluate_NotListening_Restarts()
    {
        Assert.Equal(ScreenshotToolHealthAction.Restart, ScreenshotToolHealthEvaluator.Evaluate(false, null));
    }

    [Fact]
    public void Evaluate_OldToolWithoutStatus_IsLeftAlone()
    {
        Assert.Equal(ScreenshotToolHealthAction.None, ScreenshotToolHealthEvaluator.Evaluate(true, null));
    }

    [Fact]
    public void Evaluate_StaleHeartbeat_Restarts()
    {
        Assert.Equal(ScreenshotToolHealthAction.Restart, ScreenshotToolHealthEvaluator.Evaluate(true, Status(heartbeatAgeMs: 60_000)));
    }

    [Fact]
    public void Evaluate_UnregisteredHotkey_Reports()
    {
        Assert.Equal(ScreenshotToolHealthAction.ReportHotkeyFailure, ScreenshotToolHealthEvaluator.Evaluate(true, Status(registered: false)));
        Assert.Equal(ScreenshotToolHealthAction.None, ScreenshotToolHealthEvaluator.Evaluate(true, Status()));
    }

    [Fact]
    public void ParseStatus_ReadsToolReply_AndRejectsGarbage()
    {
        var status = ScreenshotToolHealthEvaluator.ParseStatus("""{"hotkey":"Alt+A","registered":false,"registered_hotkey":"","error":"占用","error_code":1409,"heartbeat_age_ms":1500,"pid":42}""");

        Assert.NotNull(status);
        Assert.False(status!.Registered);
        Assert.Equal(1409, status.ErrorCode);
        Assert.Equal(1500, status.HeartbeatAgeMs);
        Assert.Null(ScreenshotToolHealthEvaluator.ParseStatus("unsupported"));
        Assert.Null(ScreenshotToolHealthEvaluator.ParseStatus("{not json"));
        Assert.Null(ScreenshotToolHealthEvaluator.ParseStatus(null));
    }

    [Fact]
    public void CanRestart_LimitsRestartsWithinWindow()
    {
        var now = DateTimeOffset.Now;
        var restarts = new List<DateTimeOffset> { now.AddMinutes(-1), now.AddMinutes(-2), now.AddMinutes(-3) };
        Assert.False(ScreenshotToolHealthEvaluator.CanRestart(restarts, now));

        restarts = [now.AddMinutes(-30), now.AddMinutes(-1)];
        Assert.True(ScreenshotToolHealthEvaluator.CanRestart(restarts, now));
        Assert.Single(restarts);
    }

    [Fact]
    public void SuggestFallbackHotkey_AvoidsCurrentAndChatAppDefaults()
    {
        Assert.Equal("Ctrl+Shift+A", ScreenshotToolHealthEvaluator.SuggestFallbackHotkey("Alt+A"));
        Assert.Equal("Alt+Shift+A", ScreenshotToolHealthEvaluator.SuggestFallbackHotkey("Ctrl+Shift+A"));
        Assert.Contains("Ctrl+Shift+A", ScreenshotToolHealthEvaluator.BuildHotkeyFailureMessage(Status(registered: false)));
    }
}
