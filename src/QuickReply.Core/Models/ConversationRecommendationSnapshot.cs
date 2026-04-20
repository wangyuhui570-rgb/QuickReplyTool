namespace QuickReply.Desktop.Models;

public sealed class ConversationRecommendationSnapshot
{
    public string PlatformHint { get; init; } = string.Empty;

    public string WindowTitle { get; init; } = string.Empty;

    public string WindowClassName { get; init; } = string.Empty;

    public string ProcessName { get; init; } = string.Empty;

    public string ProcessPath { get; init; } = string.Empty;

    public string CaptureStatus { get; init; } = string.Empty;

    public string CapturedText { get; init; } = string.Empty;

    public string RecognitionStatus { get; init; } = string.Empty;

    public string RecognitionMethod { get; init; } = string.Empty;

    public double RecognitionConfidence { get; init; }

    public string RecognizedCustomerText { get; init; } = string.Empty;

    public string RecognizedContextText { get; init; } = string.Empty;

    public ConversationProductContext Product { get; init; } = new();

    public IReadOnlyList<ConversationMessageTurn> Messages { get; init; } = [];

    public IReadOnlyList<string> Keywords { get; init; } = [];

    public string RecognizedProductId => Product.ProductId;

    public string RecognizedProductName => Product.ProductName;
}
