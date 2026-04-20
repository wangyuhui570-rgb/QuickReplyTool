namespace QuickReply.CloudService;

public sealed class HeuristicKnowledgeCandidateSynthesizer : IKnowledgeCandidateSynthesizer
{
    public Task<KnowledgeCandidateRecord?> SynthesizeAsync(
        KnowledgeCandidateRecord candidate,
        IReadOnlyList<KnowledgeCandidateSourceRecord> sources,
        CancellationToken cancellationToken = default)
    {
        if (sources.Count == 0)
        {
            return Task.FromResult<KnowledgeCandidateRecord?>(null);
        }

        var question = NormalizeQuestion(candidate.QuestionNormalized);
        if (KnowledgeCandidateQualityRules.IsLowValueQuestion(question))
        {
            return Task.FromResult<KnowledgeCandidateRecord?>(null);
        }

        var answers = sources
            .Select(source => ExtractAnswer(source.Excerpt))
            .Where(answer => !KnowledgeCandidateQualityRules.IsLowValueAnswer(answer))
            .ToArray();
        var synthesizedAnswer = answers
            .GroupBy(answer => answer)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key.Length)
            .Select(group => group.Key)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(synthesizedAnswer))
        {
            return Task.FromResult<KnowledgeCandidateRecord?>(null);
        }

        if (!KnowledgeCandidateQualityRules.ShouldKeepImportedCandidate(
                candidate.CandidateKind,
                question,
                synthesizedAnswer,
                sources.Count,
                candidate.Confidence,
                fromKeywordFallback: false))
        {
            return Task.FromResult<KnowledgeCandidateRecord?>(null);
        }

        var tags = candidate.Tags
            .Concat(BuildTags(candidate.CandidateKind, candidate.Platform, question, synthesizedAnswer))
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var riskLevel = InferRiskLevel(question, synthesizedAnswer);
        var groupedSources = sources
            .GroupBy(source => source.SourceType)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var evidenceSummary =
            $"类型：{(string.Equals(candidate.CandidateKind, KnowledgeCandidateKinds.Resource, StringComparison.OrdinalIgnoreCase) ? "资料卡" : "问答卡")}；来源汇总：conversation {groupedSources.GetValueOrDefault("conversation", 0)} 条，" +
            $"correction {groupedSources.GetValueOrDefault("correction", 0)} 条，" +
            $"recommendation_accept {groupedSources.GetValueOrDefault("recommendation_accept", 0)} 条。";

        return Task.FromResult<KnowledgeCandidateRecord?>(new KnowledgeCandidateRecord
        {
            Id = candidate.Id,
            Status = "reviewed",
            CandidateKind = candidate.CandidateKind,
            Platform = candidate.Platform,
            ProductKey = candidate.ProductKey,
            QuestionNormalized = question,
            AnswerDraft = synthesizedAnswer,
            AnswerFinal = candidate.AnswerFinal,
            Tags = tags,
            Confidence = candidate.Confidence,
            RiskLevel = riskLevel,
            SourceCount = candidate.SourceCount,
            ClusterKey = candidate.ClusterKey,
            EvidenceSummary = evidenceSummary,
            ReviewComment = "synthesized_by_heuristic",
            ReviewReason = string.Empty,
            CreatedAt = candidate.CreatedAt,
            UpdatedAt = DateTimeOffset.Now
        });
    }

    public static string NormalizeQuestion(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace("\r", " ").Replace("\n", " ");
    }

    public static string ExtractAnswer(string excerpt)
    {
        if (string.IsNullOrWhiteSpace(excerpt))
        {
            return string.Empty;
        }

        var separatorIndex = excerpt.IndexOf("=>", StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return string.Empty;
        }

        return excerpt[(separatorIndex + 2)..].Trim();
    }

    public static IEnumerable<string> BuildTags(string candidateKind, string platform, string question, string answer)
    {
        foreach (var tag in KnowledgeCandidateQualityRules.BuildTags(candidateKind, platform, question, answer))
        {
            yield return tag;
        }
    }

    public static string InferRiskLevel(string question, string answer)
    {
        return KnowledgeCandidateQualityRules.InferRiskLevel(question, answer);
    }
}
