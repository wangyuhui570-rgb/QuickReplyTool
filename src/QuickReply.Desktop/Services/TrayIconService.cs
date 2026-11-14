using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using QuickReply.Desktop;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Native;
using Forms = System.Windows.Forms;
using Wpf = System.Windows.Controls;
using WpfMedia = System.Windows.Media;
using WpfShapes = System.Windows.Shapes;

namespace QuickReply.Desktop.Services;

public sealed class TrayIconService : IDisposable
{
    private const string IconDataDisplay = "M1.5,3.5 C1.5,2.4 2.4,1.5 3.5,1.5 L12.5,1.5 C13.6,1.5 14.5,2.4 14.5,3.5 L14.5,10.5 C14.5,11.6 13.6,12.5 12.5,12.5 L1.5,12.5 C0.4,12.5 1.5,11.6 1.5,10.5 Z M5,14.5 L11,14.5 M8,12.5 L8,14.5";
    private const string IconDataSettings = "M8,5.5 A2.5,2.5 0 1,0 8,10.5 A2.5,2.5 0 1,0 8,5.5 M8,1.5 L8,3 M8,13 L8,14.5 M1.5,8 L3,8 M13,8 L14.5,8 M3.4,3.4 L4.5,4.5 M11.5,11.5 L12.6,12.6 M3.4,12.6 L4.5,11.5 M11.5,4.5 L12.6,3.4";
    private const string IconDataRestart = "M13.5,8 A5.5,5.5 0 1,1 12,4.1 L13.5,2.5 L13.5,6.5 L9.5,6.5";
    private const string IconDataUpdate = "M8,13.5 L8,2.5 M4,6.5 L8,2.5 L12,6.5 M2.5,13.5 L13.5,13.5";
    private const string IconDataIssue = "M3.5,2.5 L10,2.5 L13,5.5 L13,13.5 C13,14 12.5,14.5 12,14.5 L3.5,14.5 C3,14.5 2.5,14 2.5,13.5 L2.5,3.5 C2.5,3 3,2.5 3.5,2.5 Z M9.5,2.5 L9.5,6 L13,6 M5,9 L10.5,9 M5,11.5 L9,11.5";
    private const string IconDataAddNote = "M3,8 L13,8 M8,3 L8,13";
    private const string IconDataHistory = "M8,2.5 A5.5,5.5 0 1,0 8,13.5 A5.5,5.5 0 1,0 8,2.5 Z M8,5 L8,8 L10.5,9.5";
    private const string IconDataFolder = "M2,4.5 C2,3.9 2.5,3.5 3.1,3.5 L6,3.5 L7.5,5.5 L12.9,5.5 C13.5,5.5 14,5.9 14,6.5 L14,12 C14,12.6 13.5,13 12.9,13 L3.1,13 C2.5,13 2,12.6 2,12 Z";
    private const string IconDataExit = "M6,2.5 L11.5,2.5 C12.1,2.5 12.5,2.9 12.5,3.5 L12.5,12.5 C12.5,13.1 12.1,13.5 11.5,13.5 L6,13.5 M8.5,8 L2.5,8 M5.5,5 L2.5,8 L5.5,11";

    private Forms.NotifyIcon? _notifyIcon;
    private Wpf.ContextMenu? _trayContextMenu;
    private Window? _trayAnchorWindow;
    private Window? _mainWindow;
    private readonly IssueReportService _issueReportService = new();

    public void Start(Window mainWindow)
    {
        _mainWindow = mainWindow;

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = AppIdentity.DisplayName,
            Icon = LoadApplicationIcon(),
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
        _notifyIcon.MouseUp += OnNotifyIconMouseUp;
    }

    public void Stop()
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        if (_trayContextMenu is not null)
        {
            _trayContextMenu.IsOpen = false;
            _trayContextMenu = null;
        }

        if (_trayAnchorWindow is not null)
        {
            _trayAnchorWindow.Close();
            _trayAnchorWindow = null;
        }

        _mainWindow = null;
    }

    public void Dispose()
    {
        Stop();
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Dispatcher.Invoke(() =>
        {
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            if (!_mainWindow.IsVisible)
            {
                WindowTransitionService.ShowWithFade(_mainWindow);
            }

            var handle = new WindowInteropHelper(_mainWindow).Handle;
            if (handle != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
                NativeMethods.BringWindowToTop(handle);
                NativeMethods.SetForegroundWindow(handle);
            }

            _mainWindow.Activate();
        });
    }

    public void ShowNotification(string title, string text, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info, int timeoutMs = 1800)
    {
        if (_notifyIcon is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            _notifyIcon.BalloonTipTitle = string.IsNullOrWhiteSpace(title) ? AppIdentity.DisplayName : title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.BalloonTipIcon = icon;
            _notifyIcon.ShowBalloonTip(Math.Max(500, timeoutMs));
        }
        catch
        {
        }
    }

    private void OnNotifyIconMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Right)
        {
            return;
        }

        _mainWindow?.Dispatcher.BeginInvoke(() =>
        {
            if (_trayContextMenu is { IsOpen: true })
            {
                _trayContextMenu.IsOpen = false;
                return;
            }

            ShowTrayContextMenu();
        });
    }

    private void ShowTrayContextMenu()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Dispatcher.Invoke(() =>
        {
            _trayContextMenu = CreateWpfContextMenu();
            ActivateForeground();

            _trayContextMenu.Placement = Wpf.Primitives.PlacementMode.MousePoint;
            _trayContextMenu.PlacementTarget = _trayAnchorWindow ?? _mainWindow;
            _trayContextMenu.IsOpen = true;
        });
    }

    private void ActivateForeground()
    {
        if (_mainWindow is not null && _mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized)
        {
            var handle = new WindowInteropHelper(_mainWindow).Handle;
            if (handle != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(handle);
                return;
            }
        }

        EnsureAnchorWindow();
        if (_trayAnchorWindow is not null)
        {
            var handle = new WindowInteropHelper(_trayAnchorWindow).Handle;
            if (handle != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(handle);
            }
        }
    }

    private void EnsureAnchorWindow()
    {
        if (_trayAnchorWindow is not null)
        {
            return;
        }

        _trayAnchorWindow = new Window
        {
            Width = 1,
            Height = 1,
            Left = -10000,
            Top = -10000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            AllowsTransparency = true,
            Background = WpfMedia.Brushes.Transparent
        };
        _trayAnchorWindow.Show();
    }

    private Wpf.ContextMenu CreateWpfContextMenu()
    {
        var contextMenu = new Wpf.ContextMenu();
        if (System.Windows.Application.Current?.TryFindResource("AppleTrayContextMenuStyle") is Style trayStyle)
        {
            contextMenu.Style = trayStyle;
        }

        // 1. Header with branding and version badge
        contextMenu.Items.Add(CreateMenuHeader());
        contextMenu.Items.Add(new Wpf.Separator());

        // 2. Main actions
        contextMenu.Items.Add(CreateMenuItem("新建快捷短语", IconDataAddNote, (_, _) => OpenNewPhraseDialog()));
        contextMenu.Items.Add(CreateMenuItem("显示主界面", IconDataDisplay, (_, _) => ShowMainWindow()));
        contextMenu.Items.Add(CreateMenuItem("打开设置", IconDataSettings, (_, _) => OpenSettingsWindow()));

        contextMenu.Items.Add(new Wpf.Separator());

        // 3. Maintenance & updates
        contextMenu.Items.Add(CreateMenuItem("快速重启", IconDataRestart, (_, _) => RestartApplication()));
        contextMenu.Items.Add(CreateMenuItem($"检查更新（当前 {AppIdentity.Version}）", IconDataUpdate, (_, _) => App.Instance?.BeginUpdateCheck(userInitiated: true)));

        // 4. Issue report submenu
        var issueSubmenu = CreateMenuItem("问题记录与反馈", IconDataIssue, null);
        issueSubmenu.Items.Add(CreateMenuItem("记录当前问题", IconDataAddNote, (_, _) => OpenIssueReportDialog()));
        issueSubmenu.Items.Add(CreateMenuItem("问题记录历史", IconDataHistory, (_, _) => OpenIssueReportHistory()));
        issueSubmenu.Items.Add(CreateMenuItem("打开存储目录", IconDataFolder, (_, _) => OpenIssueReportDirectory()));
        contextMenu.Items.Add(issueSubmenu);

        contextMenu.Items.Add(new Wpf.Separator());

        // 5. Exit application (danger action)
        contextMenu.Items.Add(CreateMenuItem("退出程序", IconDataExit, (_, _) => ExitApplication(), isDanger: true));

        return contextMenu;
    }

    private static FrameworkElement CreateMenuHeader()
    {
        var grid = new Wpf.Grid();
        grid.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new Wpf.StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var appTitle = new Wpf.TextBlock
        {
            Text = AppIdentity.DisplayName,
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        appTitle.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "AppleTitleBrush");

        var subTitle = new Wpf.TextBlock
        {
            Text = "快捷回复效率工具",
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        subTitle.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "AppleMutedBrush");

        titleStack.Children.Add(appTitle);
        titleStack.Children.Add(subTitle);
        grid.Children.Add(titleStack);

        var badgeBorder = new Wpf.Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 6, 2),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
        badgeBorder.SetResourceReference(Wpf.Border.BackgroundProperty, "AppleBadgeBackgroundBrush");
        badgeBorder.SetResourceReference(Wpf.Border.BorderBrushProperty, "AppleBadgeBorderBrush");

        var versionText = new Wpf.TextBlock
        {
            Text = $"v{AppIdentity.Version}",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        versionText.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "AppleBadgeForegroundBrush");

        badgeBorder.Child = versionText;
        Wpf.Grid.SetColumn(badgeBorder, 1);
        grid.Children.Add(badgeBorder);

        return new Wpf.Border
        {
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(2, 2, 2, 4),
            Background = WpfMedia.Brushes.Transparent,
            Child = grid
        };
    }

    private static Wpf.MenuItem CreateMenuItem(string text, string iconData, RoutedEventHandler? onClick, bool isDanger = false)
    {
        var item = new Wpf.MenuItem
        {
            Header = text,
            Icon = CreateMenuIcon(iconData)
        };

        if (isDanger)
        {
            item.Tag = "Danger";
        }

        if (onClick is not null)
        {
            item.Click += onClick;
        }

        return item;
    }

    private static WpfShapes.Path CreateMenuIcon(string pathData)
    {
        var path = new WpfShapes.Path
        {
            Data = WpfMedia.Geometry.Parse(pathData),
            StrokeThickness = 1.3,
            StrokeStartLineCap = WpfMedia.PenLineCap.Round,
            StrokeEndLineCap = WpfMedia.PenLineCap.Round,
            StrokeLineJoin = WpfMedia.PenLineJoin.Round,
            Width = 16,
            Height = 16,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        path.SetBinding(WpfShapes.Path.StrokeProperty, new System.Windows.Data.Binding
        {
            Path = new PropertyPath("Foreground"),
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Wpf.MenuItem), 1)
        });

        return path;
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            var processPath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var icon = Icon.ExtractAssociatedIcon(processPath);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    private void OpenNewPhraseDialog()
    {
        if (_mainWindow is MainWindow mainWindow)
        {
            mainWindow.OpenAddPhraseDialog();
        }
    }

    private void OpenSettingsWindow()
    {
        if (_mainWindow is not MainWindow mainWindow)
        {
            ShowMainWindow();
            return;
        }

        _mainWindow.Dispatcher.Invoke(() =>
        {
            ShowMainWindow();
            mainWindow.OpenSettingsWindow();
        });
    }


    private void RestartApplication()
    {
        App.Instance?.PrepareForRestart();
        TryRestartProcess();
        ExitApplication();
    }


    private void OpenIssueReportDirectory()
    {
        try
        {
            var path = _issueReportService.GetIssueReportDirectoryPathAsync().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Directory.CreateDirectory(path);
            TryOpenDirectory(path);
        }
        catch (Exception ex)
        {
            Forms.MessageBox.Show($"打开问题目录失败：{ex.Message}", "问题记录", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
        }
    }

    private void OpenIssueReportHistory()
    {
        _mainWindow?.Dispatcher.Invoke(() =>
        {
            var window = new IssueReportHistoryWindow(_issueReportService, OpenIssueReportDirectory);
            if (_mainWindow?.IsVisible == true)
            {
                window.Owner = _mainWindow;
            }
            window.ShowDialog();
        });
    }



    private void OpenIssueReportDialog()
    {
        _mainWindow?.Dispatcher.Invoke(() =>
        {
            var window = new IssueReportDialogWindow(_issueReportService, TryOpenFileLocation);
            if (_mainWindow?.IsVisible == true)
            {
                window.Owner = _mainWindow;
            }
            var dialogResult = window.ShowDialog();
            if (dialogResult == true)
            {
                ShowNotification("问题记录", "问题记录已生成并保存。", Forms.ToolTipIcon.Info, 2600);
            }
        });
    }



    private void ExitApplication()
    {
        if (_mainWindow is null)
        {
            System.Windows.Application.Current.Shutdown();
            return;
        }

        _mainWindow.Dispatcher.Invoke(() =>
        {
            _mainWindow.Close();
        });
    }

    private static void TryRestartProcess()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        try
        {
            var currentProcessId = Environment.ProcessId;
            var forwardedArguments = BuildCommandLineArguments();
            var restartArguments = string.IsNullOrWhiteSpace(forwardedArguments)
                ? $"{App.RestartAfterPidArgument} {currentProcessId}"
                : $"{App.RestartAfterPidArgument} {currentProcessId} {forwardedArguments}";

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = restartArguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty
            };

            Process.Start(startInfo);
        }
        catch
        {
        }
    }

    private static void TryOpenFileLocation(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,{QuoteArgument(filePath)}",
                UseShellExecute = true
            };

            Process.Start(startInfo);
        }
        catch
        {
        }
    }

    private static void TryOpenDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directoryPath}\"",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private static void TryOpenExternalUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }




    private static string Shorten(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace(Environment.NewLine, " ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..Math.Max(0, maxLength - 1)]}…";
    }

    private static string BuildCommandLineArguments()
    {
        var args = App.StripInternalLaunchArguments(Environment.GetCommandLineArgs().Skip(1));
        if (args.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(" ", args.Select(QuoteArgument));
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return value.Contains('"') || value.Any(char.IsWhiteSpace)
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
    }
}

internal static class TrayIconServiceExtensions
{
    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
