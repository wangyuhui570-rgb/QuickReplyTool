namespace QuickReply.Desktop.Models;

public sealed class PhraseRecommendationCandidate
{
    public Guid PhraseId { get; init; }

    public string PhraseTitle { get; init; } = string.Empty;

    public string PhraseCategoryName { get; init; } = string.Empty;

    public string MatchedKeyword { get; init; } = string.Empty;

    public int MatchCount { get; init; }

    public int SampleCount { get; init; }

    public double Confidence { get; init; }

    public string RecognizedCustomerText { get; init; } = string.Empty;
}
