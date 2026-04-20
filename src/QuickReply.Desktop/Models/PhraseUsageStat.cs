namespace QuickReply.Desktop.Models;

public sealed class PhraseUsageStat
{
    public Guid PhraseId { get; init; }

    public int SendCount { get; set; }

    public DateTimeOffset? LastSentAt { get; set; }
}
