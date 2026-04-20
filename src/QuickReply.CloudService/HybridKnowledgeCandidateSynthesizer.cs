using Microsoft.Extensions.Options;

namespace QuickReply.CloudService;

public sealed class HybridKnowledgeCandidateSynthesizer : IKnowledgeCandidateSynthesizer
{
    private readonly IOptions<KnowledgeCloudOptions> _options;
    private readonly HeuristicKnowledgeCandidateSynthesizer _heuristicSynthesizer;
    private readonly CodexCliKnowledgeCandidateSynthesizer _codexCliSynthesizer;
    private readonly OllamaKnowledgeCandidateSynthesizer _ollamaSynthesizer;
    private readonly OpenAiKnowledgeCandidateSynthesizer _openAiSynthesizer;

    public HybridKnowledgeCandidateSynthesizer(
        IOptions<KnowledgeCloudOptions> options,
        HeuristicKnowledgeCandidateSynthesizer heuristicSynthesizer,
        CodexCliKnowledgeCandidateSynthesizer codexCliSynthesizer,
        OllamaKnowledgeCandidateSynthesizer ollamaSynthesizer,
        OpenAiKnowledgeCandidateSynthesizer openAiSynthesizer)
    {
        _options = options;
        _heuristicSynthesizer = heuristicSynthesizer;
        _codexCliSynthesizer = codexCliSynthesizer;
        _ollamaSynthesizer = ollamaSynthesizer;
        _openAiSynthesizer = openAiSynthesizer;
    }

    public async Task<KnowledgeCandidateRecord?> SynthesizeAsync(
        KnowledgeCandidateRecord candidate,
        IReadOnlyList<KnowledgeCandidateSourceRecord> sources,
        CancellationToken cancellationToken = default)
    {
        var baseline = await _heuristicSynthesizer
            .SynthesizeAsync(candidate, sources, cancellationToken)
            .ConfigureAwait(false);
        if (baseline is null)
        {
            return null;
        }

        if (!KnowledgeCandidateQualityRules.ShouldUseAiSynthesis(baseline, sources))
        {
            return baseline;
        }

        try
        {
            var aiAttempt = await TryAiSynthesisAsync(candidate, sources, cancellationToken).ConfigureAwait(false);
            if (aiAttempt.Result is null)
            {
                return aiAttempt.Attempted ? CreateHeuristicFallback(baseline) : baseline;
            }

            var tags = baseline.Tags
                .Concat(aiAttempt.Result.Tags)
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new KnowledgeCandidateRecord
            {
                Id = baseline.Id,
                Status = baseline.Status,
                CandidateKind = baseline.CandidateKind,
                Platform = baseline.Platform,
                ProductKey = baseline.ProductKey,
                QuestionNormalized = string.IsNullOrWhiteSpace(aiAttempt.Result.Question)
                    ? baseline.QuestionNormalized
                    : HeuristicKnowledgeCandidateSynthesizer.NormalizeQuestion(aiAttempt.Result.Question),
                AnswerDraft = string.IsNullOrWhiteSpace(aiAttempt.Result.AnswerDraft)
                    ? baseline.AnswerDraft
                    : aiAttempt.Result.AnswerDraft,
                AnswerFinal = baseline.AnswerFinal,
                Tags = tags,
                Confidence = aiAttempt.Result.Confidence ?? baseline.Confidence,
                RiskLevel = string.IsNullOrWhiteSpace(aiAttempt.Result.RiskLevel)
                    ? baseline.RiskLevel
                    : aiAttempt.Result.RiskLevel,
                SourceCount = baseline.SourceCount,
                ClusterKey = baseline.ClusterKey,
                EvidenceSummary = string.IsNullOrWhiteSpace(aiAttempt.Result.EvidenceSummary)
                    ? baseline.EvidenceSummary
                    : aiAttempt.Result.EvidenceSummary,
                ReviewComment = string.IsNullOrWhiteSpace(aiAttempt.Result.ProviderComment)
                    ? baseline.ReviewComment
                    : aiAttempt.Result.ProviderComment,
                ReviewReason = baseline.ReviewReason,
                CreatedAt = baseline.CreatedAt,
                UpdatedAt = DateTimeOffset.Now
            };
        }
        catch
        {
            return CreateHeuristicFallback(baseline);
        }
    }

    private async Task<AiSynthesisAttempt> TryAiSynthesisAsync(
        KnowledgeCandidateRecord candidate,
        IReadOnlyList<KnowledgeCandidateSourceRecord> sources,
        CancellationToken cancellationToken)
    {
        var provider = (_options.Value.AiProvider ?? string.Empty).Trim().ToLowerInvariant();
        switch (provider)
        {
            case "codex_cli":
                return await TrySingleProviderAsync(_codexCliSynthesizer, candidate, sources, cancellationToken).ConfigureAwait(false);
            case "openai":
            case "openai_api":
                return await TrySingleProviderAsync(_openAiSynthesizer, candidate, sources, cancellationToken).ConfigureAwait(false);
            case "ollama":
                return await TrySingleProviderAsync(_ollamaSynthesizer, candidate, sources, cancellationToken).ConfigureAwait(false);
            case "auto":
            default:
                var codexAttempt = await TrySingleProviderAsync(_codexCliSynthesizer, candidate, sources, cancellationToken).ConfigureAwait(false);
                if (codexAttempt.Result is not null)
                {
                    return codexAttempt;
                }

                if (IsOpenAiConfigured())
                {
                    var openAiAttempt = await TrySingleProviderAsync(_openAiSynthesizer, candidate, sources, cancellationToken).ConfigureAwait(false);
                    if (openAiAttempt.Result is not null)
                    {
                        return openAiAttempt;
                    }

                    var ollamaAttemptAfterOpenAi = await TrySingleProviderAsync(_ollamaSynthesizer, candidate, sources, cancellationToken).ConfigureAwait(false);
                    return new AiSynthesisAttempt
                    {
                        Attempted = codexAttempt.Attempted || openAiAttempt.Attempted || ollamaAttemptAfterOpenAi.Attempted,
                        Result = ollamaAttemptAfterOpenAi.Result
                    };
                }

                var ollamaAttempt = await TrySingleProviderAsync(_ollamaSynthesizer, candidate, sources, cancellationToken).ConfigureAwait(false);
                return new AiSynthesisAttempt
                {
                    Attempted = codexAttempt.Attempted || ollamaAttempt.Attempted,
                    Result = ollamaAttempt.Result
                };
        }
    }

    private static async Task<AiSynthesisAttempt> TrySingleProviderAsync(
        IAiKnowledgeCandidateSynthesizer provider,
        KnowledgeCandidateRecord candidate,
        IReadOnlyList<KnowledgeCandidateSourceRecord> sources,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await provider.TrySynthesizeAsync(candidate, sources, cancellationToken).ConfigureAwait(false);
            return new AiSynthesisAttempt
            {
                Attempted = true,
                Result = result
            };
        }
        catch
        {
            return new AiSynthesisAttempt
            {
                Attempted = true,
                Result = null
            };
        }
    }

    private bool IsOpenAiConfigured()
    {
        var options = _options.Value;
        return !string.IsNullOrWhiteSpace(options.OpenAiApiKey) &&
               !string.IsNullOrWhiteSpace(options.OpenAiModelName) &&
               !string.IsNullOrWhiteSpace(options.OpenAiBaseUrl);
    }

    private static KnowledgeCandidateRecord CreateHeuristicFallback(KnowledgeCandidateRecord baseline)
    {
        return new KnowledgeCandidateRecord
        {
            Id = baseline.Id,
            Status = baseline.Status,
            CandidateKind = baseline.CandidateKind,
            Platform = baseline.Platform,
            ProductKey = baseline.ProductKey,
            QuestionNormalized = baseline.QuestionNormalized,
            AnswerDraft = baseline.AnswerDraft,
            AnswerFinal = baseline.AnswerFinal,
            Tags = baseline.Tags,
            Confidence = baseline.Confidence,
            RiskLevel = baseline.RiskLevel,
            SourceCount = baseline.SourceCount,
            ClusterKey = baseline.ClusterKey,
            EvidenceSummary = baseline.EvidenceSummary,
            ReviewComment = "synthesized_by_heuristic_fallback",
            ReviewReason = baseline.ReviewReason,
            CreatedAt = baseline.CreatedAt,
            UpdatedAt = DateTimeOffset.Now
        };
    }

    private sealed class AiSynthesisAttempt
    {
        public bool Attempted { get; init; }
        public AiKnowledgeSynthesisResult? Result { get; init; }
    }
}
