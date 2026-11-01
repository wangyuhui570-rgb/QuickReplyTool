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
    private Lazy<ConversationLoggingService>? _conversationLoggingService;
    private SingleInstanceService? _singleInstanceService;
    private ExceptionLogService? _exceptionLogService;
    private ScreenshotToolService? _screenshotToolService;
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
        _conversationLoggingService = new Lazy<ConversationLoggingService>(() => new ConversationLoggingService());

        _mainViewModel = new MainViewModel(
            dataStore,
            messageComposeService,
            phraseExcelService,
            usageReportExcelService,
            personalConfigService,
            externalWorkbenchService,
            windowDockService,
            GetConversationLoggingService,
            _exceptionLogService);
        var searchOverlayViewModel = new SearchOverlayViewModel(_mainViewModel);
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
        _trayIconService.Start(mainWindow);
        StartupTrace.Mark("app.tray_started");
        _screenshotToolService = new ScreenshotToolService();
        _screenshotToolService.HealthIssue += message =>
            Dispatcher.BeginInvoke(() => ShowBackgroundNotification("截图工具", message, isWarning: true));
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
        _ = CheckForUpdatesAsync(userInitiated: false);
    }

    private bool _updateInProgress;
    private string? _dismissedUpdateVersion;

    /// <summary>Background update check. Silent on failure unless the user asked for it.</summary>
    public async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updateInProgress)
        {
            if (userInitiated)
            {
                System.Windows.MessageBox.Show("正在更新中，请稍候。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        try
        {
            if (!userInitiated)
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
            }

            var service = new AppUpdateService();
            var result = await Task.Run(() => service.CheckAsync(AppIdentity.Version));
            if (result.Status != AppUpdateCheckStatus.UpdateAvailable || result.Update is null)
            {
                if (userInitiated)
                {
                    System.Windows.MessageBox.Show(
                        result.Status == AppUpdateCheckStatus.UpToDate ? $"当前已是最新版本（{AppIdentity.Version}）。" : result.Message,
                        "检查更新",
                        MessageBoxButton.OK,
                        result.Status == AppUpdateCheckStatus.UpToDate ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
                return;
            }

            var update = result.Update;
            if (!userInitiated && string.Equals(_dismissedUpdateVersion, update.Version, StringComparison.Ordinal))
            {
                return;
            }

            var installRoot = AppUpdatePolicy.ResolveInstallRoot(AppContext.BaseDirectory);
            if (installRoot is null)
            {
                if (userInitiated)
                {
                    System.Windows.MessageBox.Show($"发现新版本 {update.Version}，但当前不是绿色版目录（app 文件夹）运行，无法自动更新。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            var sizeText = update.SizeBytes > 0 ? $"（约 {update.SizeBytes / 1024 / 1024} MB）" : string.Empty;
            var answer = System.Windows.MessageBox.Show(
                $"发现新版本 {update.Version}{sizeText}，当前版本 {AppIdentity.Version}。\n\n现在更新吗？\n会自动下载、替换并重启软件，设置和话术都不会受影响。\n选「否」稍后再说。",
                "发现新版本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
            {
                _dismissedUpdateVersion = update.Version;
                return;
            }

            _updateInProgress = true;
            ShowBackgroundNotification("软件更新", $"正在后台下载 {update.Version}，完成后会自动重启。");
            var lastNotify = DateTime.MinValue;
            var progress = new Progress<string>(message =>
            {
                if (DateTime.UtcNow - lastNotify > TimeSpan.FromSeconds(15))
                {
                    lastNotify = DateTime.UtcNow;
                    ShowBackgroundNotification("软件更新", message);
                }
            });
            var stagingRoot = await Task.Run(() => service.DownloadAndStageAsync(update, progress));
            service.LaunchUpdater(stagingRoot, installRoot, Environment.ProcessId);
            PrepareForRestart();
            Shutdown();
        }
        catch (Exception ex)
        {
            _updateInProgress = false;
            _exceptionLogService?.RecordException(ex, "app_update_failed", isTerminating: false);
            if (userInitiated || _dismissedUpdateVersion is null)
            {
                System.Windows.MessageBox.Show($"更新失败，旧版本不受影响，可稍后重试。\n\n{ex.Message}", "软件更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
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
        if (_conversationLoggingService?.IsValueCreated == true)
        {
            _conversationLoggingService.Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
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

    private ConversationLoggingService GetConversationLoggingService()
    {
        if (_conversationLoggingService is null)
        {
            throw new InvalidOperationException("Conversation logging service is not initialized.");
        }

        return _conversationLoggingService.Value;
    }
}
