using System.IO;
using System.Windows;
using System.Windows.Automation;

namespace QuickReply.Desktop.Services;

internal enum DouyinSendVerifyOutcome
{
    Cleared,
    StillHasContent,
    Indeterminate
}

internal readonly record struct DouyinSendVerifyExpectation(
    string? SentText,
    IReadOnlyList<string>? SentImagePaths);

internal readonly record struct DouyinSendVerifyResult(
    DouyinSendVerifyOutcome Outcome,
    string Method,
    string Detail);

/// <summary>
/// After a Douyin Enter, confirm the composer actually cleared. Empty "success" reports on
/// DESKTOP-NTRMANG (2.1.12) came from firing Enter with no observable send.
/// </summary>
internal interface IDouyinSendProgressVerifier
{
    Task<DouyinSendVerifyResult> VerifyComposerClearedAsync(
        IntPtr targetWindowHandle,
        DouyinSendVerifyExpectation expectation,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken = default);
}

internal sealed class DouyinSendProgressVerifier : IDouyinSendProgressVerifier
{
    internal const int DefaultSettleMs = 400;

    private readonly IKeyboardInputSimulator _keyboard;
    private readonly IClipboardService _clipboard;
    private readonly int _settleMs;

    public DouyinSendProgressVerifier(
        IKeyboardInputSimulator keyboard,
        IClipboardService clipboard,
        int settleMs = DefaultSettleMs)
    {
        _keyboard = keyboard;
        _clipboard = clipboard;
        _settleMs = settleMs;
    }

    public async Task<DouyinSendVerifyResult> VerifyComposerClearedAsync(
        IntPtr targetWindowHandle,
        DouyinSendVerifyExpectation expectation,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken = default)
    {
        if (_settleMs > 0)
        {
            await Task.Delay(_settleMs, cancellationToken).ConfigureAwait(false);
        }

        diagnostics["douyinSendVerifySettleMs"] = _settleMs;

        var uia = await Task.Run(
                () => TryInspectViaAutomation(targetWindowHandle, expectation),
                cancellationToken)
            .ConfigureAwait(false);
        diagnostics["douyinSendVerifyUia"] = uia.Method + ":" + uia.Outcome + ":" + uia.Detail;
        if (uia.Outcome is DouyinSendVerifyOutcome.Cleared or DouyinSendVerifyOutcome.StillHasContent)
        {
            return uia;
        }

        var probe = await TryInspectViaSelectAllCopyAsync(targetWindowHandle, expectation, diagnostics, cancellationToken)
            .ConfigureAwait(false);
        diagnostics["douyinSendVerifyProbe"] = probe.Method + ":" + probe.Outcome + ":" + probe.Detail;
        return probe;
    }

    private static DouyinSendVerifyResult TryInspectViaAutomation(
        IntPtr targetWindowHandle,
        DouyinSendVerifyExpectation expectation)
    {
        try
        {
            if (targetWindowHandle == IntPtr.Zero)
            {
                return new DouyinSendVerifyResult(DouyinSendVerifyOutcome.Indeterminate, "uia", "no_window");
            }

            var root = AutomationElement.FromHandle(targetWindowHandle);
            if (root is null)
            {
                return new DouyinSendVerifyResult(DouyinSendVerifyOutcome.Indeterminate, "uia", "no_root");
            }

            var candidates = new List<AutomationElement>();
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused is not null)
                {
                    candidates.Add(focused);
                }
            }
            catch
            {
            }

            try
            {
                var edits = root.FindAll(
                    TreeScope.Descendants,
                    new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)));
                foreach (AutomationElement edit in edits)
                {
                    candidates.Add(edit);
                }
            }
            catch
            {
            }

            string? bestValue = null;
            foreach (var element in candidates)
            {
                var value = TryReadElementText(element);
                if (value is null)
                {
                    continue;
                }

                bestValue ??= value;
                if (!string.IsNullOrWhiteSpace(expectation.SentText) &&
                    value.Contains(expectation.SentText, StringComparison.Ordinal))
                {
                    return new DouyinSendVerifyResult(
                        DouyinSendVerifyOutcome.StillHasContent,
                        "uia",
                        "text_still_present");
                }
            }

            if (bestValue is not null)
            {
                if (string.IsNullOrWhiteSpace(bestValue))
                {
                    // Empty edit: for text sends this means cleared. For image-only, empty text is
                    // not enough proof (image preview may not show up as Value), so stay indeterminate.
                    if (!string.IsNullOrWhiteSpace(expectation.SentText) &&
                        (expectation.SentImagePaths is null || expectation.SentImagePaths.Count == 0))
                    {
                        return new DouyinSendVerifyResult(DouyinSendVerifyOutcome.Cleared, "uia", "empty_value");
                    }

                    return new DouyinSendVerifyResult(DouyinSendVerifyOutcome.Indeterminate, "uia", "empty_value_image_uncertain");
                }

                if (!string.IsNullOrWhiteSpace(expectation.SentText) &&
                    !bestValue.Contains(expectation.SentText, StringComparison.Ordinal))
                {
                    // Composer has some other text, not ours – treat as cleared of our payload.
                    return new DouyinSendVerifyResult(DouyinSendVerifyOutcome.Cleared, "uia", "other_text");
                }
            }

            return new DouyinSendVerifyResult(DouyinSendVerifyOutcome.Indeterminate, "uia", "no_readable_value");
        }
        catch (Exception ex)
        {
            return new DouyinSendVerifyResult(DouyinSendVerifyOutcome.Indeterminate, "uia", ex.GetType().Name);
        }
    }

    private static string? TryReadElementText(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) &&
                valueObject is ValuePattern valuePattern)
            {
                return valuePattern.Current.Value ?? string.Empty;
            }
        }
        catch
        {
        }

        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textObject) &&
                textObject is TextPattern textPattern)
            {
                return textPattern.DocumentRange.GetText(512) ?? string.Empty;
            }
        }
        catch
        {
        }

        return null;
    }

    private async Task<DouyinSendVerifyResult> TryInspectViaSelectAllCopyAsync(
        IntPtr targetWindowHandle,
        DouyinSendVerifyExpectation expectation,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken)
    {
        ClipboardSnapshot? probeBackup = null;
        try
        {
            // Keep the target focused so Ctrl+A/C hit the composer, not our overlay.
            Native.NativeMethods.SetForegroundWindow(targetWindowHandle);
            await Task.Delay(80, cancellationToken).ConfigureAwait(false);

            probeBackup = await _clipboard.CaptureSnapshotAsync().ConfigureAwait(false);

            // Clear clipboard first so a failed copy cannot look like "still has our text".
            await _clipboard.SetTextAsync(string.Empty, persist: false).ConfigureAwait(false);
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);

            _keyboard.SendCtrlA();
            await Task.Delay(60, cancellationToken).ConfigureAwait(false);
            _keyboard.SendCtrlC();
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);

            var peeked = await PeekClipboardAsync().ConfigureAwait(false);
            diagnostics["douyinSendVerifyPeekTextLength"] = peeked.Text?.Length ?? 0;
            diagnostics["douyinSendVerifyPeekFileCount"] = peeked.Files.Count;

            _keyboard.SendEscape();

            if (!string.IsNullOrWhiteSpace(expectation.SentText))
            {
                if (!string.IsNullOrEmpty(peeked.Text) &&
                    peeked.Text.Contains(expectation.SentText, StringComparison.Ordinal))
                {
                    return new DouyinSendVerifyResult(
                        DouyinSendVerifyOutcome.StillHasContent,
                        "select_all_copy",
                        "text_still_present");
                }
            }

            if (expectation.SentImagePaths is { Count: > 0 })
            {
                var expectedNames = expectation.SentImagePaths
                    .Select(path => Path.GetFileName(path))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var stillHasImage = peeked.Files.Any(file => expectedNames.Contains(Path.GetFileName(file)));
                if (stillHasImage)
                {
                    return new DouyinSendVerifyResult(
                        DouyinSendVerifyOutcome.StillHasContent,
                        "select_all_copy",
                        "image_still_present");
                }
            }

            // Empty selection after Ctrl+A/C is the best signal Chromium gives us.
            if (string.IsNullOrEmpty(peeked.Text) && peeked.Files.Count == 0)
            {
                return new DouyinSendVerifyResult(
                    DouyinSendVerifyOutcome.Cleared,
                    "select_all_copy",
                    "empty_selection");
            }

            // Clipboard has unrelated content – not our payload.
            if (!string.IsNullOrWhiteSpace(expectation.SentText) &&
                (expectation.SentImagePaths is null || expectation.SentImagePaths.Count == 0) &&
                (string.IsNullOrEmpty(peeked.Text) ||
                 !peeked.Text.Contains(expectation.SentText, StringComparison.Ordinal)))
            {
                return new DouyinSendVerifyResult(
                    DouyinSendVerifyOutcome.Cleared,
                    "select_all_copy",
                    "text_absent");
            }

            return new DouyinSendVerifyResult(
                DouyinSendVerifyOutcome.Indeterminate,
                "select_all_copy",
                "unrelated_clipboard");
        }
        catch (Exception ex)
        {
            return new DouyinSendVerifyResult(
                DouyinSendVerifyOutcome.Indeterminate,
                "select_all_copy",
                ex.GetType().Name);
        }
        finally
        {
            if (probeBackup is { CaptureSucceeded: true, CanRestore: true })
            {
                try
                {
                    await _clipboard.RestoreSnapshotAsync(probeBackup).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private static Task<(string? Text, IReadOnlyList<string> Files)> PeekClipboardAsync()
    {
        return InvokeOnStaAsync(() =>
        {
            string? text = null;
            try
            {
                if (Clipboard.ContainsText())
                {
                    text = Clipboard.GetText();
                }
            }
            catch
            {
            }

            string[] files = [];
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                }
            }
            catch
            {
            }

            return (text, (IReadOnlyList<string>)files);
        });
    }

    private static Task<T> InvokeOnStaAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}
