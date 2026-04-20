namespace QuickReply.Desktop.Services;

public static class SearchOverlayActionFeedbackFormatter
{
    public static string BuildMessage(ReplyActionResult result)
    {
        if (result.Success || string.IsNullOrWhiteSpace(result.UserGuidance))
        {
            return result.Message;
        }

        return $"{result.Message} 建议：{result.UserGuidance}";
    }

    public static bool IsWarning(ReplyActionResult result)
    {
        return !result.Success;
    }
}
