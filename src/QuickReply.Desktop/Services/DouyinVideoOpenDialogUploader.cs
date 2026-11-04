using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal interface IDouyinVideoUploader
{
    Task<ReplyActionResult> UploadAsync(
        IntPtr targetWindowHandle,
        string videoPath,
        bool sendAfterUpload,
        SendHotkeyMode sendHotkeyMode,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken = default);

    /// <summary>Sends one image through the toolbar 图片 button; fakes that do not override it fail.</summary>
    Task<ReplyActionResult> UploadImageAsync(
        IntPtr targetWindowHandle,
        string imagePath,
        SendHotkeyMode sendHotkeyMode,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ReplyActionResult(false, "当前环境不支持抖店图片按钮上传。"));
}

/// <summary>
/// Douyin/飞鸽 ignores a FileDrop paste of a video (the Chromium composer drops it silently),
/// so every Douyin video goes through the toolbar "发送视频" button and the native OpenFileDialog.
/// Images take the same route through the toolbar 图片 button. Buttons are clicked at
/// window-relative coordinates measured on DESKTOP-NTRMANG.
/// </summary>
internal sealed class DouyinVideoOpenDialogUploader : IDouyinVideoUploader
{
    internal const string SubmittedMessage = "视频已提交给抖店上传，通常 10–20 秒后出现在会话里；输入框已交还，可继续打字。";
    internal const string StagedMessage = "已通过抖店视频按钮选中视频，请确认预览后手动发送。";

    // Toolbar order: 0=emoji, 1=image, 2=video.
    private sealed record ToolbarMedia(int IconIndex, string Noun, string SubmittedMessage, string StagedMessage)
    {
        public bool IsImage => IconIndex == 1;

        public static ToolbarMedia Video { get; } = new(2, "视频", DouyinVideoOpenDialogUploader.SubmittedMessage, DouyinVideoOpenDialogUploader.StagedMessage);

        public static ToolbarMedia Image { get; } = new(1, "图片", "图片已通过抖店图片按钮发送。", "已通过抖店图片按钮选中图片，请确认后手动发送。");
    }
    private const int IconOffsetFromBottom = 146;
    private static readonly int[] IconXsIn1280 = [394, 430, 466, 498, 538, 567, 581, 627, 668];

    private readonly IKeyboardInputSimulator _keyboard;

    public DouyinVideoOpenDialogUploader(IKeyboardInputSimulator keyboard)
    {
        _keyboard = keyboard;
    }

    public Task<ReplyActionResult> UploadAsync(
        IntPtr targetWindowHandle,
        string videoPath,
        bool sendAfterUpload,
        SendHotkeyMode sendHotkeyMode,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken = default)
    {
        return UploadMediaAsync(ToolbarMedia.Video, targetWindowHandle, videoPath, sendAfterUpload, sendHotkeyMode, diagnostics, cancellationToken);
    }

    /// <summary>
    /// Douyin opens a "send this image?" dialog for a pasted image, and the paste path kept
    /// missing it. The toolbar 图片 button (left of 视频) takes the same open-file route as videos.
    /// </summary>
    public Task<ReplyActionResult> UploadImageAsync(
        IntPtr targetWindowHandle,
        string imagePath,
        SendHotkeyMode sendHotkeyMode,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken = default)
    {
        return UploadMediaAsync(ToolbarMedia.Image, targetWindowHandle, imagePath, true, sendHotkeyMode, diagnostics, cancellationToken);
    }

    private async Task<ReplyActionResult> UploadMediaAsync(
        ToolbarMedia media,
        IntPtr targetWindowHandle,
        string videoPath,
        bool sendAfterUpload,
        SendHotkeyMode sendHotkeyMode,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken = default)
    {
        diagnostics["douyinVideoStrategy"] = "toolbar_open_dialog";
        diagnostics["douyinToolbarMedia"] = media.Noun;
        SearchOverlayDiagnostics.Log("message_compose", "douyin_toolbar_upload_started", new Dictionary<string, object?>
        {
            ["media"] = media.Noun,
            ["path"] = videoPath
        });
        diagnostics["douyinVideoPath"] = videoPath;

        if (targetWindowHandle == IntPtr.Zero || !IsWindow(targetWindowHandle))
        {
            return new ReplyActionResult(
                false,
                $"未找到抖店聊天窗口，无法打开{media.Noun}选择框。",
                FailureKind: ReplyActionFailureKind.MissingTargetWindow,
                UserGuidance: $"请先点开抖店/飞鸽聊天输入框，再重试发送{media.Noun}。");
        }

        if (!File.Exists(videoPath))
        {
            return new ReplyActionResult(
                false,
                $"{media.Noun}文件不存在：{videoPath}",
                FailureKind: ReplyActionFailureKind.MissingInput,
                UserGuidance: $"请确认{media.Noun}文件仍存在后再试。");
        }

        // Text and images may have taken several seconds before the video starts. If the user went
        // to another app meanwhile, do not pull Douyin back and click into it.
        if (UserLeftTarget(targetWindowHandle))
        {
            diagnostics["douyinVideoAborted"] = "user_left_before_start";
            return CreateUserLeftResult($"{media.Noun}还没开始上传");
        }

        ShowWindow(targetWindowHandle, 9);
        var activated = ForegroundWindowTracker.ActivateWindowWithInputAttach(targetWindowHandle);
        diagnostics["douyinVideoActivated"] = activated;
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
                $"抖店聊天窗口过小，无法定位{media.Noun}按钮。",
                FailureKind: ReplyActionFailureKind.ActivationFailed,
                UserGuidance: $"请把聊天窗口放大后再发送{media.Noun}。");
        }

        // Focus composer first (bottom-center) so the toolbar is the active chat's toolbar.
        AbsoluteClick(windowRect.Left + width * 2 / 3, windowRect.Top + height - 60);
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        // Coordinates only. Every logged send so far fell back to them anyway: the UI Automation
        // lookup never found the button and walked the whole Douyin page, which costs a lot of CPU.
        var scaleX = width / 1280.0;
        var screenX = windowRect.Left + (int)Math.Round(IconXsIn1280[media.IconIndex] * scaleX);
        var screenY = windowRect.Top + height - IconOffsetFromBottom;
        diagnostics["douyinVideoLocator"] = "coordinates";

        diagnostics["douyinVideoClickX"] = screenX;
        diagnostics["douyinVideoClickY"] = screenY;

        // Match 2.1.11 hover timings: activation settle, then hover before the coordinate click so
        // a near-miss hitbox still opens the dialog.
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);

        if (UserLeftTarget(targetWindowHandle))
        {
            diagnostics["douyinVideoAborted"] = "user_left_before_toolbar_click";
            return CreateUserLeftResult($"{media.Noun}按钮没有点击");
        }

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
                $"已点击抖店“发送{media.Noun}”，但未出现文件选择框。",
                FailureKind: ReplyActionFailureKind.ManualConfirmationRequired,
                UserGuidance: $"请手动点聊天输入框上方的“发送{media.Noun}”按钮，选好文件后回车发送；或把窗口放到主屏后再让工具重试。");
        }

        diagnostics["douyinVideoDialogHandle"] = $"0x{dialog.ToInt64():X}";
        // The path is written with window messages, so the dialog only needs to be in front when the
        // user is still on Douyin. Bringing it up over another app was the "forced back" bug.
        var userLeftBeforeDialog = UserLeftTarget(targetWindowHandle, dialog);
        diagnostics["douyinVideoUserLeftBeforeDialog"] = userLeftBeforeDialog;
        if (!userLeftBeforeDialog)
        {
            ForegroundWindowTracker.ActivateWindowWithInputAttach(dialog);
        }

        await Task.Delay(200, cancellationToken).ConfigureAwait(false);

        if (!TrySetOpenDialogFileName(dialog, videoPath))
        {
            PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
            return new ReplyActionResult(
                false,
                $"已打开{media.Noun}选择框，但写入文件路径失败。",
                FailureKind: ReplyActionFailureKind.ManualConfirmationRequired,
                UserGuidance: $"请在弹出的文件框里手动选择{media.Noun}，确认预览后回车发送。");
        }

        if (!TryClickOpenButton(dialog))
        {
            if (UserLeftTarget(targetWindowHandle, dialog))
            {
                diagnostics["douyinVideoAborted"] = "user_left_before_open_enter";
                return CreateUserLeftResult($"{media.Noun}文件框已填好路径，但没有按打开");
            }

            _keyboard.SendEnter();
        }

        var closed = await WaitUntilDialogClosedAsync(dialog, TimeSpan.FromSeconds(12), cancellationToken)
            .ConfigureAwait(false);
        diagnostics["douyinVideoDialogClosed"] = closed;
        if (!closed)
        {
            // Dismiss a possible "文件名无效" child dialog.
            DismissOwnedDialogs(dialog);
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            closed = !IsWindowVisible(dialog);
            diagnostics["douyinVideoDialogClosedAfterDismiss"] = closed;
        }

        if (!closed)
        {
            // One close, then stop. Leaving the modal up blocks the chat; clicking the toolbar again would open another dialog.
            PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero);
            diagnostics["douyinVideoDialogClosedByTool"] = true;
            return new ReplyActionResult(
                false,
                $"{media.Noun}文件选择框没有自己关掉，已关闭该窗口并停止，没有再次点击。",
                FailureKind: ReplyActionFailureKind.ManualConfirmationRequired,
                UserGuidance: $"请看一下抖店：如果{media.Noun}预览已经在输入框里，手动点发送即可；如果不在，再发一次。本次不会自动重试。");
        }

        // Images take exactly the video steps from here: no UI Automation scan of the page. A
        // full-tree scan of the Douyin page before the click took seconds of CPU on the user's
        // machine (2026-11-04: the PC froze and the toolbar was never clicked).
        // Give Douyin time to stage the upload preview in the composer (restored to 2.1.11's 900ms).
        await Task.Delay(900, cancellationToken).ConfigureAwait(false);

        if (!sendAfterUpload)
        {
            diagnostics["videoSendState"] = "staged";
            return new ReplyActionResult(true, media.StagedMessage);
        }

        // The send key goes to whatever is in front. If the user switched away during the upload
        // (up to ~13 s), leave the staged video for them instead of yanking Douyin back.
        if (UserLeftTarget(targetWindowHandle, dialog))
        {
            diagnostics["videoSendState"] = "staged_user_left";
            return new ReplyActionResult(
                false,
                $"{media.Noun}已放进抖店输入框，但你已切到别的窗口，没有按发送键。",
                FailureKind: ReplyActionFailureKind.ManualConfirmationRequired,
                UserGuidance: $"回到抖店后确认{media.Noun}预览，手动点发送即可。本次没有抢回焦点。");
        }

        await Task.Delay(200, cancellationToken).ConfigureAwait(false);

        switch (sendHotkeyMode)
        {
            case SendHotkeyMode.CtrlEnter:
                _keyboard.SendCtrlEnter();
                break;
            case SendHotkeyMode.AltS:
                _keyboard.SendAltS();
                break;
            case SendHotkeyMode.Enter:
            default:
                _keyboard.SendEnter();
                break;
        }

        // Return immediately: the upload continues inside Douyin, the user gets the input box back.
        // This is "submitted", not "delivered" – Douyin gives no reliable completion signal.
        diagnostics["videoSendState"] = "submitted";
        return new ReplyActionResult(true, media.SubmittedMessage);
    }

    /// <summary>
    /// True when another app is in front. Douyin itself, its dialogs and this tool's own windows
    /// (the overlay that just closed) do not count as the user leaving.
    /// </summary>
    private static bool UserLeftTarget(IntPtr targetWindowHandle, IntPtr dialog = default)
    {
        var foreground = Native.NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == targetWindowHandle || (dialog != IntPtr.Zero && foreground == dialog))
        {
            return false;
        }

        var root = Native.NativeMethods.GetAncestor(foreground, Native.NativeMethods.GaRootowner);
        if (root == targetWindowHandle || (dialog != IntPtr.Zero && root == dialog))
        {
            return false;
        }

        Native.NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        Native.NativeMethods.GetWindowThreadProcessId(targetWindowHandle, out var targetProcessId);
        return foregroundProcessId != (uint)Environment.ProcessId &&
               (foregroundProcessId == 0 || foregroundProcessId != targetProcessId);
    }

    private static ReplyActionResult CreateUserLeftResult(string stage)
    {
        return new ReplyActionResult(
            false,
            $"你已切到别的窗口，{stage}，已停止。",
            FailureKind: ReplyActionFailureKind.FocusLost,
            UserGuidance: "回到抖店会话后再发一次视频。本次没有抢回焦点，也没有点击。");
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
