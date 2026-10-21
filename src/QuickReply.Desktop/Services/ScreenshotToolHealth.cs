using System.IO;
using System.Text;
using System.Text.Json;

namespace QuickReply.Desktop.Services;

public enum ScreenshotToolHealthAction
{
    None,
    Restart,
    ReportHotkeyFailure
}

public sealed record ScreenshotToolStatus(
    string Hotkey,
    bool Registered,
    string RegisteredHotkey,
    string Error,
    int ErrorCode,
    long HeartbeatAgeMs);

/// <summary>Pure decision logic for the screenshot tool watchdog (unit-testable).</summary>
public static class ScreenshotToolHealthEvaluator
{
    public const long HungHeartbeatThresholdMs = 20_000;
    public const int MaxRestartsPerWindow = 3;
    public static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);

    /// <param name="listening">The single-instance port accepts connections.</param>
    /// <param name="status">Parsed "status" reply; null when the tool is an older build without it.</param>
    public static ScreenshotToolHealthAction Evaluate(bool listening, ScreenshotToolStatus? status)
    {
        if (!listening)
        {
            return ScreenshotToolHealthAction.Restart;
        }

        if (status is null)
        {
            return ScreenshotToolHealthAction.None;
        }

        if (status.HeartbeatAgeMs > HungHeartbeatThresholdMs)
        {
            return ScreenshotToolHealthAction.Restart;
        }

        return status.Registered ? ScreenshotToolHealthAction.None : ScreenshotToolHealthAction.ReportHotkeyFailure;
    }

    public static ScreenshotToolStatus? ParseStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new ScreenshotToolStatus(
                GetString(root, "hotkey"),
                root.TryGetProperty("registered", out var registered) && registered.ValueKind == JsonValueKind.True,
                GetString(root, "registered_hotkey"),
                GetString(root, "error"),
                root.TryGetProperty("error_code", out var code) && code.TryGetInt32(out var codeValue) ? codeValue : 0,
                root.TryGetProperty("heartbeat_age_ms", out var age) && age.TryGetInt64(out var ageValue) ? ageValue : 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>True when another restart is allowed; prunes timestamps older than the window.</summary>
    public static bool CanRestart(List<DateTimeOffset> recentRestarts, DateTimeOffset now)
    {
        recentRestarts.RemoveAll(time => now - time > RestartWindow);
        return recentRestarts.Count < MaxRestartsPerWindow;
    }

    /// <summary>Suggest a hotkey that common chat apps (微信/企业微信 Alt+A, QQ Ctrl+Alt+A) do not use.</summary>
    public static string SuggestFallbackHotkey(string? currentHotkey)
    {
        var normalized = (currentHotkey ?? string.Empty).Replace(" ", string.Empty);
        foreach (var candidate in new[] { "Ctrl+Shift+A", "Alt+Shift+A", "Ctrl+Alt+S", "Ctrl+Shift+X" })
        {
            if (!string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return "Ctrl+Shift+X";
    }

    public static string BuildHotkeyFailureMessage(ScreenshotToolStatus status)
    {
        var reason = string.IsNullOrWhiteSpace(status.Error)
            ? $"截图快捷键 {status.Hotkey} 注册失败"
            : status.Error;
        return $"{reason}。可关闭占用它的软件里的同名快捷键，或在设置中改为 {SuggestFallbackHotkey(status.Hotkey)}。";
    }
}

/// <summary>Appends host-side screenshot events to logs/screenshot/host-YYYYMMDD.jsonl.</summary>
public static class ScreenshotToolLog
{
    private static readonly object SyncRoot = new();

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuickReplyTool",
        "logs",
        "screenshot");

    public static void Write(string eventName, object? details = null)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["ts"] = DateTimeOffset.Now.ToString("O"),
                ["source"] = "host",
                ["event"] = eventName,
                ["details"] = details
            };
            var line = JsonSerializer.Serialize(payload) + Environment.NewLine;
            lock (SyncRoot)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(Path.Combine(DirectoryPath, $"host-{DateTime.Now:yyyyMMdd}.jsonl"), line, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
