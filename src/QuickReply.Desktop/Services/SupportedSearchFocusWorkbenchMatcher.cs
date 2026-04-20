namespace QuickReply.Desktop.Services;

internal static class SupportedSearchFocusWorkbenchMatcher
{
    public static bool IsSupported(WindowDockTargetInfo? targetInfo)
    {
        if (targetInfo is null)
        {
            return false;
        }

        var combined = string.Join(
            "\n",
            [
                targetInfo.Title,
                targetInfo.WindowClassName,
                targetInfo.ProcessName,
                targetInfo.ProcessPath
            ]);

        return ContainsAny(combined,
            "微信",
            "wechat",
            "wechatapp",
            "weixin",
            "企业微信",
            "wecom",
            "wxwork",
            "千牛",
            "qianniu",
            "aliworkbench",
            "阿里旺旺",
            "旺旺",
            "拼多多",
            "pdd",
            "pinduoduo",
            "多多客服",
            "京东",
            "京东工作台",
            "京东咚咚",
            "京麦",
            "jdwork",
            "jingdong",
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
