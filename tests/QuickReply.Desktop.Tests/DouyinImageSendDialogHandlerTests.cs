using System.Windows;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class DouyinImageSendDialogHandlerTests
{
    [Theory]
    [InlineData("发送", true)]
    [InlineData("发 送", true)]
    [InlineData("发送(1)", true)]
    [InlineData("确定", true)]
    [InlineData("发送1张", true)]
    [InlineData("发送视频", false)]
    [InlineData("取消", false)]
    [InlineData("发送给好友的说明很长很长", false)]
    [InlineData("", false)]
    public void IsConfirmName_RecognisesDialogButtons(string name, bool expected)
    {
        Assert.Equal(expected, DouyinImageSendDialogHandler.IsConfirmName(DouyinImageSendDialogHandler.NormalizeName(name)));
    }

    [Fact]
    public void IsSameRect_ToleratesSubPixelShifts()
    {
        Assert.True(DouyinImageSendDialogHandler.IsSameRect(new Rect(10, 10, 60, 30), new Rect(11, 9, 61, 30)));
        Assert.False(DouyinImageSendDialogHandler.IsSameRect(new Rect(10, 10, 60, 30), new Rect(400, 300, 60, 30)));
    }

    [Fact]
    public async Task TryConfirmAsync_ReturnsNotFoundWithoutAWindow()
    {
        var handler = new DouyinImageSendDialogHandler();
        var diagnostics = new Dictionary<string, object?>();

        var outcome = await handler.TryConfirmAsync(IntPtr.Zero, new DouyinSendButtonSnapshot([]), diagnostics);

        Assert.Equal(DouyinImageDialogOutcome.NotFound, outcome);
    }
}
