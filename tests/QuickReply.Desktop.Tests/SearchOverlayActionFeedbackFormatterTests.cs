using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class SearchOverlayActionFeedbackFormatterTests
{
    [Fact]
    public void BuildMessage_UsesRawMessageForSuccess()
    {
        var result = new ReplyActionResult(true, "已发送短语。");

        var message = SearchOverlayActionFeedbackFormatter.BuildMessage(result);

        Assert.Equal("已发送短语。", message);
        Assert.False(SearchOverlayActionFeedbackFormatter.IsWarning(result));
    }

    [Fact]
    public void BuildMessage_AppendsGuidanceForFailure()
    {
        var result = new ReplyActionResult(
            false,
            "目标输入窗口焦点已丢失，请先点一下聊天输入框，再重试。",
            FailureKind: ReplyActionFailureKind.FocusLost,
            UserGuidance: "请重新点击聊天输入框后重试。");

        var message = SearchOverlayActionFeedbackFormatter.BuildMessage(result);

        Assert.Contains("建议：请重新点击聊天输入框后重试。", message, StringComparison.Ordinal);
        Assert.True(SearchOverlayActionFeedbackFormatter.IsWarning(result));
    }
}
