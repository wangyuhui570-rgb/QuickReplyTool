namespace QuickReply.Desktop.Services;

internal enum SendHostKind
{
    GenericDesktopInput,
    BrowserHostedInput,
    PddWorkbench,
    QianNiu
}

internal sealed record SendHostProfile(
    SendHostKind Kind,
    string Name,
    int ActivationStableMs,
    int ActivationTimeoutMs,
    int RetryBackoffMs,
    int PollIntervalMs,
    int TextPasteStableMs,
    int TextPasteTimeoutMs,
    int FilePasteStableMs,
    int FilePasteTimeoutMs,
    int TextSendStableMs,
    int TextSendTimeoutMs,
    int RichSendStableMs,
    int RichSendTimeoutMs,
    int ClipboardRestoreDelayMs,
    bool PreferConservativeRichPath,
    bool PreferFastTextPath)
{
    public static SendHostProfile GenericDesktopInput { get; } = new(
        SendHostKind.GenericDesktopInput,
        "generic_desktop_input",
        ActivationStableMs: 40,
        ActivationTimeoutMs: 180,
        RetryBackoffMs: 60,
        PollIntervalMs: 25,
        TextPasteStableMs: 40,
        TextPasteTimeoutMs: 220,
        FilePasteStableMs: 320,
        FilePasteTimeoutMs: 1100,
        TextSendStableMs: 40,
        TextSendTimeoutMs: 220,
        RichSendStableMs: 140,
        RichSendTimeoutMs: 520,
        ClipboardRestoreDelayMs: 120,
        PreferConservativeRichPath: false,
        PreferFastTextPath: false);

    public static SendHostProfile BrowserHostedInput { get; } = new(
        SendHostKind.BrowserHostedInput,
        "browser_hosted_input",
        ActivationStableMs: 30,
        ActivationTimeoutMs: 140,
        RetryBackoffMs: 40,
        PollIntervalMs: 20,
        TextPasteStableMs: 25,
        TextPasteTimeoutMs: 120,
        FilePasteStableMs: 260,
        FilePasteTimeoutMs: 900,
        TextSendStableMs: 20,
        TextSendTimeoutMs: 120,
        RichSendStableMs: 100,
        RichSendTimeoutMs: 360,
        ClipboardRestoreDelayMs: 80,
        PreferConservativeRichPath: false,
        PreferFastTextPath: true);

    public static SendHostProfile PddWorkbench { get; } = new(
        SendHostKind.PddWorkbench,
        "pdd_workbench",
        ActivationStableMs: 60,
        ActivationTimeoutMs: 260,
        RetryBackoffMs: 90,
        PollIntervalMs: 30,
        TextPasteStableMs: 70,
        TextPasteTimeoutMs: 260,
        FilePasteStableMs: 520,
        FilePasteTimeoutMs: 1500,
        TextSendStableMs: 100,
        TextSendTimeoutMs: 320,
        RichSendStableMs: 200,
        RichSendTimeoutMs: 680,
        ClipboardRestoreDelayMs: 900,
        PreferConservativeRichPath: true,
        PreferFastTextPath: false);

    public static SendHostProfile QianNiu { get; } = new(
        SendHostKind.QianNiu,
        "qianniu",
        ActivationStableMs: 60,
        ActivationTimeoutMs: 240,
        RetryBackoffMs: 80,
        PollIntervalMs: 30,
        TextPasteStableMs: 60,
        TextPasteTimeoutMs: 240,
        FilePasteStableMs: 420,
        FilePasteTimeoutMs: 1300,
        TextSendStableMs: 80,
        TextSendTimeoutMs: 280,
        RichSendStableMs: 180,
        RichSendTimeoutMs: 620,
        ClipboardRestoreDelayMs: 800,
        PreferConservativeRichPath: true,
        PreferFastTextPath: false);
}

internal static class SendHostProfileResolver
{
    public static SendHostProfile Resolve(WindowDockTargetInfo? targetInfo)
    {
        if (targetInfo is null)
        {
            return SendHostProfile.GenericDesktopInput;
        }

        var combined = string.Join(
            "\n",
            [
                targetInfo.Title,
                targetInfo.WindowClassName,
                targetInfo.ProcessName,
                targetInfo.ProcessPath
            ])
            .ToLowerInvariant();

        if (combined.Contains("拼多多", StringComparison.Ordinal) ||
            combined.Contains("pdd", StringComparison.Ordinal) ||
            combined.Contains("pinduoduo", StringComparison.Ordinal))
        {
            return SendHostProfile.PddWorkbench;
        }

        if (combined.Contains("千牛", StringComparison.Ordinal) ||
            combined.Contains("qianniu", StringComparison.Ordinal) ||
            combined.Contains("aliworkbench", StringComparison.Ordinal))
        {
            return SendHostProfile.QianNiu;
        }

        if (combined.Contains("chrome", StringComparison.Ordinal) ||
            combined.Contains("msedge", StringComparison.Ordinal) ||
            combined.Contains("firefox", StringComparison.Ordinal) ||
            combined.Contains("brave", StringComparison.Ordinal) ||
            combined.Contains("opera", StringComparison.Ordinal) ||
            combined.Contains("chrome_widgetwin", StringComparison.Ordinal) ||
            combined.Contains("mozillawindowclass", StringComparison.Ordinal))
        {
            return SendHostProfile.BrowserHostedInput;
        }

        return SendHostProfile.GenericDesktopInput;
    }
}
