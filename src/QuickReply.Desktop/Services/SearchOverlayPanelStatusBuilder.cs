using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal static class SearchOverlayPanelStatusBuilder
{
    public static string BuildRecommendationStatusText(
        ConversationRecommendationSnapshot snapshot,
        IReadOnlyList<PhraseRecommendationSuggestion> suggestions,
        IReadOnlyList<KnowledgeCardSuggestion> knowledgeCards,
        AiDraftGenerationStatus aiDraftStatus,
        bool isRefreshing)
    {
        if (isRefreshing)
        {
            return "正在识别当前聊天上下文，并整理推荐短语与 AI 草稿...";
        }

        var recognizedCustomerText = snapshot.RecognizedCustomerText?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(recognizedCustomerText))
        {
            var parts = new List<string>
            {
                $"已识别客户消息（{BuildRecognitionSourceLabel(snapshot)}）"
            };

            if (snapshot.RecognitionConfidence > 0d)
            {
                parts.Add($"置信度 {snapshot.RecognitionConfidence:0.00}");
            }

            if (suggestions.Count > 0 || knowledgeCards.Count > 0)
            {
                parts.Add(BuildMatchSummary(suggestions, knowledgeCards));
            }
            else if (aiDraftStatus.State == AiDraftGenerationState.ContextSkipped)
            {
                parts.Add("当前上下文仍偏弱，AI 草稿已保守跳过");
            }
            else
            {
                parts.Add("暂未命中高频短语或已发布知识");
            }

            return string.Join("｜", parts);
        }

        var capturedText = snapshot.CapturedText?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(capturedText))
        {
            return "已抓到窗口文本，但还没有识别出可靠客户消息；你可以继续搜索、手动输入，或点击“修正识别”。";
        }

        var captureStatus = snapshot.CaptureStatus?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(captureStatus))
        {
            return $"当前未抓到稳定聊天文本（{BuildCaptureStatusLabel(captureStatus)}），推荐与 AI 会保持保守。";
        }

        return "打开浮层后会尝试识别当前聊天上下文，并在上方整理推荐短语与 AI 草稿。";
    }

    public static string BuildResultsSectionTitle(SearchOverlayMode mode)
    {
        return mode switch
        {
            SearchOverlayMode.File => "文件结果",
            SearchOverlayMode.Mixed => "搜索结果",
            _ => "短语结果"
        };
    }

    public static string BuildResultsSectionSummary(
        SearchOverlayMode mode,
        string searchText,
        int resultCount,
        bool hasRecommendationPanelContent)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return mode switch
            {
                SearchOverlayMode.File => "输入文件名后可直接发送文件；上方推荐区会继续根据当前聊天整理建议。",
                SearchOverlayMode.Mixed => "可同时搜索短语和文件；上方推荐区会根据当前聊天上下文补充建议。",
                _ => "输入关键词后可发送短语；上方推荐区会根据当前聊天上下文补充建议。"
            };
        }

        if (resultCount > 0)
        {
            return mode switch
            {
                SearchOverlayMode.File => $"已找到 {resultCount} 条文件结果。",
                SearchOverlayMode.Mixed => $"已找到 {resultCount} 条短语或文件结果。",
                _ => $"已找到 {resultCount} 条短语结果。"
            };
        }

        if (hasRecommendationPanelContent)
        {
            return "当前搜索没有直接命中，但上方仍有推荐短语、知识卡或 AI 草稿可供参考。";
        }

        return mode switch
        {
            SearchOverlayMode.File => "当前没有匹配的文件结果。",
            SearchOverlayMode.Mixed => "当前没有匹配的短语或文件结果。",
            _ => "当前没有匹配的短语结果。"
        };
    }

    public static string BuildEmptyStateTitle(SearchOverlayMode mode, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return mode switch
            {
                SearchOverlayMode.File => "输入文件名开始搜索",
                SearchOverlayMode.Mixed => "输入关键词开始混合搜索",
                _ => "输入关键词开始搜索短语"
            };
        }

        return mode switch
        {
            SearchOverlayMode.File => "没有匹配的文件结果",
            SearchOverlayMode.Mixed => "没有匹配的短语或文件结果",
            _ => "没有匹配的短语结果"
        };
    }

    public static string BuildEmptyStateHintText(
        SearchOverlayMode mode,
        string searchText,
        bool hasRecommendationPanelContent)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return mode switch
            {
                SearchOverlayMode.File => "你可以继续输入文件名，也可以切回短语或混合搜索；上方推荐区会根据当前聊天自动整理建议。",
                SearchOverlayMode.Mixed => "你可以输入问题关键词，或切到纯短语/文件模式；上方推荐区会继续根据当前聊天补充建议。",
                _ => "你可以输入问题关键词，或切到文件/混合搜索；上方推荐区会继续根据当前聊天补充建议。"
            };
        }

        if (hasRecommendationPanelContent)
        {
            return "虽然当前搜索没有直接命中，但你仍然可以使用上方的推荐短语、知识卡线索或 AI 草稿。";
        }

        return mode switch
        {
            SearchOverlayMode.File => "试试换一个文件名关键词，或切到短语/混合搜索继续处理当前聊天。",
            SearchOverlayMode.Mixed => "试试换一个更短或更明确的关键词，或者切到纯短语/文件模式继续搜索。",
            _ => "试试换一个更短或更明确的关键词，或切到混合/文件搜索继续查找。"
        };
    }

    private static string BuildRecognitionSourceLabel(ConversationRecommendationSnapshot snapshot)
    {
        var method = snapshot.RecognitionMethod?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(method))
        {
            method = snapshot.CaptureStatus?.Contains("ocr", StringComparison.OrdinalIgnoreCase) == true
                ? "ocr"
                : "capture";
        }

        return method.ToLowerInvariant() switch
        {
            "ocr" => "OCR",
            "uia" => "界面文本",
            "merged" => "界面文本 + OCR",
            var value when value.StartsWith("automation_ocr_hybrid", StringComparison.Ordinal) => "自动 OCR 混合识别",
            var value when value.StartsWith("automation_uia", StringComparison.Ordinal) => "自动界面文本识别",
            var value when value.StartsWith("automation_hybrid", StringComparison.Ordinal) => "自动混合识别",
            var value when value.StartsWith("capture", StringComparison.Ordinal) => "窗口文本抓取",
            _ => method
        };
    }

    private static string BuildCaptureStatusLabel(string captureStatus)
    {
        var tokens = captureStatus
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToLowerInvariant())
            .Distinct()
            .ToArray();

        if (tokens.Length == 0)
        {
            return "未提供状态";
        }

        var labels = tokens.Select(token => token switch
        {
            "uia" => "界面文本",
            "ocr" => "OCR",
            "missing_rect" => "未取到聊天区域",
            "empty" => "未抓到文本",
            _ => token
        });

        return string.Join(" + ", labels);
    }

    private static string BuildMatchSummary(
        IReadOnlyList<PhraseRecommendationSuggestion> suggestions,
        IReadOnlyList<KnowledgeCardSuggestion> knowledgeCards)
    {
        var parts = new List<string>();
        var historicalCount = suggestions.Count(item => item.IsHistoricalSource);
        var catalogCount = suggestions.Count(item => item.IsCatalogFallbackSource);
        var qaCount = knowledgeCards.Count(item => !item.IsResourceCard);
        var resourceCount = knowledgeCards.Count(item => item.IsResourceCard);

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

        return parts.Count == 0 ? "当前还没有命中推荐短语或知识卡" : string.Join("｜", parts);
    }
}
