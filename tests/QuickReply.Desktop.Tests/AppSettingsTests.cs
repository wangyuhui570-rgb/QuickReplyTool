using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void NormalizesServerBaseUrlAndHotkeyValues()
    {
        var settings = new AppSettings
        {
            ServerBaseUrl = " https://updates.example.com/// ",
            ScreenshotToolHotkey = "   ",
            FileLibraryFolderPath = "  C:\\kb  "
        };

        Assert.Equal("https://updates.example.com", settings.ServerBaseUrl);
        Assert.Equal(ScreenshotHotkeyBinding.DefaultHotkey, settings.ScreenshotToolHotkey);
        Assert.Equal("C:\\kb", settings.FileLibraryFolderPath);
    }

    [Fact]
    public void MigratesLegacyScreenshotHotkeyOnce()
    {
        var settings = new AppSettings
        {
            ScreenshotToolHotkey = ScreenshotHotkeyBinding.LegacyDefaultHotkey,
            ScreenshotToolHotkeyMigrationVersion = 0
        };

        var changed = settings.EnsureScreenshotHotkeyMigrationApplied();

        Assert.True(changed);
        Assert.Equal(ScreenshotHotkeyBinding.DefaultHotkey, settings.ScreenshotToolHotkey);
        Assert.Equal(ScreenshotHotkeyBinding.CurrentMigrationVersion, settings.ScreenshotToolHotkeyMigrationVersion);
    }

    [Fact]
    public void DoesNotMigrateCustomizedScreenshotHotkey()
    {
        var settings = new AppSettings
        {
            ScreenshotToolHotkey = "Ctrl+Shift+1",
            ScreenshotToolHotkeyMigrationVersion = 0
        };

        var changed = settings.EnsureScreenshotHotkeyMigrationApplied();

        Assert.True(changed);
        Assert.Equal("Ctrl+Shift+1", settings.ScreenshotToolHotkey);
        Assert.Equal(ScreenshotHotkeyBinding.CurrentMigrationVersion, settings.ScreenshotToolHotkeyMigrationVersion);
    }

    [Fact]
    public void ClampsDelaysAndLayoutValues()
    {
        var settings = new AppSettings
        {
            RestoreFocusDelayMs = -1,
            SearchOverlayWidthPercent = 5,
            SearchOverlayHeightPercent = 80,
            RecommendationDisplayCount = 12,
            SidebarWidth = 800
        };

        Assert.Equal(120, settings.RestoreFocusDelayMs);
        Assert.Equal(40, settings.SearchOverlayWidthPercent);
        Assert.Equal(56, settings.SearchOverlayHeightPercent);
        Assert.Equal(5, settings.RecommendationDisplayCount);
        Assert.Equal(620, settings.SidebarWidth);
    }

    [Fact]
    public void FallsBackToDefaultColorsWhenBlank()
    {
        var settings = new AppSettings
        {
            CategoryColorHex = " ",
            SearchOverlayColorHex = ""
        };

        Assert.Equal("#0071E3", settings.CategoryColorHex);
        Assert.Equal("#FFFFFF", settings.SearchOverlayColorHex);
    }

    [Fact]
    public void UsesExpectedLocalDefaults()
    {
        var settings = new AppSettings();

        Assert.False(settings.AllowRawConversationCloudSync);
        Assert.False(settings.AllowAutomaticIssueReportUpload);
        Assert.Equal(30, settings.LogTextRetentionDays);
    }
}
