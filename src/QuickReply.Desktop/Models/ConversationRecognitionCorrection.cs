namespace QuickReply.Desktop.Models;

public sealed class ConversationRecognitionCorrection
{
    public string CustomerText { get; init; } = string.Empty;

    public string ContextText { get; init; } = string.Empty;

    public string ProductId { get; init; } = string.Empty;

    public string ProductName { get; init; } = string.Empty;

    public IReadOnlyList<ConversationMessageTurn> Messages { get; init; } = [];
}
