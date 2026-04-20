namespace QuickReply.Desktop.Models;

public sealed class IssueReportPackage
{
    public string IssueId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string ClientId { get; init; } = string.Empty;

    public string AppVersion { get; init; } = string.Empty;

    public string MachineName { get; init; } = string.Empty;

    public string ProcessPath { get; init; } = string.Empty;

    public int ProcessId { get; init; }

    public string UserDescription { get; init; } = string.Empty;

    public string ServerBaseUrl { get; init; } = string.Empty;

    public Dictionary<string, string?> SettingsSnapshot { get; init; } = [];

    public IssueReportPathSnapshot Paths { get; init; } = new();

    public IssueReportDiagnostics Diagnostics { get; init; } = new();
}

public sealed class IssueReportPathSnapshot
{
    public string DataRootPath { get; init; } = string.Empty;

    public string LogsRootPath { get; init; } = string.Empty;

    public string FeedbackRootPath { get; init; } = string.Empty;

    public string DatabasePath { get; init; } = string.Empty;

    public string ConversationLogDatabasePath { get; init; } = string.Empty;

    public string ScreenshotToolSettingsPath { get; init; } = string.Empty;
}

public sealed class IssueReportDiagnostics
{
    public string ScreenshotToolSettingsText { get; init; } = string.Empty;

    public string StartupLogText { get; init; } = string.Empty;

    public string ExceptionLogTail { get; init; } = string.Empty;

    public string SearchOverlayLogTail { get; init; } = string.Empty;

    public string LogDirectorySnapshot { get; init; } = string.Empty;

    public IReadOnlyList<IssueReportConversationEntry> RecentConversationEntries { get; init; } = [];
}

public sealed class IssueReportConversationEntry
{
    public string OccurredAt { get; init; } = string.Empty;

    public string TriggerSource { get; init; } = string.Empty;

    public string ActionMode { get; init; } = string.Empty;

    public string PayloadType { get; init; } = string.Empty;

    public bool ResultSuccess { get; init; }

    public string ResultMessage { get; init; } = string.Empty;

    public string TargetProcessName { get; init; } = string.Empty;

    public string PhraseTitle { get; init; } = string.Empty;

    public string FileDisplayName { get; init; } = string.Empty;
}

public sealed record IssueReportIndexEntry
{
    public string IssueId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string UserDescription { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public string PackagePath { get; init; } = string.Empty;

    public bool UploadAttempted { get; init; }

    public bool UploadSucceeded { get; init; }

    public DateTimeOffset? LastUploadAttemptAt { get; init; }

    public string UploadMessage { get; init; } = string.Empty;

    public string ServerIssueId { get; init; } = string.Empty;

    public string RemoteStatus { get; init; } = string.Empty;

    public string AnalysisStatus { get; init; } = string.Empty;

    public string PatchVersion { get; init; } = string.Empty;

    public string PatchDownloadUrl { get; init; } = string.Empty;

    public string PatchReleaseNotes { get; init; } = string.Empty;

    public string InstalledPatchVersion { get; init; } = string.Empty;
}

public sealed class IssueStatusBatchRequest
{
    public IReadOnlyList<string> IssueIds { get; init; } = [];
}

public sealed class IssueStatusBatchResponse
{
    public bool Success { get; init; }

    public IReadOnlyList<IssueStatusSnapshot> Items { get; init; } = [];

    public string Message { get; init; } = string.Empty;
}

public sealed class IssueStatusSnapshot
{
    public string IssueId { get; init; } = string.Empty;

    public string ServerIssueId { get; init; } = string.Empty;

    public string RemoteStatus { get; init; } = string.Empty;

    public string AnalysisStatus { get; init; } = string.Empty;

    public string PatchVersion { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public string ReleaseNotes { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
