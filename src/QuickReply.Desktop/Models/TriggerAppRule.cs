namespace QuickReply.Desktop.Models;

/// <summary>
/// One allowed app for the "only trigger in these apps" filter. A rule matches when every
/// non-empty field matches the foreground window. A rule with all fields empty never matches.
/// </summary>
public sealed class TriggerAppRule
{
    /// <summary>Friendly name shown in settings, e.g. 微信.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Process name, ".exe" optional, case-insensitive (e.g. WeChat.exe).</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>Optional: window title must contain this text (case-insensitive).</summary>
    public string TitleKeyword { get; set; } = string.Empty;

    /// <summary>Optional: window class name must contain this text (case-insensitive).</summary>
    public string WindowClassKeyword { get; set; } = string.Empty;

    public TriggerAppRule Clone() => new()
    {
        DisplayName = DisplayName,
        ProcessName = ProcessName,
        TitleKeyword = TitleKeyword,
        WindowClassKeyword = WindowClassKeyword
    };
}
