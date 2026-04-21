using System.IO;
using System.Text.Json;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class PersonalConfigService
{
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true
    };

    public async Task ExportAsync(string filePath, PersonalConfigBundle bundle)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var sanitizedBundle = new PersonalConfigBundle
        {
            SchemaVersion = bundle.SchemaVersion,
            AppVersion = bundle.AppVersion,
            ExportedAt = bundle.ExportedAt,
            Settings = CreateExportSafeSettings(bundle.Settings),
            AppearanceThemes = bundle.AppearanceThemes
        };

        var json = JsonSerializer.Serialize(sanitizedBundle, _serializerOptions);
        await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
    }

    public async Task<PersonalConfigBundle> ImportAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        var bundle = JsonSerializer.Deserialize<PersonalConfigBundle>(json, _serializerOptions);
        if (bundle is null)
        {
            throw new InvalidDataException("配置文件内容无效。");
        }

        bundle.Settings ??= new AppSettings();
        if (!HasScreenshotHotkeyMigrationVersion(json))
        {
            bundle.Settings.ScreenshotToolHotkeyMigrationVersion = 0;
        }

        bundle.Settings.EnsureScreenshotHotkeyMigrationApplied();
        bundle.AppearanceThemes ??= [];
        return bundle;
    }

    private static AppSettings CreateExportSafeSettings(AppSettings? settings)
    {
        if (settings is null)
        {
            return new AppSettings();
        }

        var clone = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
        clone.SyncApiToken = string.Empty;
        return clone;
    }

    private static bool HasScreenshotHotkeyMigrationVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Settings", out var settingsElement) &&
                !document.RootElement.TryGetProperty("settings", out settingsElement))
            {
                return false;
            }

            return settingsElement.TryGetProperty("ScreenshotToolHotkeyMigrationVersion", out _) ||
                   settingsElement.TryGetProperty("screenshotToolHotkeyMigrationVersion", out _);
        }
        catch
        {
            return false;
        }
    }
}
