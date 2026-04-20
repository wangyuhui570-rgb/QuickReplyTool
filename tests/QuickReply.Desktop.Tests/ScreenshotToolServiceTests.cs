using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class ScreenshotToolServiceTests
{
    [Fact]
    public void NormalizeConfiguration_UsesAltAFallbackForBlankHotkey()
    {
        var configuration = ScreenshotToolService.NormalizeConfiguration(new ScreenshotToolConfiguration(true, " ", ""));

        Assert.Equal(ScreenshotHotkeyBinding.DefaultHotkey, configuration.Hotkey);
        Assert.False(string.IsNullOrWhiteSpace(configuration.SaveDirectory));
    }
}
