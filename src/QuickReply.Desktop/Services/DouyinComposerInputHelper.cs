using System.Windows.Automation;
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
    internal const int MaxImagePasteAttempts = 3;

    public static bool TryFocusComposer(IntPtr targetWindowHandle, IDictionary<string, object?> diagnostics)
    {
        if (targetWindowHandle == IntPtr.Zero)
        {
            diagnostics["douyinComposerFocus"] = "no_window";
            return false;
        }

        try
        {
            NativeMethods.SetForegroundWindow(targetWindowHandle);
        }
        catch
        {
        }

        if (TryFocusViaAutomation(targetWindowHandle, diagnostics))
        {
            return true;
        }

        return TryClickComposer(targetWindowHandle, diagnostics);
    }

    private static bool TryFocusViaAutomation(IntPtr targetWindowHandle, IDictionary<string, object?> diagnostics)
    {
        try
        {
            var root = AutomationElement.FromHandle(targetWindowHandle);
            if (root is null)
            {
                diagnostics["douyinComposerFocus"] = "uia_no_root";
                return false;
            }

            AutomationElement? best = null;
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused is not null &&
                    (Equals(focused.Current.ControlType, ControlType.Edit) ||
                     Equals(focused.Current.ControlType, ControlType.Document)))
                {
                    best = focused;
                }
            }
            catch
            {
            }

            if (best is null)
            {
                var edits = root.FindAll(
                    TreeScope.Descendants,
                    new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)));
                // Prefer a larger edit near the bottom (chat composer).
                double bestScore = double.MinValue;
                foreach (AutomationElement edit in edits)
                {
                    try
                    {
                        var rect = edit.Current.BoundingRectangle;
                        if (rect.Width < 80 || rect.Height < 20 || rect.IsEmpty)
                        {
                            continue;
                        }

                        var score = rect.Width * rect.Height + rect.Y;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = edit;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (best is null)
            {
                diagnostics["douyinComposerFocus"] = "uia_no_edit";
                return false;
            }

            best.SetFocus();
            diagnostics["douyinComposerFocus"] = "uia_setfocus";
            return true;
        }
        catch (Exception ex)
        {
            diagnostics["douyinComposerFocus"] = "uia_error:" + ex.GetType().Name;
            return false;
        }
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
