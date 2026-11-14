using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public static class JdRecommendationContextMapper
{
    public static ConversationRecommendationSnapshot ToSnapshot(JdRecommendationContext context)
    {
        var contextText = string.Join(
            Environment.NewLine,
            context.RecentMessages
                .Where(message => !string.IsNullOrWhiteSpace(message.Content))
                .Select(message => $"{FormatSpeaker(message.Speaker)}：{message.Content.Trim()}"));
        var customerText = context.TriggerMessage.Content.Trim();
        var productName = context.Session.GoodsName.Trim();

        return new ConversationRecommendationSnapshot
        {
            PlatformHint = "jd_workbench",
            WindowTitle = "京东工作台",
            ProcessName = "JDWorkbench",
            CaptureStatus = "jd_api",
            CapturedText = contextText,
            RecognitionStatus = "recognized",
            RecognitionMethod = "jd_api",
            RecognitionConfidence = 1.0d,
            RecognizedCustomerText = customerText,
            RecognizedContextText = contextText,
            Product = new ConversationProductContext
            {
                ProductName = productName
            },
            Messages = context.RecentMessages
                .Select((message, index) => new ConversationMessageTurn
                {
                    Index = index,
                    Text = $"{FormatSpeaker(message.Speaker)}：{message.Content}",
                    Score = message.MessageKey == context.TriggerMessage.MessageKey ? 1.0d : 0.5d,
                    Reason = message.MessageKey == context.TriggerMessage.MessageKey ? "latest_customer_message" : "recent_context",
                    SelectedForCustomerText = message.MessageKey == context.TriggerMessage.MessageKey,
                    IncludedInContext = true
                })
                .ToArray(),
            Keywords = RecommendationKeywordBuilder.Build(customerText, contextText, productName: productName)
        };
    }

    private static string FormatSpeaker(string speaker)
    {
        return string.Equals(speaker, "waiter", StringComparison.OrdinalIgnoreCase) ? "客服" : "客户";
    }
}
