using System;

namespace QuickReply.Desktop.Models;

public sealed class FrequentReplyPhraseSuggestion : EventArgs
{
    public string PayloadText { get; init; } = string.Empty;

    public int UseCount { get; init; }

    public string CustomerText { get; init; } = string.Empty;

    public string SuggestedTitle { get; init; } = string.Empty;

    public string SuggestedTags { get; init; } = string.Empty;
}
