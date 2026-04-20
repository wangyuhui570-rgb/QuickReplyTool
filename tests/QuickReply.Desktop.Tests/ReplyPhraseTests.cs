using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Tests;

public sealed class ReplyPhraseTests
{
    [Fact]
    public void SerializeImageRelativePaths_DeduplicatesTrimsAndLimitsToThree()
    {
        var serialized = ReplyPhrase.SerializeImageRelativePaths(
        [
            " image-1.png ",
            "image-1.png",
            "folder\\image-2.png",
            "image-3.png",
            "image-4.png"
        ]);

        Assert.Equal("image-1.png|folder\\image-2.png|image-3.png", serialized);
    }

    [Fact]
    public void DisplayText_UsesSingleLineContentAndSuppressesPlaceholderTitle()
    {
        var phrase = new ReplyPhrase
        {
            Title = "未命名短语",
            Content = "第一行\r\n第二行\n第三行"
        };

        Assert.Equal("第一行 第二行 第三行", phrase.SingleLineContent);
        Assert.Equal("第一行 第二行 第三行", phrase.DisplayText);
        Assert.False(phrase.HasDisplayTitle);
    }
}
