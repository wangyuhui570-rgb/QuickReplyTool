namespace QuickReply.Desktop.Models;

public sealed class ConversationProductContext
{
    public string ProductId { get; init; } = string.Empty;

    public string ProductName { get; init; } = string.Empty;

    public bool HasValue =>
        !string.IsNullOrWhiteSpace(ProductId) ||
        !string.IsNullOrWhiteSpace(ProductName);
}
