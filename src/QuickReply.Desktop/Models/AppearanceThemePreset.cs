namespace QuickReply.Desktop.Models;

public sealed class AppearanceThemePreset
{
    public string Name { get; set; } = string.Empty;

    public string CategoryColorHex { get; set; } = "#D8EAFE";

    public int CategoryOpacityPercent { get; set; } = 100;

    public string TagColorHex { get; set; } = "#DBEAFE";

    public int TagOpacityPercent { get; set; } = 100;

    public string PhraseColorHex { get; set; } = "#F8FAFC";

    public int PhraseOpacityPercent { get; set; } = 100;

    public string SidebarColorHex { get; set; } = "#FFFFFF";

    public int SidebarOpacityPercent { get; set; } = 96;

    public string SearchOverlayColorHex { get; set; } = "#FFFFFF";

    public int SearchOverlayOpacityPercent { get; set; } = 96;

    public string SearchInputColorHex { get; set; } = "#FFFFFF";

    public int SearchInputOpacityPercent { get; set; } = 100;
}
