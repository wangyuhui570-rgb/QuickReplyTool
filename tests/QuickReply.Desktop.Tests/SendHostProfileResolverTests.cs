using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class SendHostProfileResolverTests
{
    [Theory]
    [InlineData("拼多多商家工作台", "Chrome_WidgetWin_1", "PDDWorkbench", "pdd_workbench")]
    [InlineData("千牛接待台", "Qt5152QWindowIcon", "AliWorkbench", "qianniu")]
    [InlineData("Microsoft Edge", "Chrome_WidgetWin_1", "msedge", "browser_hosted_input")]
    [InlineData("记事本", "Notepad", "notepad", "generic_desktop_input")]
    public void Resolve_ReturnsExpectedProfile(string title, string className, string processName, string expectedProfileName)
    {
        var profile = SendHostProfileResolver.Resolve(new WindowDockTargetInfo
        {
            Handle = new IntPtr(1),
            Title = title,
            WindowClassName = className,
            ProcessName = processName,
            ProcessPath = $"C:\\Program Files\\{processName}\\{processName}.exe",
            Left = 0,
            Top = 0,
            Width = 1280,
            Height = 720
        });

        Assert.Equal(expectedProfileName, profile.Name);
    }
}
