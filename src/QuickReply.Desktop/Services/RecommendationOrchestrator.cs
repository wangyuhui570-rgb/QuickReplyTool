using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class RecommendationOrchestrator : IRecommendationOrchestrator
{
    private readonly IExternalWorkbenchService _externalWorkbenchService;
    private readonly IConversationContextCaptureService _contextCaptureService;
    private readonly IContextRecognizer _contextRecognizer;
    private readonly ICandidateRetriever _candidateRetriever;
    private readonly IReplyGenerator _replyGenerator;

    public RecommendationOrchestrator(
        IExternalWorkbenchService externalWorkbenchService,
        IConversationContextCaptureService contextCaptureService,
        IContextRecognizer contextRecognizer,
        ICandidateRetriever candidateRetriever,
        IReplyGenerator replyGenerator)
    {
        _externalWorkbenchService = externalWorkbenchService;
        _contextCaptureService = contextCaptureService;
        _contextRecognizer = contextRecognizer;
        _candidateRetriever = candidateRetriever;
        _replyGenerator = replyGenerator;
    }

    public async Task<PhraseRecommendationContext> GetRecommendationContextAsync(RecommendationRequest request)
    {
        var targetWindowHandle = ResolveTargetWindowHandle(request.TargetWindowHandle);
        if (targetWindowHandle == IntPtr.Zero)
        {
            return new PhraseRecommendationContext();
        }

        var capturedContext = await _contextCaptureService
            .CaptureConversationContextAsync(
                targetWindowHandle,
                includeScreenshot: true,
                enableOcr: true)
            .ConfigureAwait(false);
        var snapshot = _contextRecognizer.RecognizeContext(capturedContext);
        var retrievalResult = await _candidateRetriever
            .RetrieveCandidatesAsync(snapshot, request)
            .ConfigureAwait(false);
        var generatedReplies = await _replyGenerator
            .GenerateCandidatesAsync(snapshot, retrievalResult, request)
            .ConfigureAwait(false);
        var aiDraftStatus = (_replyGenerator as IAiDraftGenerationStatusSource)?.LastStatus
                            ?? AiDraftGenerationStatus.Disabled(string.Empty);

        return new PhraseRecommendationContext
        {
            Snapshot = snapshot,
            Suggestions = retrievalResult.Suggestions,
            KnowledgeCards = retrievalResult.KnowledgeCards,
            SimilarReplyGroups = retrievalResult.SimilarReplyGroups,
            GeneratedReplies = generatedReplies,
            AiDraftStatus = aiDraftStatus
        };
    }

    private IntPtr ResolveTargetWindowHandle(IntPtr preferredWindowHandle)
    {
        if (preferredWindowHandle != IntPtr.Zero)
        {
            return preferredWindowHandle;
        }

        return _externalWorkbenchService.TryGetLastExternalWindowInfo(out var windowInfo) && windowInfo is not null
            ? windowInfo.Handle
            : IntPtr.Zero;
    }
}
