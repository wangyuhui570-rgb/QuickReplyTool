namespace QuickReply.Desktop.Models;

public sealed class CapturedConversationContext
{
    public IntPtr TargetWindowHandle { get; init; }

    public string PlatformHint { get; init; } = string.Empty;

    public string WindowTitle { get; init; } = string.Empty;

    public string WindowClassName { get; init; } = string.Empty;

    public string ProcessName { get; init; } = string.Empty;

    public string ProcessPath { get; init; } = string.Empty;

    public int Left { get; init; }

    public int Top { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string CaptureStatus { get; init; } = string.Empty;

    public string CapturedText { get; init; } = string.Empty;

    public string ScreenshotPath { get; init; } = string.Empty;

    public IReadOnlyList<string> RawLines { get; init; } = [];
}
