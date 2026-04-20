using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class AppUpdateWorkflowCoordinatorTests
{
    [Fact]
    public async Task HandleUpdateCheckResultAsync_UsesZipOtaFlowAndRequestsRestart()
    {
        var updateState = new FakeUpdateCheckStateService();
        var packageService = new FakePackageUpdateService
        {
            CanSelfApply = true,
            LaunchResult = new PackageUpdateLaunchResult(true, true, "ok", "C:\\temp\\package.zip", "C:\\temp\\update.log")
        };
        var patchState = new FakePatchInstallationStateService();
        var ui = new FakeAppUpdateWorkflowUi
        {
            ConfirmResult = true
        };
        var coordinator = new AppUpdateWorkflowCoordinator(updateState, packageService, patchState, ui);
        var settings = new AppSettings();
        var manifest = new CloudUpdateManifest
        {
            LatestAppVersion = "2.2.0",
            LatestAppDownloadUrl = "https://updates.example.com/QuickReplyTool-AppUpdate-v2.2.0.zip",
            LatestAppPackageKind = AppUpdatePackageResolver.ZipOtaKind,
            LatestAppSha256 = "ABC123",
            LatestAppSizeBytes = 2048,
            LatestAppReleaseNotes = "修复发送稳定性问题"
        };

        await coordinator.HandleUpdateCheckResultAsync(
            settings,
            new UpdateCheckResult(true, false, "发现可用更新。", true, false, manifest));

        Assert.True(packageService.DownloadAndLaunchCalled);
        Assert.Equal(manifest.LatestAppDownloadUrl, packageService.LastDownloadUrl);
        Assert.Equal("2.2.0", packageService.LastVersionLabel);
        Assert.Equal("app_update", packageService.LastSourceKind);
        Assert.Equal("ABC123", packageService.LastExpectedSha256);
        Assert.Equal(2048, packageService.LastExpectedSizeBytes);
        Assert.Equal(AppUpdatePackageResolver.ZipOtaKind, packageService.LastPackageKind);
        Assert.Contains(ui.BackgroundNotifications, entry => entry.Title == "程序更新" && entry.Message.Contains("正在下载更新包", StringComparison.Ordinal));
        Assert.Contains(ui.InfoMessages, entry => entry.Message.Contains("自动安装 2.2.0", StringComparison.Ordinal));
        Assert.True(ui.PrepareForRestartCalled);
        Assert.True(ui.ShutdownCalled);
        Assert.Null(updateState.LastKnowledgeVersion);
        Assert.Null(patchState.LastInstalledPatchVersion);
    }

    [Fact]
    public async Task HandleUpdateCheckResultAsync_FallsBackToExternalUrlForInstallerPackage()
    {
        var packageService = new FakePackageUpdateService
        {
            CanSelfApply = false
        };
        var ui = new FakeAppUpdateWorkflowUi
        {
            ConfirmResult = true
        };
        var coordinator = new AppUpdateWorkflowCoordinator(
            new FakeUpdateCheckStateService(),
            packageService,
            new FakePatchInstallationStateService(),
            ui);
        var manifest = new CloudUpdateManifest
        {
            LatestAppVersion = "2.2.0",
            LatestAppDownloadUrl = "https://updates.example.com/QuickReplyTool-Setup-v2.2.0.exe",
            LatestAppPackageKind = "installer_exe"
        };

        await coordinator.HandleUpdateCheckResultAsync(
            new AppSettings(),
            new UpdateCheckResult(true, false, "发现可用更新。", true, false, manifest));

        Assert.False(packageService.DownloadAndLaunchCalled);
        Assert.Equal(
            ["https://updates.example.com/QuickReplyTool-Setup-v2.2.0.exe"],
            ui.OpenedUrls);
        Assert.False(ui.PrepareForRestartCalled);
        Assert.False(ui.ShutdownCalled);
    }

    [Fact]
    public async Task HandlePendingPackageUpdateStatusAsync_MarksPatchInstallAndShowsNotification()
    {
        var packageService = new FakePackageUpdateService
        {
            PendingStatus = new PackageUpdateStatus(
                true,
                "2.2.1",
                "issue_patch",
                "ok",
                DateTimeOffset.Parse("2026-04-15T10:00:00+08:00"),
                "C:\\temp\\patch.log")
        };
        var patchState = new FakePatchInstallationStateService();
        var ui = new FakeAppUpdateWorkflowUi();
        var coordinator = new AppUpdateWorkflowCoordinator(
            new FakeUpdateCheckStateService(),
            packageService,
            patchState,
            ui);

        await coordinator.HandlePendingPackageUpdateStatusAsync();

        Assert.Equal("2.2.1", patchState.LastInstalledPatchVersion);
        Assert.Contains(ui.BackgroundNotifications, entry => entry.Title == "问题修复" && entry.Message.Contains("2.2.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleUpdateCheckResultAsync_KnowledgeUpdate_OpensUrlAndMarksSeen()
    {
        var updateState = new FakeUpdateCheckStateService();
        var ui = new FakeAppUpdateWorkflowUi
        {
            ConfirmResult = true
        };
        var coordinator = new AppUpdateWorkflowCoordinator(
            updateState,
            new FakePackageUpdateService(),
            new FakePatchInstallationStateService(),
            ui);
        var settings = new AppSettings
        {
            LastKnownCloudKnowledgeVersion = "knowledge-v1"
        };
        var manifest = new CloudUpdateManifest
        {
            KnowledgeVersion = "knowledge-v2",
            KnowledgePackageUrl = "https://updates.example.com/knowledge-v2.html",
            KnowledgeSummary = "新增高频回复分析"
        };

        await coordinator.HandleUpdateCheckResultAsync(
            settings,
            new UpdateCheckResult(true, false, "发现可用更新。", false, true, manifest));

        Assert.Equal("knowledge-v2", updateState.LastKnowledgeVersion);
        Assert.Same(settings, updateState.LastSettingsSnapshot);
        Assert.Equal(
            ["https://updates.example.com/knowledge-v2.html"],
            ui.OpenedUrls);
    }

    private sealed class FakeUpdateCheckStateService : IUpdateCheckStateService
    {
        public AppSettings? LastSettingsSnapshot { get; private set; }

        public string? LastKnowledgeVersion { get; private set; }

        public Task MarkKnowledgeVersionSeenAsync(AppSettings settingsSnapshot, string knowledgeVersion)
        {
            LastSettingsSnapshot = settingsSnapshot;
            LastKnowledgeVersion = knowledgeVersion;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePackageUpdateService : IPackageUpdateService
    {
        public bool CanSelfApply { get; set; }

        public bool DownloadAndLaunchCalled { get; private set; }

        public string? LastDownloadUrl { get; private set; }

        public string? LastVersionLabel { get; private set; }

        public string? LastSourceKind { get; private set; }

        public string? LastExpectedSha256 { get; private set; }

        public long? LastExpectedSizeBytes { get; private set; }

        public string? LastPackageKind { get; private set; }

        public PackageUpdateLaunchResult LaunchResult { get; set; } = new(true, true, "ok", string.Empty, string.Empty);

        public PackageUpdateStatus? PendingStatus { get; set; }

        public bool CanSelfApplyPackage(string packageUrl, string? packageKind = null)
        {
            LastDownloadUrl = packageUrl;
            LastPackageKind = packageKind;
            return CanSelfApply;
        }

        public Task<PackageUpdateLaunchResult> DownloadAndLaunchAsync(
            string packageUrl,
            string versionLabel,
            string sourceKind,
            string? expectedSha256 = null,
            long? expectedSizeBytes = null,
            string? packageKind = null,
            CancellationToken cancellationToken = default)
        {
            DownloadAndLaunchCalled = true;
            LastDownloadUrl = packageUrl;
            LastVersionLabel = versionLabel;
            LastSourceKind = sourceKind;
            LastExpectedSha256 = expectedSha256;
            LastExpectedSizeBytes = expectedSizeBytes;
            LastPackageKind = packageKind;
            return Task.FromResult(LaunchResult);
        }

        public PackageUpdateStatus? ConsumeLastStatus()
        {
            var status = PendingStatus;
            PendingStatus = null;
            return status;
        }
    }

    private sealed class FakePatchInstallationStateService : IPatchInstallationStateService
    {
        public string? LastInstalledPatchVersion { get; private set; }

        public Task MarkPatchVersionInstalledAsync(string patchVersion)
        {
            LastInstalledPatchVersion = patchVersion;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAppUpdateWorkflowUi : IAppUpdateWorkflowUi
    {
        public bool ConfirmResult { get; set; }

        public List<(string Title, string Message)> InfoMessages { get; } = [];

        public List<(string Title, string Message)> WarningMessages { get; } = [];

        public List<(string Title, string Message, bool IsWarning)> BackgroundNotifications { get; } = [];

        public List<string> OpenedUrls { get; } = [];

        public bool PrepareForRestartCalled { get; private set; }

        public bool ShutdownCalled { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message)
        {
            return Task.FromResult(ConfirmResult);
        }

        public Task ShowInfoAsync(string title, string message)
        {
            InfoMessages.Add((title, message));
            return Task.CompletedTask;
        }

        public Task ShowWarningAsync(string title, string message)
        {
            WarningMessages.Add((title, message));
            return Task.CompletedTask;
        }

        public Task ShowBackgroundNotificationAsync(string title, string message, bool isWarning = false)
        {
            BackgroundNotifications.Add((title, message, isWarning));
            return Task.CompletedTask;
        }

        public Task OpenExternalUrlAsync(string url)
        {
            OpenedUrls.Add(url);
            return Task.CompletedTask;
        }

        public void PrepareForRestart()
        {
            PrepareForRestartCalled = true;
        }

        public void Shutdown()
        {
            ShutdownCalled = true;
        }
    }
}
