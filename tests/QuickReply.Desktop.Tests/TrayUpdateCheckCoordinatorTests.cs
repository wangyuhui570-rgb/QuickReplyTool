using QuickReply.Desktop.Services;
using Forms = System.Windows.Forms;

namespace QuickReply.Desktop.Tests;

public sealed class TrayUpdateCheckCoordinatorTests
{
    [Fact]
    public async Task RunAsync_ShowsNoUpdateNotificationWhenNothingChanged()
    {
        var notifications = new List<(string Title, string Message, Forms.ToolTipIcon Icon)>();
        var coordinator = new TrayUpdateCheckCoordinator(
            _ => Task.FromResult(new UpdateCheckResult(true, false, "ok", false, false, null)),
            (title, message, icon, _) => notifications.Add((title, message, icon)));

        await coordinator.RunAsync();

        Assert.Collection(
            notifications,
            item => Assert.Equal("正在检查更新...", item.Message),
            item => Assert.Equal("当前没有检测到新版本或新的知识分析结果。", item.Message));
    }

    [Fact]
    public async Task RunAsync_DoesNotStartConcurrentChecks()
    {
        var invocationCount = 0;
        var gate = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new TrayUpdateCheckCoordinator(
            _ =>
            {
                Interlocked.Increment(ref invocationCount);
                return gate.Task;
            },
            (_, _, _, _) => { });

        var first = coordinator.RunAsync();
        var second = coordinator.RunAsync();

        gate.SetResult(new UpdateCheckResult(true, false, "ok", false, false, null));
        await Task.WhenAll(first, second);

        Assert.Equal(1, invocationCount);
    }
}
