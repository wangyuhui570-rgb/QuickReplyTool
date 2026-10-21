using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Native;

namespace QuickReply.Desktop.Services;

/// <summary>Pure matching logic for the trigger app filter (unit-testable).</summary>
public static class TriggerAppFilterMatcher
{
    public static IReadOnlyList<TriggerAppRule> Presets { get; } =
    [
        new() { DisplayName = "微信", ProcessName = "WeChat.exe" },
        new() { DisplayName = "微信（新版）", ProcessName = "Weixin.exe" },
        new() { DisplayName = "企业微信", ProcessName = "WXWork.exe" },
        new() { DisplayName = "千牛", ProcessName = "AliWorkbench.exe" },
        new() { DisplayName = "拼多多商家工作台", TitleKeyword = "拼多多" },
        new() { DisplayName = "抖店/飞鸽", TitleKeyword = "抖店" },
        new() { DisplayName = "京东京麦/咚咚", TitleKeyword = "京麦" },
        new() { DisplayName = "Chrome 浏览器", ProcessName = "chrome.exe" },
        new() { DisplayName = "Edge 浏览器", ProcessName = "msedge.exe" }
    ];

    public static string NormalizeProcessName(string? processName)
    {
        var value = (processName ?? string.Empty).Trim();
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        return value.Trim();
    }

    public static bool IsAllowed(
        bool filterEnabled,
        IReadOnlyCollection<TriggerAppRule>? rules,
        string? processName,
        string? windowTitle,
        string? windowClassName)
    {
        if (!filterEnabled)
        {
            return true;
        }

        if (rules is null || rules.Count == 0)
        {
            return false;
        }

        foreach (var rule in rules)
        {
            if (Matches(rule, processName, windowTitle, windowClassName))
            {
                return true;
            }
        }

        return false;
    }

    public static bool Matches(TriggerAppRule? rule, string? processName, string? windowTitle, string? windowClassName)
    {
        if (rule is null)
        {
            return false;
        }

        var ruleProcess = NormalizeProcessName(rule.ProcessName);
        var ruleTitle = rule.TitleKeyword?.Trim() ?? string.Empty;
        var ruleClass = rule.WindowClassKeyword?.Trim() ?? string.Empty;
        if (ruleProcess.Length == 0 && ruleTitle.Length == 0 && ruleClass.Length == 0)
        {
            return false;
        }

        if (ruleProcess.Length > 0 &&
            !string.Equals(ruleProcess, NormalizeProcessName(processName), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ruleTitle.Length > 0 &&
            !(windowTitle ?? string.Empty).Contains(ruleTitle, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ruleClass.Length > 0 &&
            !(windowClassName ?? string.Empty).Contains(ruleClass, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// Fast foreground-window check used on the keyboard hook path. Process names are cached by PID
/// so a check is a few Win32 calls (well under 5 ms).
/// </summary>
public static class TriggerAppFilterGate
{
    private const long ProcessNameCacheTtlMs = 60_000;
    private static readonly ConcurrentDictionary<uint, (string Name, long CachedAt)> ProcessNameCache = new();

    public static bool IsForegroundAllowed(AppSettings? settings, IntPtr windowHandle)
    {
        if (settings is null || !settings.TriggerAppFilterEnabled)
        {
            return true;
        }

        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return true;
        }

        var root = NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRootowner);
        if (root != IntPtr.Zero)
        {
            windowHandle = root;
        }

        return TriggerAppFilterMatcher.IsAllowed(
            true,
            settings.TriggerAppRules,
            GetProcessName(processId),
            GetWindowTitle(windowHandle),
            GetWindowClassName(windowHandle));
    }

    public static string GetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return string.Empty;
        }

        var now = Environment.TickCount64;
        if (ProcessNameCache.TryGetValue(processId, out var cached) && now - cached.CachedAt < ProcessNameCacheTtlMs)
        {
            return cached.Name;
        }

        var name = string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            name = process.ProcessName ?? string.Empty;
        }
        catch
        {
        }

        if (ProcessNameCache.Count > 512)
        {
            ProcessNameCache.Clear();
        }

        ProcessNameCache[processId] = (name, now);
        return name;
    }

    public static string GetWindowTitle(IntPtr windowHandle)
    {
        var length = NativeMethods.GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(windowHandle, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    public static string GetWindowClassName(IntPtr windowHandle)
    {
        var builder = new StringBuilder(256);
        return NativeMethods.GetClassName(windowHandle, builder, builder.Capacity) > 0
            ? builder.ToString().Trim()
            : string.Empty;
    }

    /// <summary>Visible top-level windows of other processes, for the "pick from running apps" dialog.</summary>
    public static IReadOnlyList<(string ProcessName, string Title)> ListRunningWindowedApps()
    {
        var results = new List<(string ProcessName, string Title)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownProcessId = Environment.ProcessId;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == ownProcessId || process.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                var title = process.MainWindowTitle?.Trim() ?? string.Empty;
                if (title.Length == 0 || !NativeMethods.IsWindowVisible(process.MainWindowHandle))
                {
                    continue;
                }

                var name = process.ProcessName + ".exe";
                if (seen.Add(name + "\n" + title))
                {
                    results.Add((name, title));
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return results
            .OrderBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.CurrentCulture)
            .ToList();
    }
}
