using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class PersonalConfigServiceTests
{
    [Fact]
    public async Task ImportAsync_MigratesLegacyScreenshotHotkeyWhenVersionMissing()
    {
        var service = new PersonalConfigService();
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            await File.WriteAllTextAsync(filePath, """
                {
                  "Settings": {
                    "ScreenshotToolHotkey": "Ctrl+Alt+A"
                  },
                  "AppearanceThemes": []
                }
                """);

            var bundle = await service.ImportAsync(filePath);

            Assert.Equal(ScreenshotHotkeyBinding.DefaultHotkey, bundle.Settings.ScreenshotToolHotkey);
            Assert.Equal(ScreenshotHotkeyBinding.CurrentMigrationVersion, bundle.Settings.ScreenshotToolHotkeyMigrationVersion);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task ExportAsync_RemovesSensitiveSecretsFromBundle()
    {
        var service = new PersonalConfigService();
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            await service.ExportAsync(filePath, new PersonalConfigBundle
            {
                Settings = new AppSettings
                {
                    SyncApiToken = "sync-secret",
                    AiApiKey = "ai-secret"
                }
            });

            var json = await File.ReadAllTextAsync(filePath);

            Assert.DoesNotContain("sync-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("ai-secret", json, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
