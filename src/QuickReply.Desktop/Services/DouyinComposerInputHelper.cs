using QuickReply.Desktop.Native;

namespace QuickReply.Desktop.Services;

/// <summary>
/// After a Douyin text Enter, the chat input often loses focus. Paste then hits nowhere.
/// Re-activate the workbench and focus/click the composer before the image stage.
/// </summary>
internal static class DouyinComposerInputHelper
{
    internal const int PostTextSendSettleMs = 300;
    internal const int PostFocusSettleMs = 150;

    /// <summary>Douyin image paste is a single attempt. Do not raise this to retry on failure.</summary>
    internal const int MaxImagePasteAttempts = 1;

    public static bool TryFocusComposer(IntPtr targetWindowHandle, IDictionary<string, object?> diagnostics)
    {
        if (targetWindowHandle == IntPtr.Zero)
        {
            diagnostics["douyinComposerFocus"] = "no_window";
            return false;
        }

        // The send path activated Douyin once at the start. If it is no longer in front, the user
        // switched apps on purpose: never force Douyin back over the window they are working in.
        try
        {
            if (!IsTargetForeground(targetWindowHandle))
            {
                diagnostics["douyinComposerFocus"] = "skipped_user_left";
                return false;
            }
        }
        catch (Exception ex)
        {
            diagnostics["douyinComposerFocus"] = "foreground_check_error:" + ex.GetType().Name;
            return false;
        }

        // Click only. A UI Automation walk of the Douyin (Chromium) page costs seconds of CPU.
        return TryClickComposer(targetWindowHandle, diagnostics);
    }

    private static bool IsTargetForeground(IntPtr targetWindowHandle)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        if (foreground == targetWindowHandle)
        {
            return true;
        }

        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GaRootowner);
        return root != IntPtr.Zero && root == targetWindowHandle;
    }

    private static bool TryClickComposer(IntPtr targetWindowHandle, IDictionary<string, object?> diagnostics)
    {
        try
        {
            if (!NativeMethods.GetWindowRect(targetWindowHandle, out var windowRect))
            {
                diagnostics["douyinComposerFocus"] = "click_no_rect";
                return false;
            }

            var width = windowRect.Right - windowRect.Left;
            var height = windowRect.Bottom - windowRect.Top;
            if (width < 200 || height < 200)
            {
                diagnostics["douyinComposerFocus"] = "click_window_too_small";
                return false;
            }

            // Same bottom-center bias as DouyinVideoOpenDialogUploader composer click.
            var screenX = windowRect.Left + width * 2 / 3;
            var screenY = windowRect.Top + height - 60;
            AbsoluteClick(screenX, screenY);
            diagnostics["douyinComposerFocus"] = "click";
            diagnostics["douyinComposerClickX"] = screenX;
            diagnostics["douyinComposerClickY"] = screenY;
            return true;
        }
        catch (Exception ex)
        {
            diagnostics["douyinComposerFocus"] = "click_error:" + ex.GetType().Name;
            return false;
        }
    }

    private static void AbsoluteClick(int screenX, int screenY)
    {
        NativeMethods.SetCursorPos(screenX, screenY);
        Thread.Sleep(30);
        // MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP
        NativeMethods.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        NativeMethods.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
}
