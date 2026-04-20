namespace QuickReply.CloudService;

public interface IKnowledgeCandidateSynthesizer
{
    Task<KnowledgeCandidateRecord?> SynthesizeAsync(
        KnowledgeCandidateRecord candidate,
        IReadOnlyList<KnowledgeCandidateSourceRecord> sources,
        CancellationToken cancellationToken = default);
}

