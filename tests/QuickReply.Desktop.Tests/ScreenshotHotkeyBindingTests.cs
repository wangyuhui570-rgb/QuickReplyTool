using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class ScreenshotHotkeyBindingTests
{
    [Theory]
    [InlineData("Alt+A", "Alt+A")]
    [InlineData("ctrl+shift+1", "Ctrl+Shift+1")]
    [InlineData("Alt+F4", "Alt+F4")]
    [InlineData("CTRL+SPACE", "Ctrl+Space")]
    public void NormalizesValidHotkeys(string input, string expected)
    {
        var success = ScreenshotHotkeyBinding.TryNormalize(input, out var normalized, out var error);

        Assert.True(success, error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("Ctrl")]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsInvalidHotkeys(string input)
    {
        Assert.False(ScreenshotHotkeyBinding.IsValid(input));
    }
}
