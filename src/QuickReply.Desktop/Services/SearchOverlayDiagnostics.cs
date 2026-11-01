using System.IO;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using QuickReply.Desktop.Native;

namespace QuickReply.Desktop.Services;

internal static class SearchOverlayDiagnostics
{
    private static readonly object SyncRoot = new();
    private static readonly bool IsEnabled =
#if DEBUG
        true;
#else
        false;
#endif

    internal static void Log(string source, string eventName, IDictionary<string, object?>? data = null)
    {
        // Release builds keep compose events (one line per send) so live send-order issues can be
        // diagnosed from %APPDATA%\QuickReplyTool\logs; the noisy overlay events stay DEBUG-only.
        if (!IsEnabled && !string.Equals(source, "message_compose", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["occurredAt"] = DateTimeOffset.Now.ToString("O"),
                ["source"] = source,
                ["event"] = eventName,
                ["processId"] = Environment.ProcessId
            };

            if (data is not null)
            {
                foreach (var entry in data)
                {
                    payload[entry.Key] = entry.Value;
                }
            }

            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QuickReplyTool",
                "logs",
                "search-overlay");
            Directory.CreateDirectory(logDirectory);

            var logPath = Path.Combine(logDirectory, $"{DateTime.Now:yyyyMMdd}.jsonl");
            var line = JsonSerializer.Serialize(payload);
            lock (SyncRoot)
            {
                File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Ignore diagnostics failures.
        }
    }

    internal static Dictionary<string, object?> CreateWindowSnapshot(string prefix, IntPtr windowHandle)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddWindowSnapshot(payload, prefix, windowHandle);
        return payload;
    }

    internal static void AddWindowSnapshot(IDictionary<string, object?> payload, string prefix, IntPtr windowHandle)
    {
        payload[$"{prefix}Handle"] = FormatHandle(windowHandle);

        if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
        {
            payload[$"{prefix}Exists"] = false;
            return;
        }

        payload[$"{prefix}Exists"] = true;
        payload[$"{prefix}Visible"] = NativeMethods.IsWindowVisible(windowHandle);
        payload[$"{prefix}Minimized"] = NativeMethods.IsIconic(windowHandle);
        payload[$"{prefix}Title"] = GetWindowTitle(windowHandle);
        payload[$"{prefix}Class"] = GetWindowClass(windowHandle);
        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        payload[$"{prefix}ProcessId"] = processId;
        payload[$"{prefix}ProcessName"] = GetProcessName(processId);
    }

    internal static void AddGuiThreadSnapshot(IDictionary<string, object?> payload, string prefix, uint threadId)
    {
        payload[$"{prefix}ThreadId"] = threadId;
        if (threadId == 0)
        {
            payload[$"{prefix}Available"] = false;
            return;
        }

        var guiInfo = new NativeMethods.GUITHREADINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        if (!NativeMethods.GetGUIThreadInfo(threadId, ref guiInfo))
        {
            payload[$"{prefix}Available"] = false;
            return;
        }

        payload[$"{prefix}Available"] = true;
        payload[$"{prefix}Flags"] = guiInfo.flags;
        AddWindowSnapshot(payload, $"{prefix}Active", guiInfo.hwndActive);
        AddWindowSnapshot(payload, $"{prefix}Focus", guiInfo.hwndFocus);
        AddWindowSnapshot(payload, $"{prefix}Capture", guiInfo.hwndCapture);
        AddWindowSnapshot(payload, $"{prefix}Caret", guiInfo.hwndCaret);
        payload[$"{prefix}CaretRect"] = $"{guiInfo.rcCaret.Left},{guiInfo.rcCaret.Top},{guiInfo.rcCaret.Right},{guiInfo.rcCaret.Bottom}";
    }

    internal static string FormatHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return "0x0";
        }

        return $"0x{unchecked((ulong)handle.ToInt64()):X}";
    }

    private static string GetWindowTitle(IntPtr windowHandle)
    {
        var length = Math.Max(256, NativeMethods.GetWindowTextLength(windowHandle) + 1);
        var builder = new StringBuilder(length);
        NativeMethods.GetWindowText(windowHandle, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    private static string GetWindowClass(IntPtr windowHandle)
    {
        var builder = new StringBuilder(256);
        NativeMethods.GetClassName(windowHandle, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    private static string GetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }
}
