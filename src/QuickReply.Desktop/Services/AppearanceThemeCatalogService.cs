using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal sealed class AppearanceThemeCatalogService
{
    private readonly SqliteAppDataStore _dataStore;

    public AppearanceThemeCatalogService(SqliteAppDataStore dataStore)
    {
        _dataStore = dataStore;
    }

    public async Task<IReadOnlyList<AppearanceThemePreset>> LoadAsync()
    {
        var themes = await _dataStore.LoadAppearanceThemesAsync();
        return themes.Select(Clone).ToList();
    }

    public async Task SaveAsync(AppearanceThemePreset theme)
    {
        var themes = (await _dataStore.LoadAppearanceThemesAsync())
            .Where(item => !string.Equals(item.Name, theme.Name, StringComparison.OrdinalIgnoreCase))
            .Select(Clone)
            .ToList();
        themes.Add(Clone(theme));

        await _dataStore.SaveAppearanceThemesAsync(
            themes.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());
    }

    public async Task DeleteAsync(string themeName)
    {
        var themes = (await _dataStore.LoadAppearanceThemesAsync())
            .Where(item => !string.Equals(item.Name, themeName, StringComparison.OrdinalIgnoreCase))
            .Select(Clone)
            .ToArray();

        await _dataStore.SaveAppearanceThemesAsync(themes);
    }

    public static AppearanceThemePreset Clone(AppearanceThemePreset theme)
    {
        return new AppearanceThemePreset
        {
            Name = theme.Name,
            CategoryColorHex = theme.CategoryColorHex,
            CategoryOpacityPercent = theme.CategoryOpacityPercent,
            TagColorHex = theme.TagColorHex,
            TagOpacityPercent = theme.TagOpacityPercent,
            PhraseColorHex = theme.PhraseColorHex,
            PhraseOpacityPercent = theme.PhraseOpacityPercent,
            SidebarColorHex = theme.SidebarColorHex,
            SidebarOpacityPercent = theme.SidebarOpacityPercent,
            SearchOverlayColorHex = theme.SearchOverlayColorHex,
            SearchOverlayOpacityPercent = theme.SearchOverlayOpacityPercent,
            SearchInputColorHex = theme.SearchInputColorHex,
            SearchInputOpacityPercent = theme.SearchInputOpacityPercent
        };
    }
}
