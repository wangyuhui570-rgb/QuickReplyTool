using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;

namespace QuickReply.Desktop.Tests;

public sealed class AppearanceThemeCatalogServiceTests
{
    [Fact]
    public async Task SaveLoadAndDeleteAsync_ManagesThemesByName()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        var service = new AppearanceThemeCatalogService(store);

        await service.SaveAsync(new AppearanceThemePreset
        {
            Name = "晨雾",
            CategoryColorHex = "#112233",
            SearchInputColorHex = "#445566"
        });

        await service.SaveAsync(new AppearanceThemePreset
        {
            Name = "晨雾",
            CategoryColorHex = "#ABCDEF",
            SearchInputColorHex = "#778899"
        });

        var loaded = await service.LoadAsync();
        var savedTheme = Assert.Single(loaded);
        Assert.Equal("#ABCDEF", savedTheme.CategoryColorHex);
        Assert.Equal("#778899", savedTheme.SearchInputColorHex);

        await service.DeleteAsync("晨雾");

        var afterDelete = await service.LoadAsync();
        Assert.Empty(afterDelete);
    }
}
