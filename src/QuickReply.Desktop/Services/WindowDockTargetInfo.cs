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

    public int Right => Left + Width;

    public int Bottom => Top + Height;
}
