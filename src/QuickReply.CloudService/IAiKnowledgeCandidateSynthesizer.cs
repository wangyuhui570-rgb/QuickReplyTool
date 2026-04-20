namespace QuickReply.CloudService;

public interface IAiKnowledgeCandidateSynthesizer
{
    Task<AiKnowledgeSynthesisResult?> TrySynthesizeAsync(
        KnowledgeCandidateRecord candidate,
        IReadOnlyList<KnowledgeCandidateSourceRecord> sources,
        CancellationToken cancellationToken = default);
}
