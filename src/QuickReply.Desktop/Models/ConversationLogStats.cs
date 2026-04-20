namespace QuickReply.Desktop.Models;

public readonly record struct ConversationLogStats(
    long SessionLogCount,
    long TotalLogCount,
    int CaptureFileCount,
    long CaptureFolderSizeBytes,
    string DatabasePath,
    string CaptureFolderPath);
