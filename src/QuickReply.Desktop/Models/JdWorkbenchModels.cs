namespace QuickReply.Desktop.Models;

public sealed class JdWorkbenchProfile
{
    public string DisplayName { get; init; } = string.Empty;

    public string ProfilePath { get; init; } = string.Empty;

    public DateTimeOffset LastModifiedAt { get; init; }
}

public sealed class JdChatSession
{
    public string Cid { get; init; } = string.Empty;

    public string Customer { get; init; } = string.Empty;

    public string GoodsName { get; init; } = string.Empty;

    public string Service { get; init; } = string.Empty;

    public string ConsultationDate { get; init; } = string.Empty;

    public string RawMarker { get; init; } = string.Empty;
}

public sealed class JdChatMessage
{
    public string MessageKey { get; init; } = string.Empty;

    public string Cid { get; init; } = string.Empty;

    public string Speaker { get; init; } = string.Empty;

    public string MessageType { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public DateTimeOffset? CreatedAt { get; init; }
}

public sealed class JdRecommendationContext
{
    public required JdChatSession Session { get; init; }

    public IReadOnlyList<JdChatMessage> RecentMessages { get; init; } = [];

    public required JdChatMessage TriggerMessage { get; init; }
}

public sealed class JdMonitorStatus
{
    public string State { get; init; } = "stopped";

    public string Message { get; init; } = "京东监听未开启。";

    public string ProfilePath { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}
