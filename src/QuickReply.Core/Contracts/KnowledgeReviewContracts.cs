namespace QuickReply.Core.Contracts;

public sealed class KnowledgeReviewQueueItem
{
    public Guid Id { get; init; }
    public string Status { get; init; } = string.Empty;
    public string CandidateKind { get; init; } = "qa";
    public string Platform { get; init; } = string.Empty;
    public string ProductKey { get; init; } = string.Empty;
    public string QuestionNormalized { get; init; } = string.Empty;
    public string AnswerDraft { get; init; } = string.Empty;
    public List<string> Tags { get; init; } = [];
    public double Confidence { get; init; }
    public string RiskLevel { get; init; } = string.Empty;
    public int SourceCount { get; init; }
    public string ClusterKey { get; init; } = string.Empty;
    public string EvidenceSummary { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class KnowledgeReviewQueueResponse
{
    public List<KnowledgeReviewQueueItem> Items { get; init; } = [];
    public string NextCursor { get; init; } = string.Empty;
    public bool HasMore { get; init; }
}

public sealed class KnowledgeReviewSourceRecord
{
    public Guid Id { get; init; }
    public Guid CandidateId { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string Excerpt { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class KnowledgeReviewSourcesResponse
{
    public Guid CandidateId { get; init; }
    public List<KnowledgeReviewSourceRecord> Items { get; init; } = [];
    public string NextCursor { get; init; } = string.Empty;
    public bool HasMore { get; init; }
}

public sealed class ApproveKnowledgeCandidateRequest
{
    public string AnswerFinal { get; init; } = string.Empty;
    public List<string> Synonyms { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public string UsageNotes { get; init; } = string.Empty;
    public string ReviewComment { get; init; } = string.Empty;
    public string Version { get; init; } = "v1";
    public string Reviewer { get; init; } = string.Empty;
    public string ReviewSessionId { get; init; } = string.Empty;
}

public sealed class RejectKnowledgeCandidateRequest
{
    public string Reason { get; init; } = string.Empty;
    public string ReviewComment { get; init; } = string.Empty;
    public string Reviewer { get; init; } = string.Empty;
    public string ReviewSessionId { get; init; } = string.Empty;
}

public sealed class RemovePublishedKnowledgeCardRequest
{
    public string Reason { get; init; } = string.Empty;
    public string ReviewComment { get; init; } = string.Empty;
    public string Reviewer { get; init; } = string.Empty;
    public string ReviewSessionId { get; init; } = string.Empty;
}

public sealed class KnowledgeReviewHistoryEventRecord
{
    public Guid Id { get; init; }
    public Guid CandidateId { get; init; }
    public string ActionType { get; init; } = string.Empty;
    public string Reviewer { get; init; } = string.Empty;
    public string ReviewSessionId { get; init; } = string.Empty;
    public string ReviewComment { get; init; } = string.Empty;
    public string ReviewReason { get; init; } = string.Empty;
    public string BeforeSnapshotJson { get; init; } = string.Empty;
    public string AfterSnapshotJson { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class KnowledgeReviewHistoryResponse
{
    public Guid CandidateId { get; init; }
    public List<KnowledgeReviewHistoryEventRecord> Items { get; init; } = [];
    public string NextCursor { get; init; } = string.Empty;
    public bool HasMore { get; init; }
}
