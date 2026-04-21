using System.Windows;
using QuickReply.Desktop.Models;
using System.Diagnostics;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop;

public partial class App : System.Windows.Application
{
    internal const string RestartAfterPidArgument = "--restart-after";

    private MainViewModel? _mainViewModel;
    private TrayIconService? _trayIconService;
    private ConversationLoggingService? _conversationLoggingService;
    private SingleInstanceService? _singleInstanceService;
    private ExceptionLogService? _exceptionLogService;
    private ScreenshotToolService? _screenshotToolService;
    private UpdateCheckService? _updateCheckService;
    private IssueReportService? _issueReportService;
    private SelfUpdateService? _selfUpdateService;
    private AppUpdateWorkflowCoordinator? _appUpdateWorkflowCoordinator;
    private TrayUpdateCheckCoordinator? _trayUpdateCheckCoordinator;
    private DesktopRuntimeCoordinator? _runtimeCoordinator;
    private bool _restartRequested;

    public static App? Instance => Current as App;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var launchArgs = StripInternalLaunchArguments(e.Args ?? []);
        WaitForRestartedParentExitIfNeeded(e.Args ?? []);
        StartupTrace.Reset();
        StartupTrace.Mark("app.start");

        _exceptionLogService = new ExceptionLogService();
        DispatcherUnhandledException += App_OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_OnUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_OnUnobservedTaskException;
        StartupTrace.Mark("app.handlers_registered");

        var startupRegistrationService = new StartupRegistrationService();
        var dataStore = new SqliteAppDataStore();
        AppSettings startupSettings;
        try
        {
            startupSettings = await dataStore.LoadSettingsOnlyAsync();
            RetiredFeatureSettingsPolicy.Apply(startupSettings);
            StartupTrace.Mark("app.settings_loaded");

            if (startupSettings.ScreenshotHotkeyMigrationTouched)
            {
                await dataStore.UpdateSettingsAsync(startupSettings);
                StartupTrace.Mark("app.settings_migrated");
            }
        }
        catch
        {
            startupSettings = new AppSettings();
            StartupTrace.Mark("app.settings_load_failed_fallback");
        }

        var activationResult = startupRegistrationService.EnsureConfiguredLaunch(startupSettings.RunAsAdministrator);
        if (activationResult.Relaunched)
        {
            StartupTrace.Mark("app.relaunched_for_admin");
            Shutdown();
            return;
        }

        if (!string.IsNullOrWhiteSpace(activationResult.Message))
        {
            System.Windows.MessageBox.Show(
                activationResult.Message,
                "管理员启动",
                MessageBoxButton.OK,
                activationResult.UserCancelled ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        _singleInstanceService = new SingleInstanceService();
        if (!_singleInstanceService.EnsureSingleInstance(ActivatePrimaryInstance))
        {
            StartupTrace.Mark("app.single_instance_exit");
            Shutdown();
            return;
        }
        StartupTrace.Mark("app.single_instance_ready");

        var windowTracker = new ForegroundWindowTracker();
        IExternalWorkbenchService externalWorkbenchService = windowTracker;
        var windowDockService = new WindowDockService();
        var globalSearchFocusService = new GlobalSearchFocusService();
        _trayIconService = new TrayIconService();
        var clipboardService = new ClipboardService();
        var keyboardInputSimulator = new KeyboardInputSimulator();
        var phraseExcelService = new PhraseExcelService();
        var usageReportExcelService = new UsageReportExcelService();
        var personalConfigService = new PersonalConfigService();
        var messageComposeService = new MessageComposeService(externalWorkbenchService, clipboardService, keyboardInputSimulator);
        _conversationLoggingService = new ConversationLoggingService();
        _updateCheckService = new UpdateCheckService(dataStore, _conversationLoggingService);
        _issueReportService = new IssueReportService(dataStore, _conversationLoggingService);
        _selfUpdateService = new SelfUpdateService(dataStore);
        _appUpdateWorkflowCoordinator = new AppUpdateWorkflowCoordinator(
            _updateCheckService,
            _selfUpdateService,
            _issueReportService,
            new AppUpdateWorkflowUiAdapter(this));
        _trayUpdateCheckCoordinator = new TrayUpdateCheckCoordinator(
            cancellationToken =>
            {
                var settingsSnapshot = _mainViewModel?.CreateSettingsSnapshot() ?? startupSettings;
                return CheckForUpdatesNowAsync(settingsSnapshot, force: true, showUpdatePrompt: true, cancellationToken);
            },
            (title, message, icon, timeoutMs) => Dispatcher.Invoke(() =>
                _trayIconService?.ShowNotification(title, message, icon, timeoutMs)));

        _mainViewModel = new MainViewModel(
            dataStore,
            messageComposeService,
            phraseExcelService,
            usageReportExcelService,
            personalConfigService,
            externalWorkbenchService,
            windowDockService,
            _conversationLoggingService,
            _exceptionLogService);
        _runtimeCoordinator = new DesktopRuntimeCoordinator(
            () => _mainViewModel?.CreateSettingsSnapshot() ?? startupSettings,
            (settingsSnapshot, force, cancellationToken) => Task.FromResult(
                new KnowledgeSyncRunResult(true, true, "云同步已停用。", 0, 0, 0, 0, 0, 0, 0)),
            (settingsSnapshot, force, cancellationToken) =>
            {
                if (_updateCheckService is null)
                {
                    return Task.FromResult(new UpdateCheckResult(false, false, "更新检查服务尚未初始化。", false, false, null));
                }

                return _updateCheckService.CheckForUpdatesAsync(settingsSnapshot, force, cancellationToken);
            },
            (settingsSnapshot, result) =>
            {
                if (_appUpdateWorkflowCoordinator is null || !result.Success || result.Manifest is null)
                {
                    return Task.CompletedTask;
                }

                return _appUpdateWorkflowCoordinator.HandleUpdateCheckResultAsync(settingsSnapshot, result);
            },
            RefreshIssueStatusesAndNotifyAsync,
            async (title, message, isWarning) =>
            {
                await Dispatcher.InvokeAsync(() =>
                    ShowBackgroundNotification(title, message, isWarning));
            });
        var manualSearchOnlyServices = new ManualSearchOnlyRecommendationServices();
        var searchOverlayViewModel = new SearchOverlayViewModel(
            _mainViewModel,
            manualSearchOnlyServices,
            manualSearchOnlyServices);
        var searchOverlayWindow = new SearchOverlayWindow(externalWorkbenchService, searchOverlayViewModel);
        StartupTrace.Mark("app.windows_constructed");

        var mainWindow = new MainWindow(_mainViewModel, globalSearchFocusService, searchOverlayWindow);
        mainWindow.Title = AppIdentity.DisplayName;
        MainWindow = mainWindow;
        mainWindow.Show();
        StartupTrace.Mark("app.main_window_shown");
        searchOverlayWindow.Owner = mainWindow;

        await _mainViewModel.InitializeAsync(mainWindow);
        StartupTrace.Mark("app.main_view_model_initialized");
        _trayIconService.Start(mainWindow, _trayUpdateCheckCoordinator);
        StartupTrace.Mark("app.tray_started");
        if (_appUpdateWorkflowCoordinator is not null)
        {
            await _appUpdateWorkflowCoordinator.HandlePendingPackageUpdateStatusAsync().ConfigureAwait(false);
        }
        _screenshotToolService = new ScreenshotToolService();
        RefreshScreenshotToolConfiguration(showNotificationOnFailure: true);
        StartupTrace.Mark("app.screenshot_tool_checked");

        if (await _mainViewModel.ShouldShowFirstRunGuideAsync())
        {
            StartupTrace.Mark("app.first_run_guide_showing");
            var guideWindow = new FirstRunGuideWindow(
                _mainViewModel.Settings.SearchFocusHotkeyMode,
                _mainViewModel.Settings.SendHotkeyMode,
                _mainViewModel.Settings.FileLibraryFolderPath)
            {
                Owner = mainWindow
            };

            guideWindow.ShowDialog();
            await _mainViewModel.CompleteFirstRunGuideAsync(
                guideWindow.SelectedSearchHotkeyMode,
                guideWindow.SelectedSendHotkeyMode,
                guideWindow.SelectedFileLibraryFolderPath,
                completed: true);
            StartupTrace.Mark("app.first_run_guide_completed");
        }

        StartupTrace.Mark("app.startup_completed");
        _ = RunBackgroundStartupTasksAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceService?.Dispose();
        _trayIconService?.Stop();
        if (!_restartRequested)
        {
            _screenshotToolService?.Dispose();
        }
        _mainViewModel?.ShutdownAsync().GetAwaiter().GetResult();
        _conversationLoggingService?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        DispatcherUnhandledException -= App_OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_OnUnobservedTaskException;
        base.OnExit(e);
    }

    private void ActivatePrimaryInstance()
    {
        Dispatcher.Invoke(() =>
        {
            if (_trayIconService is not null)
            {
                _trayIconService.ShowMainWindow();
                return;
            }

            if (MainWindow is null)
            {
                return;
            }

            if (MainWindow.WindowState == WindowState.Minimized)
            {
                MainWindow.WindowState = WindowState.Normal;
            }

            if (!MainWindow.IsVisible)
            {
                WindowTransitionService.ShowWithFade(MainWindow);
            }

            MainWindow.Activate();
        });
    }

    public void ShowBackgroundNotification(string title, string message, bool isWarning = false)
    {
        _trayIconService?.ShowNotification(
            title,
            message,
            isWarning ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
    }

    public Task<PackageUpdateLaunchResult> ApplyIssuePatchUpdateAsync(IssueReportIndexEntry entry)
    {
        if (_appUpdateWorkflowCoordinator is null)
        {
            return Task.FromResult(new PackageUpdateLaunchResult(false, false, "自动更新流程尚未初始化。", string.Empty, string.Empty));
        }

        return _appUpdateWorkflowCoordinator.ApplyIssuePatchUpdateAsync(entry);
    }

    public async Task<KnowledgeSyncRunResult> RunCloudSyncNowAsync(
        AppSettings settingsSnapshot,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (_runtimeCoordinator is null)
        {
            return new KnowledgeSyncRunResult(false, false, "云同步服务尚未初始化。", 0, 0, 0, 0, 0, 0, 0);
        }

        return await _runtimeCoordinator.RunCloudSyncNowAsync(settingsSnapshot, force, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateCheckResult> CheckForUpdatesNowAsync(
        AppSettings settingsSnapshot,
        bool force = false,
        bool showUpdatePrompt = true,
        CancellationToken cancellationToken = default)
    {
        if (_runtimeCoordinator is null)
        {
            return new UpdateCheckResult(false, false, "更新检查服务尚未初始化。", false, false, null);
        }

        return await _runtimeCoordinator
            .CheckForUpdatesNowAsync(settingsSnapshot, force, showUpdatePrompt, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunBackgroundStartupTasksAsync()
    {
        if (_runtimeCoordinator is null)
        {
            return;
        }

        await _runtimeCoordinator.RunBackgroundStartupTasksAsync().ConfigureAwait(false);
    }

    private async Task RefreshIssueStatusesAndNotifyAsync()
    {
        if (_issueReportService is null)
        {
            return;
        }

        var refreshResult = await _issueReportService.RefreshIssueStatusesAsync().ConfigureAwait(false);
        if (!refreshResult.Success)
        {
            await Dispatcher.InvokeAsync(() =>
                ShowBackgroundNotification("问题记录", refreshResult.Message, isWarning: true));
            return;
        }

        if (refreshResult.UpdatedCount <= 0)
        {
            return;
        }

        var entries = await _issueReportService.LoadRecentIssueEntriesAsync().ConfigureAwait(false);
        var patchReadyEntries = entries
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.PatchVersion) &&
                !string.Equals(entry.PatchVersion, entry.InstalledPatchVersion, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.CreatedAt)
            .Take(3)
            .ToArray();

        if (patchReadyEntries.Length > 0)
        {
            var latestPatchEntry = patchReadyEntries[0];
            var latestPatchVersion = latestPatchEntry.PatchVersion;
            var canSelfApplyPatch = _selfUpdateService?.CanSelfApplyPackage(latestPatchEntry.PatchDownloadUrl) == true;

            await Dispatcher.InvokeAsync(() =>
            {
                var message = patchReadyEntries.Length == 1
                    ? $"你提交的问题已有修复版本 {latestPatchVersion}。"
                    : $"已有 {patchReadyEntries.Length} 条问题关联到修复版本，最新为 {latestPatchVersion}。";

                ShowBackgroundNotification("问题修复", message);

                if (string.IsNullOrWhiteSpace(latestPatchEntry.PatchDownloadUrl))
                {
                    return;
                }

                var prompt = $"检测到问题修复补丁：{latestPatchVersion}{Environment.NewLine}{Environment.NewLine}";
                if (!string.IsNullOrWhiteSpace(latestPatchEntry.PatchReleaseNotes))
                {
                    prompt += $"{latestPatchEntry.PatchReleaseNotes.Trim()}{Environment.NewLine}{Environment.NewLine}";
                }

                prompt += canSelfApplyPatch
                    ? "是否现在下载并自动安装补丁？"
                    : "是否现在打开补丁下载地址？";
                var result = System.Windows.MessageBox.Show(
                    prompt,
                    "问题修复",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    _ = ApplyIssuePatchUpdateAsync(latestPatchEntry);
                }
            });
            return;
        }

        var analysisReadyCount = entries.Count(entry =>
            !string.IsNullOrWhiteSpace(entry.AnalysisStatus) &&
            !string.Equals(entry.AnalysisStatus, "reported", StringComparison.OrdinalIgnoreCase));

        if (analysisReadyCount > 0)
        {
            await Dispatcher.InvokeAsync(() =>
                ShowBackgroundNotification("问题记录", $"已刷新 {analysisReadyCount} 条问题状态，可在托盘“问题记录历史”里查看。"));
        }
    }

    public void RefreshScreenshotToolConfiguration(bool showNotificationOnFailure)
    {
        if (_mainViewModel is null || _screenshotToolService is null)
        {
            return;
        }

        var result = _screenshotToolService.ApplyConfiguration(new ScreenshotToolConfiguration(
            _mainViewModel.Settings.LaunchScreenshotToolWithApp,
            _mainViewModel.Settings.ScreenshotToolHotkey,
            _mainViewModel.Settings.ScreenshotToolSaveDirectory));
        if (showNotificationOnFailure && !result.Success && !string.IsNullOrWhiteSpace(result.Message))
        {
            ShowBackgroundNotification("截图工具", result.Message, isWarning: true);
        }
    }

    public void MarkRestartRequested()
    {
        _restartRequested = true;
    }

    public void PrepareForRestart()
    {
        _restartRequested = true;
        _trayIconService?.Stop();
        _trayIconService = null;
        _singleInstanceService?.Dispose();
        _singleInstanceService = null;
    }

    internal static string[] StripInternalLaunchArguments(IEnumerable<string> args)
    {
        var source = args?.ToArray() ?? [];
        if (source.Length == 0)
        {
            return [];
        }

        var filtered = new List<string>(source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            if (string.Equals(source[index], RestartAfterPidArgument, StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            filtered.Add(source[index]);
        }

        return filtered.ToArray();
    }

    private static void WaitForRestartedParentExitIfNeeded(IReadOnlyList<string> startupArgs)
    {
        if (!TryGetRestartAfterPid(startupArgs, out var parentProcessId))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(parentProcessId);
            process.WaitForExit(1200);
        }
        catch
        {
        }
    }

    private static bool TryGetRestartAfterPid(IReadOnlyList<string> startupArgs, out int parentProcessId)
    {
        for (var index = 0; index < startupArgs.Count - 1; index++)
        {
            if (!string.Equals(startupArgs[index], RestartAfterPidArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(startupArgs[index + 1], out parentProcessId) && parentProcessId > 0)
            {
                return true;
            }
        }

        parentProcessId = 0;
        return false;
    }

    private void App_OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _exceptionLogService?.RecordException(e.Exception, "dispatcher_unhandled_exception", isTerminating: true);

        System.Windows.MessageBox.Show(
            "程序遇到未处理异常，已写入异常日志。请重启程序后继续使用。",
            "程序异常",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
        Shutdown(-1);
    }

    private void CurrentDomain_OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _exceptionLogService?.RecordException(exception, "appdomain_unhandled_exception", isTerminating: e.IsTerminating);
            return;
        }

        _exceptionLogService?.RecordException(
            new Exception(e.ExceptionObject?.ToString() ?? "未知异常"),
            "appdomain_unhandled_exception",
            isTerminating: e.IsTerminating);
    }

    private void TaskScheduler_OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _exceptionLogService?.RecordException(e.Exception, "task_scheduler_unobserved_exception", isTerminating: false);
        e.SetObserved();
    }
}
