using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using QuickReply.Desktop.Native;

namespace QuickReply.Desktop.Services;

internal enum DouyinImageDialogOutcome
{
    /// <summary>No new send/confirm button appeared: the image went into the composer as before.</summary>
    NotFound,

    /// <summary>The dialog's send button was pressed and the dialog closed: the image is sent.</summary>
    Confirmed,

    /// <summary>A dialog appeared but pressing its button did not close it. Nothing else was pressed.</summary>
    ConfirmFailed
}

internal readonly record struct DouyinSendButtonSnapshot(IReadOnlyList<Rect> Buttons);

/// <summary>
/// Douyin does not put a pasted image into the composer. It opens a "send this image?" dialog.
/// The dialog's button is found by comparing send/confirm buttons before and after the paste, so
/// the composer's own 发送 button is never pressed by mistake.
/// </summary>
internal interface IDouyinImageSendDialogHandler
{
    DouyinSendButtonSnapshot CaptureSendButtons(IntPtr targetWindowHandle);

    Task<DouyinImageDialogOutcome> TryConfirmAsync(
        IntPtr targetWindowHandle,
        DouyinSendButtonSnapshot before,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken = default);
}

internal sealed class DouyinImageSendDialogHandler : IDouyinImageSendDialogHandler
{
    internal const int AppearTimeoutMs = 2500;
    internal const int CloseTimeoutMs = 2500;
    private const int PollIntervalMs = 150;

    private static readonly string[] ConfirmButtonNames = ["发送", "确定", "确认", "确认发送"];

    public DouyinSendButtonSnapshot CaptureSendButtons(IntPtr targetWindowHandle)
    {
        return new DouyinSendButtonSnapshot(
            FindConfirmButtons(targetWindowHandle).Select(button => button.Bounds).ToList());
    }

    public async Task<DouyinImageDialogOutcome> TryConfirmAsync(
        IntPtr targetWindowHandle,
        DouyinSendButtonSnapshot before,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken = default)
    {
        if (targetWindowHandle == IntPtr.Zero || !NativeMethods.IsWindow(targetWindowHandle))
        {
            diagnostics["douyinImageDialog"] = "no_window";
            return DouyinImageDialogOutcome.NotFound;
        }

        var stopwatch = Stopwatch.StartNew();
        FoundButton? dialogButton = null;
        while (stopwatch.ElapsedMilliseconds <= AppearTimeoutMs)
        {
            dialogButton = await Task.Run(
                    () => FindConfirmButtons(targetWindowHandle)
                        .Where(button => !before.Buttons.Any(existing => IsSameRect(existing, button.Bounds)))
                        .Cast<FoundButton?>()
                        .FirstOrDefault(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (dialogButton is not null)
            {
                break;
            }

            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        diagnostics["douyinImageDialogSearchMs"] = stopwatch.ElapsedMilliseconds;
        if (dialogButton is not { } found)
        {
            diagnostics["douyinImageDialog"] = "not_found";
            return DouyinImageDialogOutcome.NotFound;
        }

        diagnostics["douyinImageDialog"] = "found";
        diagnostics["douyinImageDialogButtonName"] = found.Name;
        diagnostics["douyinImageDialogButtonRect"] = found.Bounds.ToString();

        var pressed = await Task.Run(() => TryPress(found, targetWindowHandle, diagnostics), cancellationToken)
            .ConfigureAwait(false);
        if (!pressed)
        {
            return DouyinImageDialogOutcome.ConfirmFailed;
        }

        var closeWatch = Stopwatch.StartNew();
        while (closeWatch.ElapsedMilliseconds <= CloseTimeoutMs)
        {
            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
            var stillOpen = await Task.Run(
                    () => FindConfirmButtons(targetWindowHandle).Any(button => IsSameRect(button.Bounds, found.Bounds)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!stillOpen)
            {
                diagnostics["douyinImageDialogClosedMs"] = closeWatch.ElapsedMilliseconds;
                return DouyinImageDialogOutcome.Confirmed;
            }
        }

        diagnostics["douyinImageDialog"] = "still_open_after_press";
        return DouyinImageDialogOutcome.ConfirmFailed;
    }

    private static bool TryPress(FoundButton button, IntPtr targetWindowHandle, IDictionary<string, object?> diagnostics)
    {
        try
        {
            if (button.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) &&
                pattern is InvokePattern invoke)
            {
                invoke.Invoke();
                diagnostics["douyinImageDialogPress"] = "invoke";
                return true;
            }
        }
        catch (Exception ex)
        {
            diagnostics["douyinImageDialogInvokeError"] = ex.GetType().Name;
        }

        // A click lands wherever the cursor is, so only click while Douyin is still in front.
        var foreground = NativeMethods.GetForegroundWindow();
        var root = foreground == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(foreground, NativeMethods.GaRootowner);
        if (foreground != targetWindowHandle && root != targetWindowHandle && !IsSameProcess(foreground, targetWindowHandle))
        {
            diagnostics["douyinImageDialogPress"] = "click_skipped_not_foreground";
            return false;
        }

        var x = (int)(button.Bounds.Left + button.Bounds.Width / 2);
        var y = (int)(button.Bounds.Top + button.Bounds.Height / 2);
        NativeMethods.SetCursorPos(x, y);
        Thread.Sleep(30);
        NativeMethods.mouse_event(NativeMethods.MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
        NativeMethods.mouse_event(NativeMethods.MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
        diagnostics["douyinImageDialogPress"] = "click";
        return true;
    }

    private static List<FoundButton> FindConfirmButtons(IntPtr targetWindowHandle)
    {
        var results = new List<FoundButton>();
        if (targetWindowHandle == IntPtr.Zero || !NativeMethods.IsWindow(targetWindowHandle))
        {
            return results;
        }

        try
        {
            // The dialog is either drawn inside the workbench page or opened as its own window of
            // the same process, so search the workbench and every top-level window of its process.
            NativeMethods.GetWindowThreadProcessId(targetWindowHandle, out var processId);
            var roots = new List<AutomationElement>();
            var workbench = AutomationElement.FromHandle(targetWindowHandle);
            if (workbench is not null)
            {
                roots.Add(workbench);
            }

            if (processId != 0)
            {
                var siblings = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, (int)processId));
                foreach (AutomationElement sibling in siblings)
                {
                    if (sibling.Current.NativeWindowHandle != targetWindowHandle.ToInt32())
                    {
                        roots.Add(sibling);
                    }
                }
            }

            var buttonCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            foreach (var root in roots)
            {
                foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, buttonCondition))
                {
                    var name = NormalizeName(element.Current.Name);
                    var bounds = element.Current.BoundingRectangle;
                    if (bounds.IsEmpty || bounds.Width <= 0 || element.Current.IsOffscreen || !IsConfirmName(name))
                    {
                        continue;
                    }

                    results.Add(new FoundButton(element, name, bounds));
                }
            }
        }
        catch
        {
        }

        return results;
    }

    internal static bool IsConfirmName(string normalizedName)
    {
        if (string.IsNullOrEmpty(normalizedName))
        {
            return false;
        }

        // "发送(1)" / "发送1张" count. Toolbar buttons such as "发送视频" must never match: pressing
        // one would open another dialog. The composer's own 发送 is filtered by the snapshot.
        return ConfirmButtonNames.Contains(normalizedName, StringComparer.Ordinal) ||
               CountedSendRegex.IsMatch(normalizedName);
    }

    private static readonly System.Text.RegularExpressions.Regex CountedSendRegex =
        new(@"^发送[(（]?\d+[张个]?[)）]?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string NormalizeName(string? name)
    {
        return string.Concat((name ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));
    }

    internal static bool IsSameRect(Rect a, Rect b)
    {
        return Math.Abs(a.Left - b.Left) <= 2 &&
               Math.Abs(a.Top - b.Top) <= 2 &&
               Math.Abs(a.Width - b.Width) <= 2 &&
               Math.Abs(a.Height - b.Height) <= 2;
    }

    private static bool IsSameProcess(IntPtr window, IntPtr targetWindowHandle)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var windowProcessId);
        NativeMethods.GetWindowThreadProcessId(targetWindowHandle, out var targetProcessId);
        return windowProcessId != 0 && windowProcessId == targetProcessId;
    }

    private readonly record struct FoundButton(AutomationElement Element, string Name, Rect Bounds);
}
