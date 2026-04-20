using System.Text;
using System.Text.Json;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal static class AiDraftPromptComposer
{
    public static string BuildSystemPrompt()
    {
        return """
            你是客服快捷短语草稿助手。
            你的任务是根据客户消息、上下文、参考短语和已发布知识卡，生成 1-3 条可以直接发送的中文回复草稿。
            规则：
            1. 优先参考和改写提供的现有短语与知识卡，不要脱离知识库自由发挥。
            2. 不要虚构售后政策、物流承诺、价格补偿或库存信息。
            3. 回复要适合客服直接发送，语气自然、简洁、礼貌。
            4. 只输出 JSON，不要输出 Markdown，不要解释。
            5. JSON 格式固定为：
               {"drafts":[{"text":"回复正文","reason":"参考了哪些上下文或短语"}]}
            """;
    }

    public static string BuildUserPrompt(AiDraftProviderRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"平台：{Normalize(request.Snapshot.PlatformHint, "unknown")}");
        builder.AppendLine($"客户消息：{Normalize(request.Snapshot.RecognizedCustomerText, "（空）")}");
        builder.AppendLine($"上下文：{Normalize(request.Snapshot.RecognizedContextText, "（无）")}");
        builder.AppendLine($"商品信息：{BuildProductSummary(request.Snapshot)}");
        builder.AppendLine($"识别置信度：{request.Snapshot.RecognitionConfidence:0.00}");
        builder.AppendLine($"建议生成条数：{Math.Max(1, request.DraftCount)}");
        builder.AppendLine("参考短语：");

        if (request.GroundingSuggestions.Count == 0)
        {
            builder.AppendLine("- （无）");
        }
        else
        {
            foreach (var suggestion in request.GroundingSuggestions.Take(5))
            {
                builder.Append("- 标题：");
                builder.AppendLine(Normalize(suggestion.Phrase.DisplayTitle, "未命名短语"));
                builder.Append("  内容：");
                builder.AppendLine(Normalize(suggestion.Phrase.Content, "（空）"));
                builder.Append("  理由：");
                builder.AppendLine(Normalize(suggestion.ReasonText, "历史高频"));
            }
        }

        builder.AppendLine("已发布知识卡：");
        if (request.GroundingKnowledgeCards.Count == 0)
        {
            builder.AppendLine("- （无）");
        }
        else
        {
            foreach (var suggestion in request.GroundingKnowledgeCards.Take(5))
            {
                builder.Append("- 问题：");
                builder.AppendLine(Normalize(suggestion.Card.Question, "（空）"));
                builder.Append("  答案：");
                builder.AppendLine(Normalize(suggestion.Card.Answer, "（空）"));
                builder.Append("  理由：");
                builder.AppendLine(Normalize(suggestion.ReasonText, "已发布知识卡"));
            }
        }

        builder.AppendLine("请输出 JSON。");
        return builder.ToString().Trim();
    }

    public static IReadOnlyList<GeneratedReplyCandidate> ParseDrafts(
        string responseContent,
        string providerType,
        string modelName,
        int draftCount,
        IReadOnlyList<PhraseRecommendationSuggestion> groundingSuggestions,
        IReadOnlyList<KnowledgeCardSuggestion> groundingKnowledgeCards,
        long latencyMs,
        string fallbackReason)
    {
        if (TryParseJsonDrafts(responseContent, out var drafts))
        {
            return BuildCandidates(drafts, providerType, modelName, draftCount, groundingSuggestions, groundingKnowledgeCards, latencyMs, fallbackReason);
        }

        var fallbackDrafts = ParseLineBasedDrafts(responseContent);
        return BuildCandidates(fallbackDrafts, providerType, modelName, draftCount, groundingSuggestions, groundingKnowledgeCards, latencyMs, fallbackReason);
    }

    private static IReadOnlyList<GeneratedReplyCandidate> BuildCandidates(
        IReadOnlyList<(string Text, string Reason)> parsedDrafts,
        string providerType,
        string modelName,
        int draftCount,
        IReadOnlyList<PhraseRecommendationSuggestion> groundingSuggestions,
        IReadOnlyList<KnowledgeCardSuggestion> groundingKnowledgeCards,
        long latencyMs,
        string fallbackReason)
    {
        var groundingTitles = groundingSuggestions
            .Select(item => item.Phrase.DisplayTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        var knowledgeTitles = groundingKnowledgeCards
            .Select(item => item.Card.Question)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        var defaultReasonParts = new List<string>();
        if (groundingTitles.Length > 0)
        {
            defaultReasonParts.Add($"短语：{string.Join(" / ", groundingTitles)}");
        }

        if (knowledgeTitles.Length > 0)
        {
            defaultReasonParts.Add($"知识：{string.Join(" / ", knowledgeTitles)}");
        }

        if (defaultReasonParts.Count == 0)
        {
            defaultReasonParts.Add("当前识别到的客户消息与上下文");
        }

        var defaultReason = $"参考了{string.Join("；", defaultReasonParts)}";
        var groundingKnowledgeIdsJson = JsonSerializer.Serialize(
            groundingKnowledgeCards
                .Select(item => item.Card.Id)
                .Distinct()
                .ToArray());

        return parsedDrafts
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select((item, index) => new GeneratedReplyCandidate
            {
                DisplayRank = index + 1,
                Text = NormalizeDraftText(item.Text),
                Source = string.Equals(providerType, "ollama", StringComparison.OrdinalIgnoreCase)
                    ? "ai_draft_ollama"
                    : "ai_draft_api_fallback",
                Reason = Normalize(item.Reason, defaultReason),
                Confidence = Math.Max(0.35d, 0.88d - (index * 0.08d)),
                ProviderType = providerType,
                ModelName = modelName,
                GroundingPhraseCount = groundingSuggestions.Count,
                GroundingKnowledgeCount = groundingKnowledgeCards.Count,
                GroundingKnowledgeIdsJson = groundingKnowledgeIdsJson,
                LatencyMs = latencyMs,
                CharacterCount = NormalizeDraftText(item.Text).Length,
                FallbackReason = fallbackReason,
                BackingPhraseId = groundingSuggestions.FirstOrDefault()?.Phrase.Id,
                BackingPhraseTitle = groundingSuggestions.FirstOrDefault()?.Phrase.DisplayTitle ?? string.Empty
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .DistinctBy(item => item.Text)
            .Take(Math.Max(1, draftCount))
            .ToArray();
    }

    private static bool TryParseJsonDrafts(string responseContent, out IReadOnlyList<(string Text, string Reason)> drafts)
    {
        drafts = [];
        var normalized = StripCodeFence(responseContent);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(normalized);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                drafts = ReadDraftArray(document.RootElement);
                return drafts.Count > 0;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("drafts", out var draftsElement) &&
                draftsElement.ValueKind == JsonValueKind.Array)
            {
                drafts = ReadDraftArray(draftsElement);
                return drafts.Count > 0;
            }
        }
        catch
        {
        }

        return false;
    }

    private static IReadOnlyList<(string Text, string Reason)> ReadDraftArray(JsonElement arrayElement)
    {
        var drafts = new List<(string Text, string Reason)>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var text = item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString() ?? string.Empty
                : string.Empty;
            var reason = item.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString() ?? string.Empty
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                drafts.Add((text, reason));
            }
        }

        return drafts;
    }

    private static IReadOnlyList<(string Text, string Reason)> ParseLineBasedDrafts(string responseContent)
    {
        var drafts = new List<(string Text, string Reason)>();
        var lines = responseContent
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            var normalized = NormalizeDraftText(line);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                drafts.Add((normalized, string.Empty));
            }
        }

        return drafts;
    }

    private static string StripCodeFence(string responseContent)
    {
        var normalized = (responseContent ?? string.Empty).Trim();
        if (!normalized.StartsWith("```", StringComparison.Ordinal))
        {
            return normalized;
        }

        var firstNewLine = normalized.IndexOf('\n');
        if (firstNewLine < 0)
        {
            return normalized.Trim('`');
        }

        normalized = normalized[(firstNewLine + 1)..];
        var closingFenceIndex = normalized.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFenceIndex >= 0)
        {
            normalized = normalized[..closingFenceIndex];
        }

        return normalized.Trim();
    }

    private static string NormalizeDraftText(string? value)
    {
        var normalized = Normalize(value, string.Empty)
            .TrimStart('-', '•', '*')
            .Trim();

        if (normalized.Length >= 3 &&
            char.IsDigit(normalized[0]) &&
            normalized[1] is '.' or '、' or ')' or '）')
        {
            normalized = normalized[2..].Trim();
        }

        return normalized.Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
    }

    private static string BuildProductSummary(ConversationRecommendationSnapshot snapshot)
    {
        if (!snapshot.Product.HasValue)
        {
            return "（无）";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot.RecognizedProductId))
        {
            parts.Add($"ID {snapshot.RecognizedProductId}");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.RecognizedProductName))
        {
            parts.Add(snapshot.RecognizedProductName);
        }

        return parts.Count == 0 ? "（无）" : string.Join(" / ", parts);
    }

    private static string Normalize(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}
