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

    /// <summary>
    /// After an image paste (before Enter): StillHasContent = image/preview detected;
    /// Cleared = composer looks empty (paste missed); Indeterminate = cannot tell.
    /// </summary>
    Task<DouyinSendVerifyResult> ConfirmComposerHasImageAsync(
        IntPtr targetWindowHandle,
        IReadOnlyList<string> imagePaths,
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

    public async Task<DouyinSendVerifyResult> ConfirmComposerHasImageAsync(
        IntPtr targetWindowHandle,
        IReadOnlyList<string> imagePaths,
        IDictionary<string, object?> diagnostics,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);

        var expectation = new DouyinSendVerifyExpectation(SentText: null, SentImagePaths: imagePaths);
        var uia = await Task.Run(
                () => TryDetectImageViaAutomation(targetWindowHandle, imagePaths),
                cancellationToken)
            .ConfigureAwait(false);
        diagnostics["douyinImagePasteConfirmUia"] = uia.Method + ":" + uia.Outcome + ":" + uia.Detail;
        if (uia.Outcome is DouyinSendVerifyOutcome.StillHasContent or DouyinSendVerifyOutcome.Cleared)
        {
            return uia;
        }

        var probe = await TryInspectViaSelectAllCopyAsync(targetWindowHandle, expectation, diagnostics, cancellationToken)
            .ConfigureAwait(false);
        diagnostics["douyinImagePasteConfirmProbe"] = probe.Method + ":" + probe.Outcome + ":" + probe.Detail;

        // Map probe outcomes for "has image?" semantics:
        // StillHasContent (image_still_present) → confirmed.
        // Cleared / empty → not present.
        // Indeterminate empty_selection_image_uncertain → not present enough to Enter safely.
        if (probe.Outcome == DouyinSendVerifyOutcome.StillHasContent)
        {
            return probe;
        }

        if (probe.Detail is "empty_selection" or "empty_selection_image_uncertain" or "text_absent")
        {
            return new DouyinSendVerifyResult(DouyinSendVerifyOutcome.Cleared, probe.Method, "composer_empty_after_paste");
        }

        return new DouyinSendVerifyResult(DouyinSendVerifyOutcome.Indeterminate, probe.Method, probe.Detail);
    }

    private static DouyinSendVerifyResult TryDetectImageViaAutomation(
        IntPtr targetWindowHandle,
        IReadOnlyList<string> imagePaths)
    {
        try
        {
            if (targetWindowHandle == IntPtr.Zero)
            {
                return new DouyinSendVerifyResult(DouyinSendVerifyOutcome.Indeterminate, "uia_image", "no_window");
            }

            var root = AutomationElement.FromHandle(targetWindowHandle);
            if (root is null)
            {
                return new DouyinSendVerifyResult(DouyinSendVerifyOutcome.Indeterminate, "uia_image", "no_root");
            }

            if (!Native.NativeMethods.GetWindowRect(targetWindowHandle, out var windowRect))
            {
                return new DouyinSendVerifyResult(DouyinSendVerifyOutcome.Indeterminate, "uia_image", "no_rect");
            }

            var windowBottom = windowRect.Bottom;
            var windowTop = windowRect.Top;
            var height = Math.Max(windowBottom - windowTop, 1);
            var lowerBandTop = windowTop + height * 0.55;

            var expectedNames = imagePaths
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Image / thumbnail / attachment chips usually sit in the lower half near the composer.
            var imageCondition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Thumb));

            var candidates = root.FindAll(TreeScope.Descendants, imageCondition);
            foreach (AutomationElement element in candidates)
            {
                try
                {
                    var rect = element.Current.BoundingRectangle;
                    if (rect.IsEmpty || rect.Bottom < lowerBandTop)
                    {
                        continue;
                    }

                    var name = element.Current.Name ?? string.Empty;
                    var help = element.Current.HelpText ?? string.Empty;
                    if (expectedNames.Count > 0 &&
                        expectedNames.Any(n =>
                            name.Contains(n, StringComparison.OrdinalIgnoreCase) ||
                            help.Contains(n, StringComparison.OrdinalIgnoreCase)))
                    {
                        return new DouyinSendVerifyResult(
                            DouyinSendVerifyOutcome.StillHasContent,
                            "uia_image",
                            "name_match");
                    }

                    if (Equals(element.Current.ControlType, ControlType.Image) &&
                        rect.Width >= 24 &&
                        rect.Height >= 24)
                    {
                        return new DouyinSendVerifyResult(
                            DouyinSendVerifyOutcome.StillHasContent,
                            "uia_image",
                            "image_in_composer_band");
                    }
                }
                catch
                {
                }
            }

            // Also: edit value non-empty after paste can mean a caption/alt stuck in composer.
            var edits = root.FindAll(
                TreeScope.Descendants,
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)));
            foreach (AutomationElement edit in edits)
            {
                var value = TryReadElementText(edit);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return new DouyinSendVerifyResult(
                        DouyinSendVerifyOutcome.StillHasContent,
                        "uia_image",
                        "non_empty_value");
                }
            }

            return new DouyinSendVerifyResult(DouyinSendVerifyOutcome.Cleared, "uia_image", "no_image_preview");
        }
        catch (Exception ex)
        {
            return new DouyinSendVerifyResult(DouyinSendVerifyOutcome.Indeterminate, "uia_image", ex.GetType().Name);
        }
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

            // Empty selection after Ctrl+A/C is the best signal Chromium gives us for text.
            // For image-only stages it is ambiguous (never pasted vs already sent), so do not
            // treat it as Cleared — that caused false success when Enter hit an empty composer.
            if (string.IsNullOrEmpty(peeked.Text) && peeked.Files.Count == 0)
            {
                if (expectation.SentImagePaths is { Count: > 0 })
                {
                    return new DouyinSendVerifyResult(
                        DouyinSendVerifyOutcome.Indeterminate,
                        "select_all_copy",
                        "empty_selection_image_uncertain");
                }

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
                if (System.Windows.Clipboard.ContainsText())
                {
                    text = System.Windows.Clipboard.GetText();
                }
            }
            catch
            {
            }

            string[] files = [];
            try
            {
                if (System.Windows.Clipboard.ContainsFileDropList())
                {
                    files = System.Windows.Clipboard.GetFileDropList().Cast<string>().ToArray();
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
