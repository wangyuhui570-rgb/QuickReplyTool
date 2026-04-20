namespace QuickReply.Desktop.Models;

public sealed class KnowledgeSyncPayload
{
    public string ClientId { get; init; } = string.Empty;

    public string AppVersion { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public DateTimeOffset? Since { get; init; }

    public bool IsFullSync { get; init; }

    public IReadOnlyList<KnowledgeCategorySyncItem> Categories { get; init; } = [];

    public IReadOnlyList<KnowledgePhraseSyncItem> Phrases { get; init; } = [];

    public IReadOnlyList<KnowledgeQuickTagSyncItem> QuickTags { get; init; } = [];

    public IReadOnlyList<KnowledgeFileSyncItem> FileItems { get; init; } = [];
}

public sealed class PublishedKnowledgeSyncResponse
{
    public string KnowledgeVersion { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public long LatestRevision { get; init; }

    public string NextCursor { get; init; } = string.Empty;

    public bool HasMore { get; init; }

    public IReadOnlyList<PublishedKnowledgeCardSyncItem> Cards { get; init; } = [];

    public IReadOnlyList<Guid> RemovedCardIds { get; init; } = [];
}

public sealed class PublishedKnowledgeCardSyncItem
{
    public Guid Id { get; init; }

    public string CandidateKind { get; init; } = "qa";

    public string Platform { get; init; } = string.Empty;

    public string ProductKey { get; init; } = string.Empty;

    public string Question { get; init; } = string.Empty;

    public string Answer { get; init; } = string.Empty;

    public IReadOnlyList<string> Synonyms { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string UsageNotes { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public long Revision { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class ConversationSyncPayload
{
    public string ClientId { get; init; } = string.Empty;

    public string AppVersion { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public DateTimeOffset? Since { get; init; }

    public bool IsFullSync { get; init; }

    public IReadOnlyList<ConversationLogSyncItem> ConversationLogs { get; init; } = [];

    public IReadOnlyList<RecommendationEventSyncItem> RecommendationEvents { get; init; } = [];

    public IReadOnlyList<RecognitionCorrectionSyncItem> RecognitionCorrections { get; init; } = [];
}

public sealed class KnowledgeCategorySyncItem
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public int SortOrder { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class KnowledgePhraseSyncItem
{
    public Guid Id { get; init; }

    public Guid CategoryId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Tags { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public int SortOrder { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class KnowledgeQuickTagSyncItem
{
    public Guid Id { get; init; }

    public Guid PhraseId { get; init; }

    public int SortOrder { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class KnowledgeFileSyncItem
{
    public Guid Id { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string RelativePath { get; init; } = string.Empty;

    public string FullPath { get; init; } = string.Empty;

    public string Tags { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class ConversationLogSyncItem
{
    public Guid Id { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public string TriggerSource { get; init; } = string.Empty;

    public string ActionMode { get; init; } = string.Empty;

    public string PayloadType { get; init; } = string.Empty;

    public string SearchKeyword { get; init; } = string.Empty;

    public bool ResultSuccess { get; init; }

    public string ResultMessage { get; init; } = string.Empty;

    public string PlatformHint { get; init; } = string.Empty;

    public string TargetWindowTitle { get; init; } = string.Empty;

    public string TargetProcessName { get; init; } = string.Empty;

    public string CaptureStatus { get; init; } = string.Empty;

    public string CapturedText { get; init; } = string.Empty;

    public string PhraseId { get; init; } = string.Empty;

    public string PhraseTitle { get; init; } = string.Empty;

    public string PhraseTags { get; init; } = string.Empty;

    public string PhraseCategoryName { get; init; } = string.Empty;

    public string FileItemId { get; init; } = string.Empty;

    public string FileDisplayName { get; init; } = string.Empty;

    public string PayloadText { get; init; } = string.Empty;

    public string RecognitionStatus { get; init; } = string.Empty;

    public double RecognitionConfidence { get; init; }

    public string RecognizedCustomerText { get; init; } = string.Empty;

    public string RecognizedContextText { get; init; } = string.Empty;

    public string RecognizedProductId { get; init; } = string.Empty;

    public string RecognizedProductName { get; init; } = string.Empty;

    public string RecognizedMessagesJson { get; init; } = string.Empty;

    public string ReplyGroupId { get; init; } = string.Empty;

    public int ReplyGroupIndex { get; init; }

    public string DataQualityGrade { get; init; } = string.Empty;

    public string AppVersion { get; init; } = string.Empty;

    public string RecommendationRuleVersion { get; init; } = string.Empty;
}

public sealed class RecommendationEventSyncItem
{
    public Guid Id { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public string RecommendationSessionId { get; init; } = string.Empty;

    public string EventType { get; init; } = string.Empty;

    public string SearchMode { get; init; } = string.Empty;

    public string SearchKeyword { get; init; } = string.Empty;

    public string ActionMode { get; init; } = string.Empty;

    public bool ResultSuccess { get; init; }

    public string PhraseId { get; init; } = string.Empty;

    public string PhraseTitle { get; init; } = string.Empty;

    public string PhraseCategoryName { get; init; } = string.Empty;

    public int DisplayRank { get; init; }

    public string MatchedKeyword { get; init; } = string.Empty;

    public int MatchCount { get; init; }

    public int SampleCount { get; init; }

    public double Confidence { get; init; }

    public string RecognizedCustomerText { get; init; } = string.Empty;

    public string PlatformHint { get; init; } = string.Empty;

    public string WindowTitle { get; init; } = string.Empty;

    public string ActualPayloadText { get; init; } = string.Empty;

    public int GroundingPhraseCount { get; init; }

    public int GroundingKnowledgeCount { get; init; }

    public string GroundingKnowledgeIdsJson { get; init; } = string.Empty;

    public bool KnowledgeHit { get; init; }

    public string AppVersion { get; init; } = string.Empty;

    public string RecommendationRuleVersion { get; init; } = string.Empty;
}

public sealed class RecognitionCorrectionSyncItem
{
    public Guid Id { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public string RecommendationSessionId { get; init; } = string.Empty;

    public string PlatformHint { get; init; } = string.Empty;

    public string WindowTitle { get; init; } = string.Empty;

    public string SourceCustomerText { get; init; } = string.Empty;

    public string CorrectedCustomerText { get; init; } = string.Empty;

    public string SourceContextText { get; init; } = string.Empty;

    public string SourceProductId { get; init; } = string.Empty;

    public string SourceProductName { get; init; } = string.Empty;

    public string SourceMessagesJson { get; init; } = string.Empty;

    public string CorrectedContextText { get; init; } = string.Empty;

    public string CorrectedProductId { get; init; } = string.Empty;

    public string CorrectedProductName { get; init; } = string.Empty;

    public string CorrectedMessagesJson { get; init; } = string.Empty;

    public string RecognitionMethod { get; init; } = string.Empty;

    public string AppVersion { get; init; } = string.Empty;
}

public sealed class CloudUpdateManifest
{
    public string LatestAppVersion { get; init; } = string.Empty;

    public string LatestAppDownloadUrl { get; init; } = string.Empty;

    public string LatestAppPackageKind { get; init; } = string.Empty;

    public string LatestAppSha256 { get; init; } = string.Empty;

    public long? LatestAppSizeBytes { get; init; }

    public string LatestAppReleaseChannel { get; init; } = string.Empty;

    public bool LatestAppMandatory { get; init; }

    public string LatestAppReleaseNotes { get; init; } = string.Empty;

    public string KnowledgeVersion { get; init; } = string.Empty;

    public string KnowledgePackageUrl { get; init; } = string.Empty;

    public string KnowledgeSummary { get; init; } = string.Empty;
}
