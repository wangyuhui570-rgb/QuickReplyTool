using System.Text.Json;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal sealed class AiGroundedReplyGenerator : IReplyGenerator, IAiDraftGenerationStatusSource
{
    private readonly AppSettings _settings;
    private readonly IAiDraftProvider _ollamaProvider;
    private readonly IAiDraftProvider _apiFallbackProvider;

    public AiGroundedReplyGenerator(
        AppSettings settings,
        IAiDraftProvider ollamaProvider,
        IAiDraftProvider apiFallbackProvider)
    {
        _settings = settings;
        _ollamaProvider = ollamaProvider;
        _apiFallbackProvider = apiFallbackProvider;
    }

    public AiDraftGenerationStatus LastStatus { get; private set; } = AiDraftGenerationStatus.Disabled(string.Empty);

    public async Task<IReadOnlyList<GeneratedReplyCandidate>> GenerateCandidatesAsync(
        ConversationRecommendationSnapshot snapshot,
        CandidateRetrievalResult retrievalResult,
        RecommendationRequest request)
    {
        if (!_settings.EnableAiDrafts)
        {
            LastStatus = AiDraftGenerationStatus.Disabled(string.Empty);
            return [];
        }

        var groundingSuggestions = retrievalResult.Suggestions
            .Where(item => !string.IsNullOrWhiteSpace(item.Phrase.Content))
            .Take(5)
            .ToArray();
        var groundingKnowledgeCards = retrievalResult.KnowledgeCards
            .Where(item => !string.IsNullOrWhiteSpace(item.Card.Answer))
            .Take(5)
            .ToArray();
        if (!HasReliableContext(snapshot))
        {
            var degradedDrafts = BuildGroundedFallbackDrafts(
                snapshot,
                groundingSuggestions,
                groundingKnowledgeCards,
                Math.Clamp(Math.Min(request.GeneratedReplyCount, _settings.AiDraftCount), 1, 3),
                "low_confidence_context");
            if (degradedDrafts.Count > 0)
            {
                LastStatus = AiDraftGenerationStatus.Generated(
                    "当前上下文较弱，已按命中的短语/知识生成保守草稿。",
                    "grounding_fallback",
                    "rule_based",
                    0,
                    groundingSuggestions.Length,
                    groundingKnowledgeCards.Length,
                    "low_confidence_context",
                    degradedDrafts.Sum(item => item.CharacterCount),
                    degradedDrafts.Count);
                return degradedDrafts;
            }

            LastStatus = AiDraftGenerationStatus.ContextSkipped(
                "未识别到可靠客户消息，暂不生成 AI 草稿。",
                groundingSuggestions.Length,
                groundingKnowledgeCards.Length);
            return [];
        }

        if (groundingSuggestions.Length == 0 && groundingKnowledgeCards.Length == 0)
        {
            LastStatus = AiDraftGenerationStatus.ContextSkipped(
                "当前还没有命中的参考短语或已发布知识，暂不生成 AI 草稿。",
                0,
                0);
            return [];
        }

        var providerRequest = new AiDraftProviderRequest
        {
            Snapshot = snapshot,
            GroundingPhrases = groundingSuggestions
                .Select(item => new AiDraftGroundingPhrase
                {
                    PhraseId = item.Phrase.Id,
                    Title = item.Phrase.DisplayTitle,
                    Content = item.Phrase.Content?.Trim() ?? string.Empty,
                    ReasonText = item.ReasonText,
                    SourceBadgeText = item.SourceBadgeText
                })
                .ToArray(),
            GroundingKnowledgeCards = groundingKnowledgeCards,
            DraftCount = Math.Clamp(Math.Min(request.GeneratedReplyCount, _settings.AiDraftCount), 1, 3)
        };

        var ollamaResult = await _ollamaProvider
            .GenerateAsync(_settings, providerRequest, CancellationToken.None)
            .ConfigureAwait(false);
        if (ollamaResult.Success && ollamaResult.Drafts.Count > 0)
        {
            LastStatus = AiDraftGenerationStatus.Generated(
                ollamaResult.Message,
                ollamaResult.ProviderType,
                ollamaResult.ModelName,
                ollamaResult.LatencyMs,
                groundingSuggestions.Length,
                groundingKnowledgeCards.Length,
                string.Empty,
                ollamaResult.CharacterCount,
                ollamaResult.Drafts.Count);
            return ollamaResult.Drafts;
        }

        if (!_settings.EnableAiApiFallback)
        {
            var degradedDrafts = BuildGroundedFallbackDrafts(
                snapshot,
                groundingSuggestions,
                groundingKnowledgeCards,
                providerRequest.DraftCount,
                ollamaResult.FailureReason);
            if (degradedDrafts.Count > 0)
            {
                LastStatus = AiDraftGenerationStatus.Generated(
                    "本地 AI 当前不可用，已按命中的短语/知识生成保守草稿。",
                    "grounding_fallback",
                    "rule_based",
                    ollamaResult.LatencyMs,
                    groundingSuggestions.Length,
                    groundingKnowledgeCards.Length,
                    ollamaResult.FailureReason,
                    degradedDrafts.Sum(item => item.CharacterCount),
                    degradedDrafts.Count);
                return degradedDrafts;
            }

            LastStatus = AiDraftGenerationStatus.ProviderFailed(
                ollamaResult.Message,
                ollamaResult.ProviderType,
                ollamaResult.ModelName,
                ollamaResult.LatencyMs,
                groundingSuggestions.Length,
                groundingKnowledgeCards.Length,
                ollamaResult.FailureReason,
                ollamaResult.CharacterCount);
            return [];
        }

        if (!_settings.AllowRemoteAiForCustomerText)
        {
            var degradedDrafts = BuildGroundedFallbackDrafts(
                snapshot,
                groundingSuggestions,
                groundingKnowledgeCards,
                providerRequest.DraftCount,
                "remote_customer_text_not_allowed");
            if (degradedDrafts.Count > 0)
            {
                LastStatus = AiDraftGenerationStatus.Generated(
                    "远端 AI 已禁用客户文本处理，已按命中的短语/知识生成保守草稿。",
                    "grounding_fallback",
                    "rule_based",
                    0,
                    groundingSuggestions.Length,
                    groundingKnowledgeCards.Length,
                    "remote_customer_text_not_allowed",
                    degradedDrafts.Sum(item => item.CharacterCount),
                    degradedDrafts.Count);
                return degradedDrafts;
            }

            LastStatus = AiDraftGenerationStatus.ProviderFailed(
                "已启用 API fallback，但未允许远端处理客户文本，已跳过 AI 草稿。",
                _apiFallbackProvider.ProviderType,
                _settings.AiApiModelName,
                0,
                groundingSuggestions.Length,
                groundingKnowledgeCards.Length,
                "remote_customer_text_not_allowed");
            return [];
        }

        var apiResult = await _apiFallbackProvider
            .GenerateAsync(_settings, providerRequest, CancellationToken.None)
            .ConfigureAwait(false);
        if (apiResult.Success && apiResult.Drafts.Count > 0)
        {
            var drafts = apiResult.Drafts
                .Select(draft => new GeneratedReplyCandidate
                {
                    DisplayRank = draft.DisplayRank,
                    Text = draft.Text,
                    Source = draft.Source,
                    Reason = draft.Reason,
                    Confidence = draft.Confidence,
                        ProviderType = draft.ProviderType,
                        ModelName = draft.ModelName,
                        GroundingPhraseCount = draft.GroundingPhraseCount,
                        GroundingKnowledgeCount = draft.GroundingKnowledgeCount,
                        GroundingKnowledgeIdsJson = draft.GroundingKnowledgeIdsJson,
                        LatencyMs = draft.LatencyMs,
                        CharacterCount = draft.CharacterCount,
                        FallbackReason = ollamaResult.FailureReason,
                    BackingPhraseId = draft.BackingPhraseId,
                    BackingPhraseTitle = draft.BackingPhraseTitle
                })
                .ToArray();
            LastStatus = AiDraftGenerationStatus.Generated(
                apiResult.Message,
                apiResult.ProviderType,
                apiResult.ModelName,
                apiResult.LatencyMs,
                groundingSuggestions.Length,
                groundingKnowledgeCards.Length,
                ollamaResult.FailureReason,
                apiResult.CharacterCount,
                drafts.Length);
            return drafts;
        }

        var fallbackDrafts = BuildGroundedFallbackDrafts(
            snapshot,
            groundingSuggestions,
            groundingKnowledgeCards,
            providerRequest.DraftCount,
            apiResult.FailureReason);
        if (fallbackDrafts.Count > 0)
        {
            LastStatus = AiDraftGenerationStatus.Generated(
                "AI provider 当前不可用，已按命中的短语/知识生成保守草稿。",
                "grounding_fallback",
                "rule_based",
                apiResult.LatencyMs,
                groundingSuggestions.Length,
                groundingKnowledgeCards.Length,
                apiResult.FailureReason,
                fallbackDrafts.Sum(item => item.CharacterCount),
                fallbackDrafts.Count);
            return fallbackDrafts;
        }

        LastStatus = AiDraftGenerationStatus.ProviderFailed(
            apiResult.Message,
            apiResult.ProviderType,
            apiResult.ModelName,
            apiResult.LatencyMs,
            groundingSuggestions.Length,
            groundingKnowledgeCards.Length,
            apiResult.FailureReason,
            apiResult.CharacterCount);
        return [];
    }

    private static bool HasReliableContext(ConversationRecommendationSnapshot snapshot)
    {
        return snapshot is not null &&
               snapshot.RecognitionConfidence >= 0.45d &&
               !string.IsNullOrWhiteSpace(snapshot.RecognizedCustomerText);
    }

    private static IReadOnlyList<GeneratedReplyCandidate> BuildGroundedFallbackDrafts(
        ConversationRecommendationSnapshot snapshot,
        IReadOnlyList<PhraseRecommendationSuggestion> groundingSuggestions,
        IReadOnlyList<KnowledgeCardSuggestion> groundingKnowledgeCards,
        int draftCount,
        string fallbackReason)
    {
        var drafts = new List<GeneratedReplyCandidate>();
        var knowledgeIdsJson = JsonSerializer.Serialize(
            groundingKnowledgeCards.Select(item => item.Card.Id).Distinct().ToArray());

        foreach (var suggestion in groundingSuggestions)
        {
            var content = suggestion.Phrase.Content?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            drafts.Add(new GeneratedReplyCandidate
            {
                DisplayRank = drafts.Count + 1,
                Text = content,
                Source = "ai_draft_grounding_fallback",
                Reason = $"上下文较弱，先沿用命中的{suggestion.SourceBadgeText}：{suggestion.Phrase.DisplayTitle}",
                Confidence = suggestion.IsHistoricalSource ? 0.72d : 0.62d,
                ProviderType = "grounding_fallback",
                ModelName = "rule_based",
                GroundingPhraseCount = groundingSuggestions.Count,
                GroundingKnowledgeCount = groundingKnowledgeCards.Count,
                GroundingKnowledgeIdsJson = knowledgeIdsJson,
                LatencyMs = 0,
                CharacterCount = content.Length,
                FallbackReason = fallbackReason,
                BackingPhraseId = suggestion.Phrase.Id,
                BackingPhraseTitle = suggestion.Phrase.DisplayTitle
            });
        }

        foreach (var knowledgeCard in groundingKnowledgeCards)
        {
            var answer = knowledgeCard.Card.Answer?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(answer))
            {
                continue;
            }

            drafts.Add(new GeneratedReplyCandidate
            {
                DisplayRank = drafts.Count + 1,
                Text = answer,
                Source = "ai_draft_grounding_fallback",
                Reason = $"上下文较弱，先参考已命中的{knowledgeCard.SourceBadgeText}：{knowledgeCard.Card.Question}",
                Confidence = knowledgeCard.IsResourceCard ? 0.56d : 0.68d,
                ProviderType = "grounding_fallback",
                ModelName = "rule_based",
                GroundingPhraseCount = groundingSuggestions.Count,
                GroundingKnowledgeCount = groundingKnowledgeCards.Count,
                GroundingKnowledgeIdsJson = knowledgeIdsJson,
                LatencyMs = 0,
                CharacterCount = answer.Length,
                FallbackReason = fallbackReason,
                BackingPhraseId = null,
                BackingPhraseTitle = string.Empty
            });
        }

        return drafts
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .DistinctBy(item => item.Text)
            .Take(Math.Max(1, draftCount))
            .Select((item, index) => new GeneratedReplyCandidate
            {
                DisplayRank = index + 1,
                Text = item.Text,
                Source = item.Source,
                Reason = item.Reason,
                Confidence = item.Confidence,
                ProviderType = item.ProviderType,
                ModelName = item.ModelName,
                GroundingPhraseCount = item.GroundingPhraseCount,
                GroundingKnowledgeCount = item.GroundingKnowledgeCount,
                GroundingKnowledgeIdsJson = item.GroundingKnowledgeIdsJson,
                LatencyMs = item.LatencyMs,
                CharacterCount = item.CharacterCount,
                FallbackReason = item.FallbackReason,
                BackingPhraseId = item.BackingPhraseId,
                BackingPhraseTitle = item.BackingPhraseTitle
            })
            .ToArray();
    }
}
