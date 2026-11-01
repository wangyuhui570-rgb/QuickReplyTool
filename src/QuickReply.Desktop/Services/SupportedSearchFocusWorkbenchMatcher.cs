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

        // Browser tabs that merely mention Douyin (video search, the consumer site) are not
        // a customer-service workbench. SendHostProfileResolver already requires a workbench
        // title or the doudian/feige process; do not also match the bare word "douyin".
        return SendHostProfileResolver.Resolve(targetInfo).Kind == SendHostKind.DouyinWorkbench;
    }
}
