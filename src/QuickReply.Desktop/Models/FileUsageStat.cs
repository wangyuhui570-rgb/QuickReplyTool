namespace QuickReply.Desktop.Models;

public sealed class FileUsageStat
{
    public Guid FileItemId { get; init; }

    public int UseCount { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
