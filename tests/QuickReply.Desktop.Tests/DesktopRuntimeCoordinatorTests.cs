using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class DesktopRuntimeCoordinatorTests
{
    [Fact]
    public async Task CheckForUpdatesNowAsync_RunsPromptHandlerOnlyWhenRequestedAndSuccessful()
    {
        var settings = new AppSettings { ServerBaseUrl = "https://example.com" };
        var updateResult = new UpdateCheckResult(
            Success: true,
            Skipped: false,
            Message: "发现新版本",
            HasAppUpdate: true,
            HasKnowledgeUpdate: false,
            Manifest: new CloudUpdateManifest { LatestAppVersion = "2.1.11" });
        var promptInvocationCount = 0;
        var coordinator = new DesktopRuntimeCoordinator(
            () => settings,
            (_, _, _) => Task.FromResult(new KnowledgeSyncRunResult(false, true, "skip", 0, 0, 0, 0, 0, 0, 0)),
            (_, _, _) => Task.FromResult(updateResult),
            (_, _) =>
            {
                promptInvocationCount++;
                return Task.CompletedTask;
            },
            () => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask);

        var shown = await coordinator.CheckForUpdatesNowAsync(settings, showUpdatePrompt: true);
        var hidden = await coordinator.CheckForUpdatesNowAsync(settings, showUpdatePrompt: false);

        Assert.Equal(updateResult, shown);
        Assert.Equal(updateResult, hidden);
        Assert.Equal(1, promptInvocationCount);
    }

    [Fact]
    public async Task RunBackgroundStartupTasksAsync_ReportsSyncFailureAndStopsAfterUpdateFailure()
    {
        var settings = new AppSettings { ServerBaseUrl = "https://example.com" };
        var notifications = new List<string>();
        var refreshIssueCount = 0;
        var coordinator = new DesktopRuntimeCoordinator(
            () => settings,
            (_, _, _) => Task.FromResult(new KnowledgeSyncRunResult(false, false, "云同步失败", 0, 0, 0, 0, 0, 0, 0)),
            (_, _, _) => Task.FromResult(new UpdateCheckResult(false, false, "更新检查失败", false, false, null)),
            (_, _) => Task.CompletedTask,
            () =>
            {
                refreshIssueCount++;
                return Task.CompletedTask;
            },
            (title, message, isWarning) =>
            {
                notifications.Add($"{title}|{message}|{isWarning}");
                return Task.CompletedTask;
            });

        await coordinator.RunBackgroundStartupTasksAsync();

        Assert.Contains("云同步|云同步失败|True", notifications);
        Assert.Contains("更新检查|更新检查失败|True", notifications);
        Assert.Equal(0, refreshIssueCount);
    }
}
