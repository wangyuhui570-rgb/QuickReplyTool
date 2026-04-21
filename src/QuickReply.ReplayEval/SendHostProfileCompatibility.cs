namespace QuickReply.Desktop.Services;

public sealed class WindowDockTargetInfo
{
    public required IntPtr Handle { get; init; }

    public required string Title { get; init; }

    public string WindowClassName { get; init; } = string.Empty;

    public string ProcessName { get; init; } = string.Empty;

    public string ProcessPath { get; init; } = string.Empty;

    public required int Left { get; init; }

    public required int Top { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }
}

internal enum SendHostKind
{
    GenericDesktopInput,
    BrowserHostedInput,
    PddWorkbench,
    QianNiu
}

internal sealed record SendHostProfile(
    SendHostKind Kind,
    string Name)
{
    public static SendHostProfile GenericDesktopInput { get; } =
        new(SendHostKind.GenericDesktopInput, "generic_desktop_input");

    public static SendHostProfile BrowserHostedInput { get; } =
        new(SendHostKind.BrowserHostedInput, "browser_hosted_input");

    public static SendHostProfile PddWorkbench { get; } =
        new(SendHostKind.PddWorkbench, "pdd_workbench");

    public static SendHostProfile QianNiu { get; } =
        new(SendHostKind.QianNiu, "qianniu");
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
            new[]
            {
                targetInfo.Title,
                targetInfo.WindowClassName,
                targetInfo.ProcessName,
                targetInfo.ProcessPath
            })
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
