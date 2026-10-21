using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace QuickReply.Desktop.Tests;

public sealed class SqliteAppDataStoreTests
{
    [Fact]
    public async Task LoadAndUpdateSettings_RoundTripsThroughTempDatabase()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();

        await store.LoadAsync();

        var settings = new AppSettings
        {
            ServerBaseUrl = "https://sync.example.com/",
            SyncClientId = "client-001",
            SyncApiToken = "token-1234",
            SearchOverlayWidthPercent = 20,
            RecommendationDisplayCount = 4
        };

        await store.UpdateSettingsAsync(settings);

        var filePath = scope.CreateLibraryFile("docs\\guide.txt", "guide");
        await store.UpsertFileItemAsync(new LibraryFileItem
        {
            DisplayName = "产品说明",
            FullPath = filePath
        });

        var reloaded = await store.LoadAsync();
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = 'sync_api_token' LIMIT 1;";
        var persistedTokenValue = (string?)await command.ExecuteScalarAsync();

        Assert.Equal(Path.Combine(scope.AppRootPath, "quick-reply.db"), store.DatabasePath);
        Assert.Equal("https://sync.example.com", reloaded.Settings.ServerBaseUrl);
        Assert.Equal("token-1234", reloaded.Settings.SyncApiToken);
        Assert.Equal(40, reloaded.Settings.SearchOverlayWidthPercent);
        Assert.Equal(4, reloaded.Settings.RecommendationDisplayCount);
        Assert.Equal(string.Empty, persistedTokenValue);
        Assert.Contains(reloaded.FileItems, item => item.DisplayName == "产品说明" && item.FullPath == filePath);
    }

    [Fact]
    public async Task LoadSettingsOnlyAsync_MigratesLegacyScreenshotHotkey()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();

        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync();

            foreach (var key in new[] { "screenshot_tool_hotkey", "screenshot_tool_hotkey_migration_version" })
            {
                await using var deleteCommand = connection.CreateCommand();
                deleteCommand.CommandText = "DELETE FROM app_settings WHERE key = $key;";
                deleteCommand.Parameters.AddWithValue("$key", key);
                await deleteCommand.ExecuteNonQueryAsync();
            }

            await using var insertHotkeyCommand = connection.CreateCommand();
            insertHotkeyCommand.CommandText = "INSERT INTO app_settings (key, value) VALUES ('screenshot_tool_hotkey', $value);";
            insertHotkeyCommand.Parameters.AddWithValue("$value", ScreenshotHotkeyBinding.LegacyDefaultHotkey);
            await insertHotkeyCommand.ExecuteNonQueryAsync();
        }

        var settings = await store.LoadSettingsOnlyAsync();

        Assert.Equal(ScreenshotHotkeyBinding.DefaultHotkey, settings.ScreenshotToolHotkey);
        Assert.Equal(ScreenshotHotkeyBinding.CurrentMigrationVersion, settings.ScreenshotToolHotkeyMigrationVersion);
        Assert.True(settings.ScreenshotHotkeyMigrationTouched);
    }

    [Fact]
    public async Task LoadSettingsOnlyAsync_MigratesInvalidServerBaseUrlToBuiltInDefault()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();

        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO app_settings (key, value)
                VALUES ('server_base_url', $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$value", "ftp://invalid-service");
            await command.ExecuteNonQueryAsync();
        }

        var settings = await store.LoadSettingsOnlyAsync();

        await using var verifyConnection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await verifyConnection.OpenAsync();
        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = "SELECT value FROM app_settings WHERE key = 'server_base_url' LIMIT 1;";
        var persistedValue = (string?)await verifyCommand.ExecuteScalarAsync();

        Assert.Equal(string.Empty, settings.ServerBaseUrl);
        Assert.Equal(string.Empty, persistedValue);
    }


}
