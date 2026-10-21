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
