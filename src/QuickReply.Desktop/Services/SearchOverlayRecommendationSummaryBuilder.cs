using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal static class SearchOverlayRecommendationSummaryBuilder
{
    public static string BuildRecommendationHintText(
        IReadOnlyList<PhraseRecommendationSuggestion> suggestions,
        IReadOnlyList<KnowledgeCardSuggestion> knowledgeCards)
    {
        var historicalCount = suggestions.Count(item => item.IsHistoricalSource);
        var catalogCount = suggestions.Count(item => item.IsCatalogFallbackSource);
        var qaCount = knowledgeCards.Count(item => !item.IsResourceCard);
        var resourceCount = knowledgeCards.Count(item => item.IsResourceCard);

        if (historicalCount > 0 || catalogCount > 0 || qaCount > 0 || resourceCount > 0)
        {
            var parts = new List<string>();
            if (historicalCount > 0)
            {
                parts.Add($"历史高频 {historicalCount} 条");
            }

            if (catalogCount > 0)
            {
                parts.Add($"短语库匹配 {catalogCount} 条");
            }

            if (qaCount > 0)
            {
                parts.Add($"问答卡 {qaCount} 条");
            }

            if (resourceCount > 0)
            {
                parts.Add($"资料卡 {resourceCount} 条");
            }

            return $"已命中 {string.Join("｜", parts)}。";
        }

        return string.Empty;
    }

    public static string BuildGroundingSummaryText(
        IReadOnlyList<PhraseRecommendationSuggestion> suggestions,
        IReadOnlyList<KnowledgeCardSuggestion> knowledgeCards)
    {
        var historicalCount = suggestions.Count(item => item.IsHistoricalSource);
        var catalogCount = suggestions.Count(item => item.IsCatalogFallbackSource);
        if (historicalCount == 0 && catalogCount == 0 && knowledgeCards.Count == 0)
        {
            return "当前还没有命中任何高频短语或已发布知识。";
        }

        var parts = new List<string>();
        if (historicalCount > 0)
        {
            parts.Add($"历史高频 {historicalCount} 条");
        }

        if (catalogCount > 0)
        {
            parts.Add($"短语库匹配 {catalogCount} 条");
        }

        if (knowledgeCards.Count > 0)
        {
            parts.Add($"知识 {knowledgeCards.Count} 条{BuildKnowledgeTypeSummary(knowledgeCards)}");
        }

        return $"参考依据：{string.Join("｜", parts.Where(item => !string.IsNullOrWhiteSpace(item)))}";
    }

    public static string BuildAiDraftStatusMessage(AiDraftGenerationStatus status)
    {
        if (!status.ShouldSurfaceStatus && !status.HasDrafts)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(status.Message))
        {
            parts.Add(status.Message.Trim());
        }

        if (status.HasDrafts)
        {
            parts.Add($"已生成 {status.GeneratedCount} 条 AI 草稿");
        }

        if (!string.IsNullOrWhiteSpace(status.ProviderType))
        {
            var providerLabel = BuildProviderLabel(status.ProviderType, status.ModelName);
            parts.Add($"provider {providerLabel}");
        }

        parts.Add($"参考短语 {status.GroundingPhraseCount} 条");
        parts.Add($"知识 {status.GroundingKnowledgeCount} 条");
        if (!string.IsNullOrWhiteSpace(status.FallbackReason))
        {
            parts.Add($"fallback {status.FallbackReason}");
        }

        return string.Join("｜", parts.Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private static string BuildKnowledgeTypeSummary(IReadOnlyList<KnowledgeCardSuggestion> knowledgeCards)
    {
        if (knowledgeCards.Count == 0)
        {
            return string.Empty;
        }

        var qaCount = knowledgeCards.Count(item =>
            string.Equals(item.Card.CandidateKind, "qa", StringComparison.OrdinalIgnoreCase));
        var resourceCount = knowledgeCards.Count(item =>
            string.Equals(item.Card.CandidateKind, "resource", StringComparison.OrdinalIgnoreCase));

        if (qaCount > 0 && resourceCount > 0)
        {
            return $"（问答卡 {qaCount} / 资料卡 {resourceCount}）";
        }

        if (qaCount > 0)
        {
            return $"（问答卡 {qaCount}）";
        }

        if (resourceCount > 0)
        {
            return $"（资料卡 {resourceCount}）";
        }

        return string.Empty;
    }

    private static string BuildProviderLabel(string providerType, string modelName)
    {
        var normalized = providerType?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "codex_cli" => string.IsNullOrWhiteSpace(modelName) ? "ChatGPT/Codex" : $"ChatGPT/Codex/{modelName}",
            "ollama" => string.IsNullOrWhiteSpace(modelName) ? "Ollama" : $"Ollama/{modelName}",
            "openai_api" => string.IsNullOrWhiteSpace(modelName) ? "OpenAI API" : $"OpenAI API/{modelName}",
            "grounding_fallback" => "保守降级/rule_based",
            _ => string.IsNullOrWhiteSpace(modelName) ? (providerType ?? string.Empty) : $"{providerType}/{modelName}"
        };
    }
}
