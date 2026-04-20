namespace QuickReply.Desktop.Models;

public sealed class PersonalConfigBundle
{
    public string SchemaVersion { get; set; } = "1";

    public string AppVersion { get; set; } = string.Empty;

    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;

    public AppSettings Settings { get; set; } = new();

    public List<AppearanceThemePreset> AppearanceThemes { get; set; } = [];
}
