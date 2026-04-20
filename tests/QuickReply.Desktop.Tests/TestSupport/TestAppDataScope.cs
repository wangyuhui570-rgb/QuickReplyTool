using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests.TestSupport;

internal sealed class TestAppDataScope : IDisposable
{
    public TestAppDataScope()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "QuickReplyTool.Tests", Guid.NewGuid().ToString("N"));
        AppRootPath = Path.Combine(RootPath, "QuickReplyTool");
        LogsRootPath = Path.Combine(AppRootPath, "logs");
        LibraryRootPath = Path.Combine(RootPath, "library");
        FeedbackRootPath = Path.Combine(RootPath, "feedback");
        InstallRootPath = Path.Combine(RootPath, "install");

        Directory.CreateDirectory(AppRootPath);
        Directory.CreateDirectory(LogsRootPath);
        Directory.CreateDirectory(LibraryRootPath);
        Directory.CreateDirectory(FeedbackRootPath);
        Directory.CreateDirectory(InstallRootPath);
    }

    public string RootPath { get; }

    public string AppRootPath { get; }

    public string LogsRootPath { get; }

    public string LibraryRootPath { get; }

    public string FeedbackRootPath { get; }

    public string InstallRootPath { get; }

    public SqliteAppDataStore CreateDataStore()
    {
        return new SqliteAppDataStore(AppRootPath);
    }

    public ConversationLoggingService CreateConversationLoggingService()
    {
        return new ConversationLoggingService(LogsRootPath);
    }

    public string CreateLibraryFile(string relativePath, string content = "sample")
    {
        var fullPath = Path.Combine(LibraryRootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public string CreateFile(string relativePath, string content = "sample")
    {
        var fullPath = Path.Combine(RootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch
        {
        }
    }
}
