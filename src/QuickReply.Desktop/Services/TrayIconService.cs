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
    private TrayUpdateCheckCoordinator? _updateCheckCoordinator;

    public void Start(Window mainWindow, TrayUpdateCheckCoordinator? updateCheckCoordinator = null)
    {
        _mainWindow = mainWindow;
        _updateCheckCoordinator = updateCheckCoordinator;
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
        _updateCheckCoordinator = null;
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
        contextMenu.Items.Add(CreateActionItem("检查更新", (_, _) => BeginCheckUpdates()));
        contextMenu.Items.Add(CreateActionItem("问题记录", (_, _) => OpenIssueReportDialog()));
        contextMenu.Items.Add(CreateActionItem("问题记录历史", (_, _) => OpenIssueReportHistory()));
        contextMenu.Items.Add(CreateActionItem("刷新问题状态", (_, _) => RefreshIssueStatuses()));
        contextMenu.Items.Add(CreateActionItem("重试问题上传", (_, _) => RetryPendingIssueUploads()));
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

    private async void BeginCheckUpdates()
    {
        if (_updateCheckCoordinator is null)
        {
            ShowNotification("更新检查", "更新检查服务尚未初始化。", Forms.ToolTipIcon.Warning, 2200);
            return;
        }

        await _updateCheckCoordinator.RunAsync().ConfigureAwait(false);
    }

    private void RestartApplication()
    {
        App.Instance?.PrepareForRestart();
        TryRestartProcess();
        ExitApplication();
    }

    // 网络操作期间防止重复点击；托盘菜单事件运行在 UI 线程，原来同步等待网络会让整个界面卡死。
    private bool _issueNetworkOperationRunning;

    private async void RetryPendingIssueUploads()
    {
        if (_issueNetworkOperationRunning)
        {
            ShowNotification("问题记录", "上一个问题记录操作仍在进行，请稍候。", Forms.ToolTipIcon.Info, 2000);
            return;
        }

        _issueNetworkOperationRunning = true;
        try
        {
            var result = await _issueReportService.RetryPendingUploadsAsync();
            ShowNotification("问题记录", result.Message, result.Success ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning, 2600);
            if (!result.Success)
            {
                Forms.MessageBox.Show(result.Message, "问题记录", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            var message = $"重试问题包上传失败：{ex.Message}";
            ShowNotification("问题记录", message, Forms.ToolTipIcon.Warning, 2600);
            Forms.MessageBox.Show(message, "问题记录", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
        }
        finally
        {
            _issueNetworkOperationRunning = false;
        }
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
            Forms.MessageBox.Show($"读取问题记录历史失败：{ex.Message}", "问题记录", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
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
            ClientSize = new System.Drawing.Size(920, 520),
            Font = new System.Drawing.Font("Segoe UI Variable Text", 9.2f, System.Drawing.FontStyle.Regular)
        };

        var listView = new Forms.ListView
        {
            Location = new System.Drawing.Point(18, 18),
            Size = new System.Drawing.Size(884, 358),
            View = Forms.View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false
        };
        listView.Columns.Add("时间", 135);
        listView.Columns.Add("上传", 74);
        listView.Columns.Add("服务器问题ID", 150);
        listView.Columns.Add("远端状态", 96);
        listView.Columns.Add("分析状态", 110);
        listView.Columns.Add("补丁版本", 96);
        listView.Columns.Add("问题描述", 250);
        listView.Columns.Add("上传结果", 250);

        foreach (var entry in entries.OrderByDescending(item => item.CreatedAt))
        {
            var item = new Forms.ListViewItem(entry.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"))
            {
                Tag = entry
            };
            item.SubItems.Add(entry.UploadSucceeded ? "成功" : entry.UploadAttempted ? "失败" : "未上传");
            item.SubItems.Add(string.IsNullOrWhiteSpace(entry.ServerIssueId) ? "-" : entry.ServerIssueId);
            item.SubItems.Add(string.IsNullOrWhiteSpace(entry.RemoteStatus) ? "-" : entry.RemoteStatus);
            item.SubItems.Add(string.IsNullOrWhiteSpace(entry.AnalysisStatus) ? "-" : entry.AnalysisStatus);
            item.SubItems.Add(string.IsNullOrWhiteSpace(entry.PatchVersion) ? "-" : entry.PatchVersion);
            item.SubItems.Add(Shorten(entry.UserDescription, 70));
            item.SubItems.Add(Shorten(entry.UploadMessage, 90));
            listView.Items.Add(item);
        }

        var detailTextBox = new Forms.TextBox
        {
            Location = new System.Drawing.Point(18, 386),
            Size = new System.Drawing.Size(884, 62),
            ReadOnly = true,
            Multiline = true,
            ScrollBars = Forms.ScrollBars.Vertical,
            BorderStyle = Forms.BorderStyle.FixedSingle,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(28, 28, 30)
        };

        var openDirectoryButton = new Forms.Button
        {
            Text = "打开问题目录",
            Location = new System.Drawing.Point(18, 465),
            Size = new System.Drawing.Size(118, 34),
            FlatStyle = Forms.FlatStyle.Flat,
            BackColor = Color.FromArgb(234, 236, 241),
            ForeColor = Color.FromArgb(28, 28, 30)
        };
        openDirectoryButton.FlatAppearance.BorderSize = 0;
        openDirectoryButton.Click += (_, _) => OpenIssueReportDirectory();

        var openPatchButton = new Forms.Button
        {
            Text = "安装补丁",
            Location = new System.Drawing.Point(146, 465),
            Size = new System.Drawing.Size(96, 34),
            FlatStyle = Forms.FlatStyle.Flat,
            BackColor = Color.FromArgb(18, 108, 240),
            ForeColor = Color.White,
            Enabled = false
        };
        openPatchButton.FlatAppearance.BorderSize = 0;
        var patchOperationRunning = false;
        dialog.FormClosing += (_, args) =>
        {
            // 补丁下载进行中时不允许关闭对话框，避免回调访问已释放的窗口。
            if (patchOperationRunning)
            {
                args.Cancel = true;
            }
        };
        openPatchButton.Click += async (_, _) =>
        {
            if (patchOperationRunning ||
                listView.SelectedItems.Count == 0 ||
                listView.SelectedItems[0].Tag is not IssueReportIndexEntry entry)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(entry.PatchDownloadUrl))
            {
                if (App.Instance is not null && !string.Equals(entry.PatchVersion, entry.InstalledPatchVersion, StringComparison.OrdinalIgnoreCase))
                {
                    PackageUpdateLaunchResult result;
                    patchOperationRunning = true;
                    openPatchButton.Enabled = false;
                    var originalText = openPatchButton.Text;
                    openPatchButton.Text = "下载中...";
                    try
                    {
                        result = await App.Instance.ApplyIssuePatchUpdateAsync(entry);
                    }
                    catch (Exception ex)
                    {
                        result = new PackageUpdateLaunchResult(false, false, $"应用补丁失败：{ex.Message}", string.Empty, string.Empty);
                    }
                    finally
                    {
                        patchOperationRunning = false;
                        if (!openPatchButton.IsDisposed)
                        {
                            openPatchButton.Text = originalText;
                            openPatchButton.Enabled = true;
                        }
                    }

                    if (dialog.IsDisposed)
                    {
                        return;
                    }

                    if (!result.Success)
                    {
                        Forms.MessageBox.Show(dialog, result.Message, "问题修复", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                    }
                    else
                    {
                        dialog.Close();
                    }
                }
                else
                {
                    TryOpenExternalUrl(entry.PatchDownloadUrl);
                }
            }
        };

        var viewPatchNotesButton = new Forms.Button
        {
            Text = "补丁说明",
            Location = new System.Drawing.Point(252, 465),
            Size = new System.Drawing.Size(96, 34),
            FlatStyle = Forms.FlatStyle.Flat,
            BackColor = Color.FromArgb(234, 236, 241),
            ForeColor = Color.FromArgb(28, 28, 30),
            Enabled = false
        };
        viewPatchNotesButton.FlatAppearance.BorderSize = 0;
        viewPatchNotesButton.Click += (_, _) =>
        {
            if (listView.SelectedItems.Count == 0 || listView.SelectedItems[0].Tag is not IssueReportIndexEntry entry)
            {
                return;
            }

            var message = BuildIssueEntryDetails(entry);
            Forms.MessageBox.Show(dialog, message, "补丁说明", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
        };

        var retryButton = new Forms.Button
        {
            Text = "重试待上传",
            Location = new System.Drawing.Point(358, 465),
            Size = new System.Drawing.Size(118, 34),
            FlatStyle = Forms.FlatStyle.Flat,
            BackColor = Color.FromArgb(18, 108, 240),
            ForeColor = Color.White
        };
        retryButton.FlatAppearance.BorderSize = 0;
        retryButton.Click += (_, _) =>
        {
            RetryPendingIssueUploads();
            dialog.Close();
        };

        var refreshButton = new Forms.Button
        {
            Text = "刷新状态",
            Location = new System.Drawing.Point(486, 465),
            Size = new System.Drawing.Size(118, 34),
            FlatStyle = Forms.FlatStyle.Flat,
            BackColor = Color.FromArgb(234, 236, 241),
            ForeColor = Color.FromArgb(28, 28, 30)
        };
        refreshButton.FlatAppearance.BorderSize = 0;
        refreshButton.Click += (_, _) =>
        {
            RefreshIssueStatuses();
            dialog.Close();
        };

        listView.SelectedIndexChanged += (_, _) =>
        {
            if (listView.SelectedItems.Count == 0 || listView.SelectedItems[0].Tag is not IssueReportIndexEntry entry)
            {
                openPatchButton.Enabled = false;
                openPatchButton.Text = "安装补丁";
                viewPatchNotesButton.Enabled = false;
                detailTextBox.Text = "选择一条问题记录后，可在这里查看补丁说明和远端状态。";
                return;
            }

            var patchInstalled = !string.IsNullOrWhiteSpace(entry.PatchVersion) &&
                                 string.Equals(entry.PatchVersion, entry.InstalledPatchVersion, StringComparison.OrdinalIgnoreCase);
            openPatchButton.Enabled = !string.IsNullOrWhiteSpace(entry.PatchDownloadUrl) && !patchInstalled;
            openPatchButton.Text = patchInstalled ? "已安装" : "安装补丁";
            viewPatchNotesButton.Enabled =
                !string.IsNullOrWhiteSpace(entry.PatchReleaseNotes) ||
                !string.IsNullOrWhiteSpace(entry.PatchVersion) ||
                !string.IsNullOrWhiteSpace(entry.PatchDownloadUrl);
            detailTextBox.Text = BuildIssueEntryDetails(entry);
        };

        detailTextBox.Text = entries.Count == 0
            ? "当前还没有问题记录。"
            : "选择一条问题记录后，可在这里查看补丁说明和远端状态。";

        var closeButton = new Forms.Button
        {
            Text = "关闭",
            Location = new System.Drawing.Point(816, 465),
            Size = new System.Drawing.Size(86, 34),
            FlatStyle = Forms.FlatStyle.Flat,
            BackColor = Color.FromArgb(234, 236, 241),
            ForeColor = Color.FromArgb(28, 28, 30)
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.Click += (_, _) => dialog.Close();

        dialog.Controls.Add(listView);
        dialog.Controls.Add(detailTextBox);
        dialog.Controls.Add(openDirectoryButton);
        dialog.Controls.Add(openPatchButton);
        dialog.Controls.Add(viewPatchNotesButton);
        dialog.Controls.Add(retryButton);
        dialog.Controls.Add(refreshButton);
        dialog.Controls.Add(closeButton);
        dialog.ShowDialog();
    }

    private async void RefreshIssueStatuses()
    {
        if (_issueNetworkOperationRunning)
        {
            ShowNotification("问题记录", "上一个问题记录操作仍在进行，请稍候。", Forms.ToolTipIcon.Info, 2000);
            return;
        }

        _issueNetworkOperationRunning = true;
        try
        {
            var result = await _issueReportService.RefreshIssueStatusesAsync();
            ShowNotification("问题记录", result.Message, result.Success ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning, 2600);
            if (!result.Success)
            {
                Forms.MessageBox.Show(result.Message, "问题记录", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            var message = $"刷新问题状态失败：{ex.Message}";
            ShowNotification("问题记录", message, Forms.ToolTipIcon.Warning, 2600);
            Forms.MessageBox.Show(message, "问题记录", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
        }
        finally
        {
            _issueNetworkOperationRunning = false;
        }
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
            Text = "写下你刚刚遇到的问题。程序会自动附带时间、最近日志和当前设置，生成反馈文件和结构化问题包，并尝试立即上传到服务器。",
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

        var reportOperationRunning = false;
        dialog.FormClosing += (_, args) =>
        {
            // 打包/上传进行中时不允许关闭对话框，避免回调访问已释放的窗口。
            if (reportOperationRunning)
            {
                args.Cancel = true;
            }
        };
        cancelButton.Click += (_, _) => dialog.Close();
        generateButton.Click += async (_, _) =>
        {
            if (reportOperationRunning)
            {
                return;
            }

            reportOperationRunning = true;
            generateButton.Enabled = false;
            cancelButton.Enabled = false;
            issueTextBox.ReadOnly = true;
            var originalText = generateButton.Text;
            generateButton.Text = "生成中...";
            try
            {
                IssueReportResult result;
                try
                {
                    result = await _issueReportService.CreateReportAsync(issueTextBox.Text);
                }
                finally
                {
                    reportOperationRunning = false;
                }

                if (dialog.IsDisposed)
                {
                    return;
                }

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
            catch (Exception ex)
            {
                if (!dialog.IsDisposed)
                {
                    Forms.MessageBox.Show(dialog, $"生成问题记录失败：{ex.Message}", "问题记录", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                }
            }
            finally
            {
                reportOperationRunning = false;
                if (!generateButton.IsDisposed)
                {
                    generateButton.Text = originalText;
                    generateButton.Enabled = true;
                    cancelButton.Enabled = true;
                    issueTextBox.ReadOnly = false;
                }
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
