namespace QuickReply.Desktop.Models;

public sealed class GeneratedReplyCandidate
{
    public int DisplayRank { get; init; }

    public string Text { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public string ProviderType { get; init; } = string.Empty;

    public string ModelName { get; init; } = string.Empty;

    public int GroundingPhraseCount { get; init; }

    public int GroundingKnowledgeCount { get; init; }

    public string GroundingKnowledgeIdsJson { get; init; } = string.Empty;

    public long LatencyMs { get; init; }

    public int CharacterCount { get; init; }

    public string FallbackReason { get; init; } = string.Empty;

    public Guid? BackingPhraseId { get; init; }

    public string BackingPhraseTitle { get; init; } = string.Empty;

    public string ProviderDisplayText => ProviderType.ToLowerInvariant() switch
    {
        "codex_cli" => string.IsNullOrWhiteSpace(ModelName) ? "ChatGPT/Codex" : $"ChatGPT/Codex · {ModelName}",
        "ollama" => string.IsNullOrWhiteSpace(ModelName) ? "Ollama" : $"Ollama · {ModelName}",
        "openai_api" => string.IsNullOrWhiteSpace(ModelName) ? "OpenAI API" : $"OpenAI API · {ModelName}",
        "grounding_fallback" => "保守降级草稿",
        _ => string.IsNullOrWhiteSpace(ModelName) ? ProviderType : $"{ProviderType} · {ModelName}"
    };

    public string SourceBadgeText => string.Equals(ProviderType, "grounding_fallback", StringComparison.OrdinalIgnoreCase)
        ? "保守草稿"
        : "AI 草稿";

    public string GroundingSummaryText
    {
        get
        {
            var parts = new List<string>();
            if (GroundingPhraseCount > 0)
            {
                parts.Add($"短语 {GroundingPhraseCount} 条");
            }

            if (GroundingKnowledgeCount > 0)
            {
                parts.Add($"知识 {GroundingKnowledgeCount} 条");
            }

            if (!string.IsNullOrWhiteSpace(FallbackReason))
            {
                parts.Add($"fallback {FallbackReason}");
            }

            return string.Join("｜", parts);
        }
    }
}
