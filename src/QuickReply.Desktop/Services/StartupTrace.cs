using System.IO;
using System.Text;

namespace QuickReply.Desktop.Services;

internal static class StartupTrace
{
    private static readonly object SyncRoot = new();
    private static readonly string TraceDirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuickReplyTool",
        "logs",
        "startup");
    private static readonly string TraceFilePath = Path.Combine(TraceDirectoryPath, "latest.log");

    public static void Mark(string stage, string? detail = null)
    {
        try
        {
            Directory.CreateDirectory(TraceDirectoryPath);

            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append(" | ")
                .Append(stage);

            if (!string.IsNullOrWhiteSpace(detail))
            {
                line.Append(" | ").Append(detail.Trim());
            }

            lock (SyncRoot)
            {
                File.AppendAllText(TraceFilePath, line.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    public static void Reset()
    {
        try
        {
            Directory.CreateDirectory(TraceDirectoryPath);
            lock (SyncRoot)
            {
                File.WriteAllText(TraceFilePath, string.Empty, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
