namespace QuickReply.Desktop.Models;

public sealed class ExceptionLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.Now;

    public string Source { get; set; } = string.Empty;

    public string ExceptionType { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string StackTrace { get; set; } = string.Empty;

    public string Context { get; set; } = string.Empty;

    public bool IsTerminating { get; set; }
}
