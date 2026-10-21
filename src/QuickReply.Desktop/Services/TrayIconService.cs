using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using QuickReply.Desktop;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Native;
using Forms = System.Windows.Forms;

namespace QuickReply.Desktop.Services;

public sealed class TrayIconService : IDisposable
{
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _contextMenu;
    private Window? _mainWindow;
    private readonly IssueReportService _issueReportService = new();
    public void Start(Window mainWindow)
    {
        _mainWindow = mainWindow;
        _contextMenu = CreateContextMenu();

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = AppIdentity.DisplayName,
            Icon = LoadApplicationIcon(),
            Visible = true,
            ContextMenuStrip = _contextMenu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    public void Stop()
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _contextMenu?.Dispose();
        _contextMenu = null;
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

    private Forms.ContextMenuStrip CreateContextMenu()
    {
        var contextMenu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Forms.Padding(4, 4, 4, 4),
            BackColor = Color.FromArgb(247, 247, 250),
            ForeColor = Color.FromArgb(28, 28, 30),
            Font = new System.Drawing.Font("Segoe UI Variable Text", 8.4f, System.Drawing.FontStyle.Regular),
            DropShadowEnabled = true,
            Renderer = new AppleTrayMenuRenderer()
        };

        contextMenu.Items.Add(CreateActionItem("显示主界面", (_, _) => ShowMainWindow()));
        contextMenu.Items.Add(CreateActionItem("打开设置", (_, _) => OpenSettingsWindow()));
        contextMenu.Items.Add(CreateActionItem("问题记录", (_, _) => OpenIssueReportDialog()));
        contextMenu.Items.Add(CreateActionItem("问题记录历史", (_, _) => OpenIssueReportHistory()));
        contextMenu.Items.Add(CreateActionItem("打开问题目录", (_, _) => OpenIssueReportDirectory()));
        contextMenu.Items.Add(CreateActionItem("快速重启", (_, _) => RestartApplication()));
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add(CreateActionItem("退出程序", (_, _) => ExitApplication(), isDanger: true));
        return contextMenu;
    }

    private static Forms.ToolStripMenuItem CreateActionItem(string text, EventHandler onClick, bool isDanger = false)
    {
        return new Forms.ToolStripMenuItem(text)
        {
            AutoSize = false,
            Size = new System.Drawing.Size(96, 22),
            Margin = new Forms.Padding(1, 1, 1, 1),
            Padding = new Forms.Padding(6, 0, 6, 0),
            ForeColor = isDanger ? Color.FromArgb(198, 40, 40) : Color.FromArgb(28, 28, 30)
        }.Also(item => item.Click += onClick);
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
        IReadOnlyList<IssueReportIndexEntry> entries;
        try
        {
            entries = _issueReportService.LoadRecentIssueEntriesAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Forms.MessageBox.Show($"读取问题记录失败：{ex.Message}", "问题记录历史", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new Forms.Form
        {
            Text = "问题记录历史",
            StartPosition = Forms.FormStartPosition.CenterScreen,
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Color.FromArgb(248, 248, 250),
            ClientSize = new System.Drawing.Size(720, 420),
            Font = new System.Drawing.Font("Segoe UI Variable Text", 9.5f, System.Drawing.FontStyle.Regular)
        };

        var listView = new Forms.ListView
        {
            Location = new System.Drawing.Point(20, 20),
            Size = new System.Drawing.Size(680, 300),
            View = Forms.View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            GridLines = true
        };
        listView.Columns.Add("时间", 160);
        listView.Columns.Add("问题摘要", 360);
        listView.Columns.Add("本地文件", 140);

        foreach (var entry in entries)
        {
            var item = new Forms.ListViewItem(entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            item.SubItems.Add(Truncate(entry.UserDescription, 48));
            item.SubItems.Add(string.IsNullOrWhiteSpace(entry.FilePath) ? "-" : "已保存");
            item.Tag = entry;
            listView.Items.Add(item);
        }

        var detailTextBox = new Forms.TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = Forms.ScrollBars.Vertical,
            BorderStyle = Forms.BorderStyle.FixedSingle,
            Location = new System.Drawing.Point(20, 330),
            Size = new System.Drawing.Size(520, 70),
            BackColor = Color.White,
            ForeColor = Color.FromArgb(28, 28, 30),
            Text = entries.Count == 0 ? "当前还没有问题记录。" : "选择一条问题记录后查看本地详情。"
        };

        listView.SelectedIndexChanged += (_, _) =>
        {
            if (listView.SelectedItems.Count == 0 || listView.SelectedItems[0].Tag is not IssueReportIndexEntry entry)
            {
                detailTextBox.Text = "选择一条问题记录后查看本地详情。";
                return;
            }

            detailTextBox.Text = BuildIssueEntryDetails(entry);
        };

        var openDirectoryButton = new Forms.Button
        {
            Text = "打开目录",
            Location = new System.Drawing.Point(550, 330),
            Size = new System.Drawing.Size(150, 34),
            FlatStyle = Forms.FlatStyle.Flat,
            BackColor = Color.FromArgb(18, 108, 240),
            ForeColor = Color.White
        };
        openDirectoryButton.FlatAppearance.BorderSize = 0;
        openDirectoryButton.Click += (_, _) => OpenIssueReportDirectory();

        var closeButton = new Forms.Button
        {
            Text = "关闭",
            Location = new System.Drawing.Point(550, 370),
            Size = new System.Drawing.Size(150, 34),
            FlatStyle = Forms.FlatStyle.Flat,
            BackColor = Color.FromArgb(234, 236, 241),
            ForeColor = Color.FromArgb(28, 28, 30)
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.Click += (_, _) => dialog.Close();

        dialog.Controls.Add(listView);
        dialog.Controls.Add(detailTextBox);
        dialog.Controls.Add(openDirectoryButton);
        dialog.Controls.Add(closeButton);
        dialog.ShowDialog();
    }

    private void OpenIssueReportDialog()
    {
        using var dialog = new Forms.Form
        {
            Text = "问题记录",
            StartPosition = Forms.FormStartPosition.CenterScreen,
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Color.FromArgb(248, 248, 250),
            ClientSize = new System.Drawing.Size(540, 340),
            Font = new System.Drawing.Font("Segoe UI Variable Text", 9.5f, System.Drawing.FontStyle.Regular)
        };

        var titleLabel = new Forms.Label
        {
            Text = "记录当前遇到的问题",
            AutoSize = false,
            Location = new System.Drawing.Point(20, 18),
            Size = new System.Drawing.Size(480, 26),
            Font = new System.Drawing.Font("Segoe UI Variable Text", 12f, System.Drawing.FontStyle.Bold),
            ForeColor = Color.FromArgb(24, 24, 27)
        };

        var hintLabel = new Forms.Label
        {
            Text = "写下你刚刚遇到的问题。程序会自动附带时间、最近日志和当前设置，在本地生成反馈文件和问题包（不再上传到服务器）。",
            AutoSize = false,
            Location = new System.Drawing.Point(20, 48),
            Size = new System.Drawing.Size(500, 42),
            ForeColor = Color.FromArgb(96, 96, 102)
        };

        var issueTextBox = new Forms.TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = Forms.ScrollBars.Vertical,
            BorderStyle = Forms.BorderStyle.FixedSingle,
            Location = new System.Drawing.Point(20, 98),
            Size = new System.Drawing.Size(500, 170),
            BackColor = Color.White,
            ForeColor = Color.FromArgb(28, 28, 30)
        };

        var generateButton = new Forms.Button
        {
            Text = "生成并上传",
            Location = new System.Drawing.Point(392, 286),
            Size = new System.Drawing.Size(128, 34),
            FlatStyle = Forms.FlatStyle.Flat,
            BackColor = Color.FromArgb(18, 108, 240),
            ForeColor = Color.White
        };
        generateButton.FlatAppearance.BorderSize = 0;

        var cancelButton = new Forms.Button
        {
            Text = "取消",
            Location = new System.Drawing.Point(296, 286),
            Size = new System.Drawing.Size(86, 34),
            FlatStyle = Forms.FlatStyle.Flat,
            BackColor = Color.FromArgb(234, 236, 241),
            ForeColor = Color.FromArgb(28, 28, 30)
        };
        cancelButton.FlatAppearance.BorderSize = 0;

        cancelButton.Click += (_, _) => dialog.Close();
        generateButton.Click += (_, _) =>
        {
            generateButton.Enabled = false;
            try
            {
                var result = _issueReportService.CreateReportAsync(issueTextBox.Text).GetAwaiter().GetResult();
                if (!result.Success)
                {
                    Forms.MessageBox.Show(dialog, result.Message, "问题记录", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                    return;
                }

                TryOpenFileLocation(result.FilePath);
                ShowNotification("问题记录", result.Message, result.UploadAttempted && !result.UploadSucceeded ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info, 2600);
                if (result.UploadAttempted && !result.UploadSucceeded)
                {
                    Forms.MessageBox.Show(dialog, result.Message, "问题记录", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                }
                dialog.DialogResult = Forms.DialogResult.OK;
                dialog.Close();
            }
            finally
            {
                generateButton.Enabled = true;
            }
        };

        dialog.Controls.Add(titleLabel);
        dialog.Controls.Add(hintLabel);
        dialog.Controls.Add(issueTextBox);
        dialog.Controls.Add(cancelButton);
        dialog.Controls.Add(generateButton);
        dialog.AcceptButton = generateButton;
        dialog.CancelButton = cancelButton;
        dialog.ShowDialog();
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


    private static string Truncate(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(0, maxLength - 1)] + "…";
    }

    private static string BuildIssueEntryDetails(IssueReportIndexEntry entry)
    {
        var lines = new List<string>
        {
            $"问题ID：{(string.IsNullOrWhiteSpace(entry.ServerIssueId) ? entry.IssueId : entry.ServerIssueId)}",
            $"远端状态：{(string.IsNullOrWhiteSpace(entry.RemoteStatus) ? "-" : entry.RemoteStatus)}",
            $"分析状态：{(string.IsNullOrWhiteSpace(entry.AnalysisStatus) ? "-" : entry.AnalysisStatus)}",
            $"补丁版本：{(string.IsNullOrWhiteSpace(entry.PatchVersion) ? "-" : entry.PatchVersion)}",
            $"补丁状态：{GetPatchInstallStatus(entry)}"
        };

        if (!string.IsNullOrWhiteSpace(entry.PatchDownloadUrl))
        {
            lines.Add($"补丁地址：{entry.PatchDownloadUrl}");
        }

        if (!string.IsNullOrWhiteSpace(entry.PatchReleaseNotes))
        {
            lines.Add(string.Empty);
            lines.Add("补丁说明：");
            lines.Add(entry.PatchReleaseNotes.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(entry.UploadMessage))
        {
            lines.Add(string.Empty);
            lines.Add($"提示：{entry.UploadMessage.Trim()}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string GetPatchInstallStatus(IssueReportIndexEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.PatchVersion))
        {
            return "无";
        }

        return string.Equals(entry.PatchVersion, entry.InstalledPatchVersion, StringComparison.OrdinalIgnoreCase)
            ? "已安装"
            : "待安装";
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

    private sealed class AppleTrayMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public AppleTrayMenuRenderer() : base(new AppleTrayMenuColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = CreateRoundedRectangle(e.AffectedBounds, 16);
            using var brush = new SolidBrush(Color.FromArgb(248, 248, 250));
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var borderBounds = Rectangle.Inflate(e.AffectedBounds, -1, -1);
            using var path = CreateRoundedRectangle(borderBounds, 16);
            using var pen = new Pen(Color.FromArgb(224, 224, 230));
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not Forms.ToolStripMenuItem)
            {
                base.OnRenderMenuItemBackground(e);
                return;
            }

            var bounds = new Rectangle(System.Drawing.Point.Empty, e.Item.Size);
            var itemBounds = Rectangle.Inflate(bounds, -4, -1);
            var isSelected = e.Item.Selected;
            var backColor = isSelected ? Color.FromArgb(234, 236, 241) : Color.Transparent;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = CreateRoundedRectangle(itemBounds, 12);
            using var brush = new SolidBrush(backColor);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.ContentRectangle.Top + (e.Item.ContentRectangle.Height / 2);
            using var pen = new Pen(Color.FromArgb(228, 228, 232));
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
        {
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2;

            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class AppleTrayMenuColorTable : Forms.ProfessionalColorTable
    {
        public override Color MenuBorder => Color.FromArgb(224, 224, 230);
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Color.FromArgb(234, 236, 241);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(234, 236, 241);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(234, 236, 241);
        public override Color ToolStripDropDownBackground => Color.FromArgb(248, 248, 250);
        public override Color ImageMarginGradientBegin => Color.FromArgb(248, 248, 250);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(248, 248, 250);
        public override Color ImageMarginGradientEnd => Color.FromArgb(248, 248, 250);
        public override Color SeparatorDark => Color.FromArgb(228, 228, 232);
        public override Color SeparatorLight => Color.FromArgb(228, 228, 232);
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
