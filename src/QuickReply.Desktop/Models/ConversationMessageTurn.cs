namespace QuickReply.Desktop.Models;

public sealed class ConversationMessageTurn
{
    public int Index { get; init; }

    public string Text { get; init; } = string.Empty;

    public double Score { get; init; }

    public string Reason { get; init; } = string.Empty;

    public bool SelectedForCustomerText { get; init; }

    public bool IncludedInContext { get; init; }
}
