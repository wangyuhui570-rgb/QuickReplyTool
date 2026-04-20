namespace QuickReply.CloudService;

public sealed class KnowledgeCandidateSynthesisService
{
    private readonly KnowledgeCloudStore _store;
    private readonly IKnowledgeCandidateSynthesizer _synthesizer;
    private readonly KnowledgeCloudOptions _options;

    public KnowledgeCandidateSynthesisService(
        KnowledgeCloudStore store,
        IKnowledgeCandidateSynthesizer synthesizer,
        Microsoft.Extensions.Options.IOptions<KnowledgeCloudOptions> options)
    {
        _store = store;
        _synthesizer = synthesizer;
        _options = options.Value;
    }

    public async Task<KnowledgeImportRunResponse> SynthesizeDraftCandidatesAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var candidates = await _store
            .ClaimKnowledgeCandidatesAsync(
                ["draft"],
                limit,
                TimeSpan.FromSeconds(Math.Clamp(_options.SynthesisLeaseDurationSeconds, 30, 3600)),
                cancellationToken)
            .ConfigureAwait(false);

        var processedCount = 0;
        var sourceCount = 0;
        var failedCount = 0;
        var confidenceTotal = 0d;
        var correctionSourceCount = 0;
        var recommendationSourceCount = 0;
        var createdAt = DateTimeOffset.Now;

        foreach (var candidate in candidates)
        {
            try
            {
                var sources = await _store
                    .GetKnowledgeCandidateSourcesAsync(candidate.Id, cancellationToken)
                    .ConfigureAwait(false);
                sourceCount += sources.Count;
                correctionSourceCount += sources.Count(source => string.Equals(source.SourceType, "correction", StringComparison.OrdinalIgnoreCase));
                recommendationSourceCount += sources.Count(source => string.Equals(source.SourceType, "recommendation_accept", StringComparison.OrdinalIgnoreCase));

                var synthesized = await _synthesizer
                    .SynthesizeAsync(candidate, sources, cancellationToken)
                    .ConfigureAwait(false);
                if (synthesized is null)
                {
                    failedCount++;
                    await _store.ReleaseKnowledgeCandidateClaimAsync(candidate.Id, candidate.LeaseToken, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await _store.UpsertKnowledgeCandidateAsync(synthesized, sources, cancellationToken).ConfigureAwait(false);
                processedCount++;
                confidenceTotal += synthesized.Confidence;
            }
            catch
            {
                failedCount++;
                await _store.ReleaseKnowledgeCandidateClaimAsync(candidate.Id, candidate.LeaseToken, cancellationToken).ConfigureAwait(false);
            }
        }

        var jobRunId = Guid.NewGuid();
        var avgConfidence = processedCount == 0 ? 0d : Math.Round(confidenceTotal / processedCount, 3);
        await _store.InsertKnowledgeJobRunAsync(new KnowledgeJobRunRecord
        {
            Id = jobRunId,
            SampleCount = sourceCount,
            CandidateCount = processedCount,
            PublishCount = 0,
            RejectCount = 0,
            AvgConfidence = avgConfidence,
            FailedClusterCount = 0,
            FailedSynthesizeCount = failedCount,
            CreatedAt = createdAt
        }, cancellationToken).ConfigureAwait(false);

        return new KnowledgeImportRunResponse
        {
            JobRunId = jobRunId,
            InputPath = "draft-candidates",
            SampleCount = sourceCount,
            CandidateCount = processedCount,
            PublishCount = 0,
            RejectCount = 0,
            AvgConfidence = avgConfidence,
            FailedClusterCount = 0,
            FailedSynthesizeCount = failedCount,
            ImportedCorrectionSourceCount = correctionSourceCount,
            ImportedRecommendationSourceCount = recommendationSourceCount,
            Message = processedCount == 0
                ? "没有可综合的 draft 候选，或当前候选缺少足够来源。"
                : $"已综合整理 {processedCount} 条候选知识。",
            CreatedAt = createdAt
        };
    }

}
