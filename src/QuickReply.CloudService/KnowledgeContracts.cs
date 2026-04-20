namespace QuickReply.CloudService;

public sealed class KnowledgeCandidateRecord
{
    public Guid Id { get; init; }
    public string Status { get; init; } = "draft";
    public string CandidateKind { get; init; } = KnowledgeCandidateKinds.Qa;
    public string Platform { get; init; } = string.Empty;
    public string ProductKey { get; init; } = string.Empty;
    public string QuestionNormalized { get; init; } = string.Empty;
    public string AnswerDraft { get; init; } = string.Empty;
    public string AnswerFinal { get; init; } = string.Empty;
    public List<string> Tags { get; init; } = [];
    public double Confidence { get; init; }
    public string RiskLevel { get; init; } = "medium";
    public int SourceCount { get; init; }
    public string ClusterKey { get; init; } = string.Empty;
    public string EvidenceSummary { get; init; } = string.Empty;
    public string ReviewComment { get; init; } = string.Empty;
    public string ReviewReason { get; init; } = string.Empty;
    public string ReviewedBy { get; init; } = string.Empty;
    public string ReviewSessionId { get; init; } = string.Empty;
    public DateTimeOffset? ReviewedAt { get; init; }
    public string LeaseToken { get; init; } = string.Empty;
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class KnowledgeCandidateSourceRecord
{
    public Guid Id { get; init; }
    public Guid CandidateId { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string Excerpt { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class PublishedKnowledgeCardRecord
{
    public Guid Id { get; init; }
    public string CandidateKind { get; init; } = KnowledgeCandidateKinds.Qa;
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

public sealed class PublishedKnowledgeSyncResponse
{
    public string KnowledgeVersion { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; }
    public long LatestRevision { get; init; }
    public string NextCursor { get; init; } = string.Empty;
    public bool HasMore { get; init; }
    public IReadOnlyList<PublishedKnowledgeCardRecord> Cards { get; init; } = [];
    public IReadOnlyList<Guid> RemovedCardIds { get; init; } = [];
}

public sealed class KnowledgeReviewQueueResponse
{
    public IReadOnlyList<KnowledgeReviewQueueItem> Items { get; init; } = [];
    public string NextCursor { get; init; } = string.Empty;
    public bool HasMore { get; init; }
}

public sealed class KnowledgeReviewSourcesResponse
{
    public Guid CandidateId { get; init; }
    public IReadOnlyList<KnowledgeCandidateSourceRecord> Items { get; init; } = [];
    public string NextCursor { get; init; } = string.Empty;
    public bool HasMore { get; init; }
}

public sealed class KnowledgeReviewQueueItem
{
    public Guid Id { get; init; }
    public string Status { get; init; } = string.Empty;
    public string CandidateKind { get; init; } = KnowledgeCandidateKinds.Qa;
    public string Platform { get; init; } = string.Empty;
    public string ProductKey { get; init; } = string.Empty;
    public string QuestionNormalized { get; init; } = string.Empty;
    public string AnswerDraft { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public double Confidence { get; init; }
    public string RiskLevel { get; init; } = string.Empty;
    public int SourceCount { get; init; }
    public string ClusterKey { get; init; } = string.Empty;
    public string EvidenceSummary { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class ApproveKnowledgeCandidateRequest
{
    public string AnswerFinal { get; init; } = string.Empty;
    public IReadOnlyList<string> Synonyms { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string UsageNotes { get; init; } = string.Empty;
    public string ReviewComment { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
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

public sealed class ImportKnowledgeCandidatesRequest
{
    public string InputPath { get; init; } = string.Empty;
}

public sealed class KnowledgeJobRunRecord
{
    public Guid Id { get; init; }
    public int SampleCount { get; init; }
    public int CandidateCount { get; init; }
    public int PublishCount { get; init; }
    public int RejectCount { get; init; }
    public double AvgConfidence { get; init; }
    public int FailedClusterCount { get; init; }
    public int FailedSynthesizeCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class KnowledgeImportRunResponse
{
    public Guid JobRunId { get; init; }
    public string InputPath { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public int CandidateCount { get; init; }
    public int PublishCount { get; init; }
    public int RejectCount { get; init; }
    public double AvgConfidence { get; init; }
    public int FailedClusterCount { get; init; }
    public int FailedSynthesizeCount { get; init; }
    public int ImportedCorrectionSourceCount { get; init; }
    public int ImportedRecommendationSourceCount { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
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
    public IReadOnlyList<KnowledgeReviewHistoryEventRecord> Items { get; init; } = [];
    public string NextCursor { get; init; } = string.Empty;
    public bool HasMore { get; init; }
}

public sealed class CodexCliAuthStatusResponse
{
    public bool CliAvailable { get; init; }
    public bool LoggedIn { get; init; }
    public string LoginMode { get; init; } = string.Empty;
    public string LoginModeDisplayName { get; init; } = "未登录";
    public string Message { get; init; } = string.Empty;
}

public sealed class CodexCliLoginRequest
{
    public bool UseDeviceAuth { get; init; } = true;
}

public sealed class CodexCliAuthActionResponse
{
    public bool Success { get; init; }
    public bool CliAvailable { get; init; }
    public bool LoggedIn { get; init; }
    public string LoginMode { get; init; } = string.Empty;
    public string LoginModeDisplayName { get; init; } = "未登录";
    public string Message { get; init; } = string.Empty;
    public string CommandOutput { get; init; } = string.Empty;
}

public sealed class CloudServiceRuntimeInfoResponse
{
    public bool Ok { get; init; }
    public string Service { get; init; } = "QuickReply.CloudService";
    public string Version { get; init; } = string.Empty;
    public bool DatabaseReady { get; init; }
    public string DatabasePath { get; init; } = string.Empty;
    public bool AiSynthesisEnabled { get; init; }
    public string AiProvider { get; init; } = string.Empty;
    public bool AiReady { get; init; }
    public bool ManagementApiTokenConfigured { get; init; }
    public bool LoopbackBypassEnabled { get; init; }
    public bool CodexCliAvailable { get; init; }
    public bool CodexLoggedIn { get; init; }
    public string CodexLoginMode { get; init; } = string.Empty;
    public string[] Warnings { get; init; } = [];
}
