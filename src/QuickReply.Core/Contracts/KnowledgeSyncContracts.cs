namespace QuickReply.Core.Contracts;

public sealed class PublishedKnowledgeSyncResponse
{
    public string KnowledgeVersion { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; }
    public long LatestRevision { get; init; }
    public string NextCursor { get; init; } = string.Empty;
    public bool HasMore { get; init; }
    public List<PublishedKnowledgeCardSyncItem> Cards { get; init; } = [];
    public List<Guid> RemovedCardIds { get; init; } = [];
}

public sealed class PublishedKnowledgeCardSyncItem
{
    public Guid Id { get; init; }
    public string CandidateKind { get; init; } = "qa";
    public string Platform { get; init; } = string.Empty;
    public string ProductKey { get; init; } = string.Empty;
    public string Question { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public List<string> Synonyms { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public string UsageNotes { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public long Revision { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
