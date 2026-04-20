namespace QuickReply.Desktop.Models;

public sealed class KnowledgeReviewQueueItem
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public string CandidateKind { get; set; } = "qa";
    public string Platform { get; set; } = string.Empty;
    public string ProductKey { get; set; } = string.Empty;
    public string QuestionNormalized { get; set; } = string.Empty;
    public string AnswerDraft { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public double Confidence { get; set; }
    public string RiskLevel { get; set; } = string.Empty;
    public int SourceCount { get; set; }
    public string ClusterKey { get; set; } = string.Empty;
    public string EvidenceSummary { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class KnowledgeReviewQueueResponse
{
    public List<KnowledgeReviewQueueItem> Items { get; set; } = [];
    public string NextCursor { get; set; } = string.Empty;
    public bool HasMore { get; set; }
}

public sealed class KnowledgeReviewSourceRecord
{
    public Guid Id { get; set; }
    public Guid CandidateId { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string Excerpt { get; set; } = string.Empty;
}

public sealed class KnowledgeReviewSourcesResponse
{
    public Guid CandidateId { get; set; }
    public List<KnowledgeReviewSourceRecord> Items { get; set; } = [];
    public string NextCursor { get; set; } = string.Empty;
    public bool HasMore { get; set; }
}

public sealed class ApproveKnowledgeCandidateRequest
{
    public string AnswerFinal { get; set; } = string.Empty;
    public List<string> Synonyms { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string UsageNotes { get; set; } = string.Empty;
    public string ReviewComment { get; set; } = string.Empty;
    public string Version { get; set; } = "v1";
    public string Reviewer { get; set; } = string.Empty;
    public string ReviewSessionId { get; set; } = string.Empty;
}

public sealed class RejectKnowledgeCandidateRequest
{
    public string Reason { get; set; } = string.Empty;
    public string ReviewComment { get; set; } = string.Empty;
    public string Reviewer { get; set; } = string.Empty;
    public string ReviewSessionId { get; set; } = string.Empty;
}

public sealed class RemovePublishedKnowledgeCardRequest
{
    public string Reason { get; set; } = string.Empty;
    public string ReviewComment { get; set; } = string.Empty;
    public string Reviewer { get; set; } = string.Empty;
    public string ReviewSessionId { get; set; } = string.Empty;
}

public sealed class KnowledgeReviewHistoryEventRecord
{
    public Guid Id { get; set; }
    public Guid CandidateId { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string Reviewer { get; set; } = string.Empty;
    public string ReviewSessionId { get; set; } = string.Empty;
    public string ReviewComment { get; set; } = string.Empty;
    public string ReviewReason { get; set; } = string.Empty;
    public string BeforeSnapshotJson { get; set; } = string.Empty;
    public string AfterSnapshotJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class KnowledgeReviewHistoryResponse
{
    public Guid CandidateId { get; set; }
    public List<KnowledgeReviewHistoryEventRecord> Items { get; set; } = [];
    public string NextCursor { get; set; } = string.Empty;
    public bool HasMore { get; set; }
}
