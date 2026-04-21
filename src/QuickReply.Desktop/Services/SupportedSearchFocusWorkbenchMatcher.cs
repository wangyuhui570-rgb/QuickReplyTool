namespace QuickReply.Desktop.Services;

internal static class SupportedSearchFocusWorkbenchMatcher
{
    public static bool IsSupported(WindowDockTargetInfo? targetInfo)
    {
        if (targetInfo is null)
        {
            return false;
        }

        var platformHint = ConversationLoggingService.InferPlatformHint(
            targetInfo.Title,
            targetInfo.ProcessName,
            targetInfo.ProcessPath,
            targetInfo.WindowClassName);
        if (platformHint is "wechat" or "wecom" or "qianniu" or "jd_workbench" or "pdd_workbench")
        {
            return true;
        }

        var combined = string.Join(
            "\n",
            [
                targetInfo.Title,
                targetInfo.WindowClassName,
                targetInfo.ProcessName,
                targetInfo.ProcessPath
            ]);

        return ContainsAny(
            combined,
            "douyin",
            "抖音工作台",
            "抖店",
            "doudian");
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
