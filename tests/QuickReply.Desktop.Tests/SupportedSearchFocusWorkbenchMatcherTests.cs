using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class SupportedSearchFocusWorkbenchMatcherTests
{
    [Theory]
    [InlineData("微信", "WeChatMainWndForPC", "WeChat", @"C:\Program Files\Tencent\WeChat\WeChat.exe")]
    [InlineData("企业微信", "WeWorkWindow", "WXWork", @"C:\Program Files\Tencent\WXWork\WXWork.exe")]
    [InlineData("千牛工作台", "StandardFrame", "AliWorkbench", @"C:\Program Files\AliWorkbench\AliWorkbench.exe")]
    [InlineData("拼多多客服工作台", "Chrome_WidgetWin_1", "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData("京东工作台", "JdWorkbench", "JdWork", @"C:\Program Files\JD\JdWork.exe")]
    [InlineData("会话详情", "Chrome_WidgetWin_1", "jingmai", @"C:\Program Files\JD\JingMai\jingmai.exe")]
    [InlineData("抖店 工作台", "Chrome_WidgetWin_1", "msedge", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe")]
    public void IsSupported_ReturnsTrueForConfiguredWorkbenchTargets(string title, string className, string processName, string processPath)
    {
        var result = SupportedSearchFocusWorkbenchMatcher.IsSupported(CreateWindowInfo(title, className, processName, processPath));

        Assert.True(result);
    }

    [Theory]
    [InlineData("Visual Studio Code", "Chrome_WidgetWin_1", "Code", @"C:\Program Files\Microsoft VS Code\Code.exe")]
    [InlineData("Google Chrome", "Chrome_WidgetWin_1", "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData("Notepad", "Notepad", "notepad", @"C:\Windows\System32\notepad.exe")]
    [InlineData("资源管理器", "CabinetWClass", "explorer", @"C:\Windows\explorer.exe")]
    public void IsSupported_ReturnsFalseForNonWorkbenchTargets(string title, string className, string processName, string processPath)
    {
        var result = SupportedSearchFocusWorkbenchMatcher.IsSupported(CreateWindowInfo(title, className, processName, processPath));

        Assert.False(result);
    }

    private static WindowDockTargetInfo CreateWindowInfo(string title, string className, string processName, string processPath)
    {
        return new WindowDockTargetInfo
        {
            Handle = IntPtr.Zero,
            Title = title,
            WindowClassName = className,
            ProcessName = processName,
            ProcessPath = processPath,
            Left = 0,
            Top = 0,
            Width = 0,
            Height = 0
        };
    }
}
