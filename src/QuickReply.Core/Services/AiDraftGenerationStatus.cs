namespace QuickReply.Desktop.Services;

public enum AiDraftGenerationState
{
    Disabled,
    ContextSkipped,
    ProviderFailed,
    Generated
}

public sealed class AiDraftGenerationStatus
{
    public static AiDraftGenerationStatus Disabled(string message)
    {
        return new AiDraftGenerationStatus
        {
            State = AiDraftGenerationState.Disabled,
            Message = message
        };
    }

    public static AiDraftGenerationStatus ContextSkipped(string message, int groundingPhraseCount = 0, int groundingKnowledgeCount = 0)
    {
        return new AiDraftGenerationStatus
        {
            State = AiDraftGenerationState.ContextSkipped,
            Message = message,
            GroundingPhraseCount = groundingPhraseCount,
            GroundingKnowledgeCount = groundingKnowledgeCount
        };
    }

    public static AiDraftGenerationStatus ProviderFailed(
        string message,
        string providerType,
        string modelName,
        long latencyMs,
        int groundingPhraseCount,
        int groundingKnowledgeCount,
        string fallbackReason,
        int characterCount = 0)
    {
        return new AiDraftGenerationStatus
        {
            State = AiDraftGenerationState.ProviderFailed,
            Message = message,
            ProviderType = providerType,
            ModelName = modelName,
            LatencyMs = latencyMs,
            GroundingPhraseCount = groundingPhraseCount,
            GroundingKnowledgeCount = groundingKnowledgeCount,
            FallbackReason = fallbackReason,
            CharacterCount = characterCount
        };
    }

    public static AiDraftGenerationStatus Generated(
        string message,
        string providerType,
        string modelName,
        long latencyMs,
        int groundingPhraseCount,
        int groundingKnowledgeCount,
        string fallbackReason,
        int characterCount,
        int generatedCount)
    {
        return new AiDraftGenerationStatus
        {
            State = AiDraftGenerationState.Generated,
            Message = message,
            ProviderType = providerType,
            ModelName = modelName,
            LatencyMs = latencyMs,
            GroundingPhraseCount = groundingPhraseCount,
            GroundingKnowledgeCount = groundingKnowledgeCount,
            FallbackReason = fallbackReason,
            CharacterCount = characterCount,
            GeneratedCount = generatedCount
        };
    }

    public AiDraftGenerationState State { get; init; } = AiDraftGenerationState.Disabled;

    public string Message { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string ModelName { get; init; } = string.Empty;

    public long LatencyMs { get; init; }

    public int GroundingPhraseCount { get; init; }

    public int GroundingKnowledgeCount { get; init; }

    public string FallbackReason { get; init; } = string.Empty;

    public int CharacterCount { get; init; }

    public int GeneratedCount { get; init; }

    public bool HasDrafts => State == AiDraftGenerationState.Generated && GeneratedCount > 0;

    public bool ShouldSurfaceStatus => State is AiDraftGenerationState.ContextSkipped or AiDraftGenerationState.ProviderFailed;
}

public interface IAiDraftGenerationStatusSource
{
    AiDraftGenerationStatus LastStatus { get; }
}
