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
