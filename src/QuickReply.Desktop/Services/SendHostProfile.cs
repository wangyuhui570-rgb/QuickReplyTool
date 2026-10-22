namespace QuickReply.Desktop.Services;

internal enum SendHostKind
{
    GenericDesktopInput,
    BrowserHostedInput,
    PddWorkbench,
    QianNiu,
    DouyinWorkbench
}

internal sealed record SendHostProfile(
    SendHostKind Kind,
    string Name,
    int ActivationStableMs,
    int ActivationTimeoutMs,
    int RetryBackoffMs,
    int PollIntervalMs,
    // *StableMs: fixed settle after paste / before send on the happy path.
    // *TimeoutMs: recovery budget used only when the target lost focus.
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
        ActivationStableMs: 20,
        ActivationTimeoutMs: 180,
        RetryBackoffMs: 60,
        PollIntervalMs: 25,
        TextPasteStableMs: 15,
        TextPasteTimeoutMs: 220,
        FilePasteStableMs: 80,
        FilePasteTimeoutMs: 1100,
        TextSendStableMs: 10,
        TextSendTimeoutMs: 220,
        RichSendStableMs: 60,
        RichSendTimeoutMs: 520,
        ClipboardRestoreDelayMs: 120,
        PreferConservativeRichPath: false,
        PreferFastTextPath: false);

    public static SendHostProfile BrowserHostedInput { get; } = new(
        SendHostKind.BrowserHostedInput,
        "browser_hosted_input",
        ActivationStableMs: 15,
        ActivationTimeoutMs: 140,
        RetryBackoffMs: 40,
        PollIntervalMs: 20,
        TextPasteStableMs: 10,
        TextPasteTimeoutMs: 120,
        FilePasteStableMs: 60,
        FilePasteTimeoutMs: 900,
        TextSendStableMs: 0,
        TextSendTimeoutMs: 120,
        RichSendStableMs: 50,
        RichSendTimeoutMs: 360,
        ClipboardRestoreDelayMs: 80,
        PreferConservativeRichPath: false,
        PreferFastTextPath: true);

    public static SendHostProfile PddWorkbench { get; } = new(
        SendHostKind.PddWorkbench,
        "pdd_workbench",
        ActivationStableMs: 30,
        ActivationTimeoutMs: 260,
        RetryBackoffMs: 90,
        PollIntervalMs: 30,
        TextPasteStableMs: 40,
        TextPasteTimeoutMs: 260,
        FilePasteStableMs: 150,
        FilePasteTimeoutMs: 1500,
        TextSendStableMs: 40,
        TextSendTimeoutMs: 320,
        RichSendStableMs: 120,
        RichSendTimeoutMs: 680,
        ClipboardRestoreDelayMs: 900,
        PreferConservativeRichPath: true,
        PreferFastTextPath: false);

    public static SendHostProfile QianNiu { get; } = new(
        SendHostKind.QianNiu,
        "qianniu",
        ActivationStableMs: 30,
        ActivationTimeoutMs: 240,
        RetryBackoffMs: 80,
        PollIntervalMs: 30,
        TextPasteStableMs: 30,
        TextPasteTimeoutMs: 240,
        FilePasteStableMs: 120,
        FilePasteTimeoutMs: 1300,
        TextSendStableMs: 30,
        TextSendTimeoutMs: 280,
        RichSendStableMs: 100,
        RichSendTimeoutMs: 620,
        ClipboardRestoreDelayMs: 800,
        PreferConservativeRichPath: true,
        PreferFastTextPath: false);

    public static SendHostProfile DouyinWorkbench { get; } = new(
        SendHostKind.DouyinWorkbench,
        "douyin_workbench",
        ActivationStableMs: 60,
        ActivationTimeoutMs: 340,
        RetryBackoffMs: 100,
        PollIntervalMs: 35,
        TextPasteStableMs: 70,
        TextPasteTimeoutMs: 340,
        FilePasteStableMs: 900,
        FilePasteTimeoutMs: 2600,
        TextSendStableMs: 100,
        TextSendTimeoutMs: 420,
        RichSendStableMs: 250,
        RichSendTimeoutMs: 1200,
        ClipboardRestoreDelayMs: 900,
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

        if (IsDouyinWorkbench(targetInfo, combined))
        {
            return SendHostProfile.DouyinWorkbench;
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

    private static bool IsDouyinWorkbench(WindowDockTargetInfo targetInfo, string combinedLower)
    {
        var processIdentity = string.Join(
            "\n",
            [
                targetInfo.WindowClassName,
                targetInfo.ProcessName,
                targetInfo.ProcessPath
            ]);

        if (ContainsAny(processIdentity, "doudian", "feige"))
        {
            return true;
        }

        var isBrowserShell =
            ContainsAny(targetInfo.WindowClassName, "Chrome_WidgetWin", "MozillaWindowClass") &&
            ContainsAny(targetInfo.ProcessName, "chrome", "msedge", "firefox", "brave", "opera");

        // Browser tabs: only trust the title so ordinary Douyin video pages stay browser_hosted_input.
        var classificationText = isBrowserShell ? targetInfo.Title : combinedLower;

        if (ContainsAny(
                classificationText,
                "抖音工作台",
                "抖店",
                "飞鸽",
                "巨量百应",
                "doudian",
                "feige",
                "jinritemai"))
        {
            return true;
        }

        return ContainsAny(classificationText, "抖音", "douyin") &&
               ContainsAny(
                   classificationText,
                   "客服",
                   "商家",
                   "工作台",
                   "接待",
                   "会话",
                   "卖家",
                   "workbench",
                   "seller",
                   "merchant",
                   "chat",
                   "feige");
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
