namespace QuickReply.Desktop.Models;

public sealed class RecommendationRequest
{
    public IntPtr TargetWindowHandle { get; init; }

    public RecommendationQueryScope? QueryScope { get; init; }

    public int DisplayCount { get; init; } = 5;

    public int MinimumSampleCount { get; init; } = 8;

    public int GeneratedReplyCount { get; init; } = 3;
}
