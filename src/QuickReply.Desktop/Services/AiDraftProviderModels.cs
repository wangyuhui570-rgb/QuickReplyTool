using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal sealed class AiDraftProviderRequest
{
    public required ConversationRecommendationSnapshot Snapshot { get; init; }

    public required IReadOnlyList<PhraseRecommendationSuggestion> GroundingSuggestions { get; init; }

    public required IReadOnlyList<KnowledgeCardSuggestion> GroundingKnowledgeCards { get; init; }

    public required int DraftCount { get; init; }
}

internal sealed class AiDraftProviderResponse
{
    public static AiDraftProviderResponse Failed(
        string providerType,
        string modelName,
        string message,
        string failureReason,
        long latencyMs = 0,
        int characterCount = 0)
    {
        return new AiDraftProviderResponse
        {
            ProviderType = providerType,
            ModelName = modelName,
            Success = false,
            Message = message,
            FailureReason = failureReason,
            LatencyMs = latencyMs,
            CharacterCount = characterCount
        };
    }

    public static AiDraftProviderResponse Succeeded(
        string providerType,
        string modelName,
        string message,
        IReadOnlyList<GeneratedReplyCandidate> drafts,
        long latencyMs,
        int characterCount)
    {
        return new AiDraftProviderResponse
        {
            ProviderType = providerType,
            ModelName = modelName,
            Success = true,
            Message = message,
            Drafts = drafts,
            LatencyMs = latencyMs,
            CharacterCount = characterCount
        };
    }

    public string ProviderType { get; init; } = string.Empty;

    public string ModelName { get; init; } = string.Empty;

    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public string FailureReason { get; init; } = string.Empty;

    public long LatencyMs { get; init; }

    public int CharacterCount { get; init; }

    public IReadOnlyList<GeneratedReplyCandidate> Drafts { get; init; } = [];
}

internal interface IAiDraftProvider
{
    string ProviderType { get; }

    Task<AiDraftProviderResponse> GenerateAsync(
        AppSettings settings,
        AiDraftProviderRequest request,
        CancellationToken cancellationToken);
}
