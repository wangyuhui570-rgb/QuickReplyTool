using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

/// <summary>
/// Douyin/飞鸽 video FileDrop paste is ignored by the Chromium composer.
/// Working path (probed on DESKTOP-NTRMANG): hover+click toolbar "发送视频",
/// fill the native OpenFileDialog with the absolute path, confirm Open, then send.
/// </summary>
internal static class DouyinVideoOpenDialogUploader
{
    private const int VideoIconIndex = 2; // 0=emoji, 1=image, 2=video
    private const int IconOffsetFromBottom = 146;
    private const int IconRowYFromBottom = 146; // ~585 in 731-tall window
    private static readonly int[] IconXsIn1280 = [394, 430, 466, 498, 538, 567, 581, 627, 668];

    public static async Task<ReplyActionResult> TryUploadAndOptionallySendAsync(
        IntPtr targetWindowHandle,
        string videoPath,
        bool sendAfterUpload,
        SendHotkeyMode sendHotkeyMode,
        IKeyboardInputSimulator keyboard,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken = default)
    {
        diagnostics["douyinVideoStrategy"] = "toolbar_open_dialog";
        diagnostics["douyinVideoPath"] = videoPath;

        if (targetWindowHandle == IntPtr.Zero || !IsWindow(targetWindowHandle))
        {
            return new ReplyActionResult(
                false,
                "未找到抖店聊天窗口，无法打开视频选择框。",
                FailureKind: ReplyActionFailureKind.MissingTargetWindow,
                UserGuidance: "请先点开抖店/飞鸽聊天输入框，再重试发送视频。");
        }

        if (!File.Exists(videoPath))
        {
            return new ReplyActionResult(
                false,
                $"视频文件不存在：{videoPath}",
                FailureKind: ReplyActionFailureKind.MissingInput,
                UserGuidance: "请确认视频文件仍存在后再试。");
        }

        ShowWindow(targetWindowHandle, 9);
        SetForegroundWindow(targetWindowHandle);
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);

        if (!GetWindowRect(targetWindowHandle, out var windowRect))
        {
            return new ReplyActionResult(
                false,
                "无法读取抖店窗口位置。",
                FailureKind: ReplyActionFailureKind.ActivationFailed,
                UserGuidance: "请把抖店聊天窗口移到可见位置后再试。");
        }

        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        if (width < 400 || height < 300)
        {
            return new ReplyActionResult(
                false,
                "抖店聊天窗口过小，无法定位视频按钮。",
                FailureKind: ReplyActionFailureKind.ActivationFailed,
                UserGuidance: "请把聊天窗口放大后再发送视频。");
        }

        var scaleX = width / 1280.0;
        var iconX = (int)Math.Round(IconXsIn1280[VideoIconIndex] * scaleX);
        var iconY = height - IconOffsetFromBottom;
        var screenX = windowRect.Left + iconX;
        var screenY = windowRect.Top + iconY;
        diagnostics["douyinVideoClickX"] = screenX;
        diagnostics["douyinVideoClickY"] = screenY;

        // Focus composer first (bottom-center), then hover+click video icon.
        AbsoluteClick(windowRect.Left + width * 2 / 3, windowRect.Top + height - 60);
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        AbsoluteMove(screenX - 15, screenY);
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        AbsoluteMove(screenX, screenY);
        await Task.Delay(220, cancellationToken).ConfigureAwait(false);
        AbsoluteClick(screenX, screenY);

        var dialog = await WaitForOpenFileDialogAsync(targetWindowHandle, TimeSpan.FromSeconds(6), cancellationToken)
            .ConfigureAwait(false);
        if (dialog == IntPtr.Zero)
        {
            return new ReplyActionResult(
                false,
                "已点击抖店“发送视频”，但未出现文件选择框。",
                FailureKind: ReplyActionFailureKind.ManualConfirmationRequired,
                UserGuidance: "请手动点聊天输入框上方的“发送视频”按钮，选好文件后回车发送；或把窗口放到主屏后再让工具重试。");
        }

        diagnostics["douyinVideoDialogHandle"] = $"0x{dialog.ToInt64():X}";
        SetForegroundWindow(dialog);
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);

        if (!TrySetOpenDialogFileName(dialog, videoPath))
        {
            PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
            return new ReplyActionResult(
                false,
                "已打开视频选择框，但写入文件路径失败。",
                FailureKind: ReplyActionFailureKind.ManualConfirmationRequired,
                UserGuidance: "请在弹出的文件框里手动选择视频，确认预览后回车发送。");
        }

        if (!TryClickOpenButton(dialog))
        {
            // Fallback: Alt+O / Enter
            keyboard.SendEnter();
        }

        var closed = await WaitUntilDialogClosedAsync(dialog, TimeSpan.FromSeconds(12), cancellationToken)
            .ConfigureAwait(false);
        diagnostics["douyinVideoDialogClosed"] = closed;
        if (!closed)
        {
            // Dismiss possible "文件名无效" child dialog then retry Enter once.
            DismissOwnedDialogs(dialog);
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            closed = !IsWindowVisible(dialog);
            diagnostics["douyinVideoDialogClosedAfterDismiss"] = closed;
        }

        if (!closed)
        {
            return new ReplyActionResult(
                false,
                "视频文件选择框仍未关闭，可能路径无效或被拦截。",
                FailureKind: ReplyActionFailureKind.ManualConfirmationRequired,
                UserGuidance: "请检查弹出的文件框：选中目标 mp4 后点“打开”，确认输入区出现视频预览后再发送。");
        }

        // Give Douyin time to stage the upload preview in the composer.
        await Task.Delay(900, cancellationToken).ConfigureAwait(false);
        SetForegroundWindow(targetWindowHandle);
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);

        if (!sendAfterUpload)
        {
            return new ReplyActionResult(true, "已通过抖店视频按钮选中视频，请确认预览后手动发送。");
        }

        switch (sendHotkeyMode)
        {
            case SendHotkeyMode.CtrlEnter:
                keyboard.SendCtrlEnter();
                break;
            case SendHotkeyMode.Enter:
            default:
                keyboard.SendEnter();
                break;
        }

        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        return new ReplyActionResult(true, "已通过抖店视频按钮选择并发送视频。");
    }

    private static async Task<IntPtr> WaitForOpenFileDialogAsync(
        IntPtr ownerHint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true;
                }

                var className = new StringBuilder(256);
                GetClassName(hWnd, className, className.Capacity);
                if (!string.Equals(className.ToString(), "#32770", StringComparison.Ordinal))
                {
                    return true;
                }

                var owner = GetWindow(hWnd, 4); // GW_OWNER
                if (owner == ownerHint || ownerHint == IntPtr.Zero)
                {
                    found = hWnd;
                    return false;
                }

                // Also accept dialogs whose thread belongs to same process as ownerHint.
                GetWindowThreadProcessId(ownerHint, out var ownerPid);
                GetWindowThreadProcessId(hWnd, out var dialogPid);
                if (ownerPid != 0 && ownerPid == dialogPid)
                {
                    found = hWnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero)
            {
                return found;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return IntPtr.Zero;
    }

    private static async Task<bool> WaitUntilDialogClosedAsync(
        IntPtr dialog,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindow(dialog) || !IsWindowVisible(dialog))
            {
                return true;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return !IsWindowVisible(dialog);
    }

    private static bool TrySetOpenDialogFileName(IntPtr dialog, string filePath)
    {
        var edits = new List<IntPtr>();
        EnumChildWindows(dialog, (hWnd, _) =>
        {
            var className = new StringBuilder(128);
            GetClassName(hWnd, className, className.Capacity);
            if (string.Equals(className.ToString(), "Edit", StringComparison.Ordinal))
            {
                edits.Add(hWnd);
            }

            return true;
        }, IntPtr.Zero);

        if (edits.Count == 0)
        {
            return false;
        }

        var ok = false;
        foreach (var edit in edits)
        {
            var result = SendMessage(edit, 0x000C, IntPtr.Zero, filePath); // WM_SETTEXT
            var buffer = new StringBuilder(filePath.Length + 16);
            SendMessage(edit, 0x000D, buffer.Capacity, buffer); // WM_GETTEXT
            if (string.Equals(buffer.ToString(), filePath, StringComparison.OrdinalIgnoreCase))
            {
                ok = true;
            }
            else if (result != IntPtr.Zero)
            {
                ok = true;
            }
        }

        return ok;
    }

    private static bool TryClickOpenButton(IntPtr dialog)
    {
        var openButton = IntPtr.Zero;
        EnumChildWindows(dialog, (hWnd, _) =>
        {
            var className = new StringBuilder(128);
            GetClassName(hWnd, className, className.Capacity);
            if (!string.Equals(className.ToString(), "Button", StringComparison.Ordinal))
            {
                return true;
            }

            var text = new StringBuilder(64);
            GetWindowText(hWnd, text, text.Capacity);
            var label = text.ToString();
            if (label.Contains("打开", StringComparison.Ordinal) ||
                label.Contains("Open", StringComparison.OrdinalIgnoreCase))
            {
                openButton = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        if (openButton == IntPtr.Zero || !GetWindowRect(openButton, out var rect))
        {
            return false;
        }

        AbsoluteMove(rect.Left - 10, (rect.Top + rect.Bottom) / 2);
        Thread.Sleep(80);
        AbsoluteClick((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        return true;
    }

    private static void DismissOwnedDialogs(IntPtr mainDialog)
    {
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            var className = new StringBuilder(256);
            GetClassName(hWnd, className, className.Capacity);
            if (!string.Equals(className.ToString(), "#32770", StringComparison.Ordinal))
            {
                return true;
            }

            if (hWnd == mainDialog)
            {
                return true;
            }

            if (!GetWindowRect(hWnd, out var rect))
            {
                return true;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 500 && height < 220)
            {
                // Likely "文件名无效" style prompt: click OK region.
                AbsoluteClick(rect.Right - 50, rect.Bottom - 25);
            }

            return true;
        }, IntPtr.Zero);
    }

    private static void AbsoluteMove(int screenX, int screenY)
    {
        var vx = GetSystemMetrics(76);
        var vy = GetSystemMetrics(77);
        var vw = Math.Max(GetSystemMetrics(78), 1);
        var vh = Math.Max(GetSystemMetrics(79), 1);
        var ax = (screenX - vx) * 65535 / Math.Max(vw - 1, 1);
        var ay = (screenY - vy) * 65535 / Math.Max(vh - 1, 1);
        var input = new INPUT
        {
            type = 0,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = ax,
                    dy = ay,
                    dwFlags = 0x0001 | 0x8000 | 0x4000 // MOVE|ABSOLUTE|VIRTUALDESK
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void AbsoluteClick(int screenX, int screenY)
    {
        AbsoluteMove(screenX, screenY);
        Thread.Sleep(30);
        var vx = GetSystemMetrics(76);
        var vy = GetSystemMetrics(77);
        var vw = Math.Max(GetSystemMetrics(78), 1);
        var vh = Math.Max(GetSystemMetrics(79), 1);
        var ax = (screenX - vx) * 65535 / Math.Max(vw - 1, 1);
        var ay = (screenY - vy) * 65535 / Math.Max(vh - 1, 1);
        Span<INPUT> inputs =
        [
            new INPUT
            {
                type = 0,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = ax,
                        dy = ay,
                        dwFlags = 0x0001 | 0x8000 | 0x4000 | 0x0002
                    }
                }
            },
            new INPUT
            {
                type = 0,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = ax,
                        dy = ay,
                        dwFlags = 0x0001 | 0x8000 | 0x4000 | 0x0004
                    }
                }
            }
        ];
        SendInput((uint)inputs.Length, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, int wParam, StringBuilder lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
