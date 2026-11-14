using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;

namespace QuickReply.Desktop.Tests;

public sealed class FileLibraryCatalogServiceTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public FileLibraryCatalogServiceTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task MeasureBulkSyncAndVerifyAllFilesPersisted()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        for (var index = 0; index < 500; index++)
        {
            scope.CreateLibraryFile($"file-{index:D4}.txt");
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await new FileLibraryCatalogService(store).SyncAsync([], scope.LibraryRootPath);
        timer.Stop();
        Assert.Equal(500, result.AddedItems.Count);
        Assert.Equal(500, (await store.LoadAsync()).FileItems.Count);
        _output.WriteLine($"FILE_SYNC count=500 elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
    }

    [Fact]
    public async Task SyncAsync_RemovesMissingItemsAndAddsNewManagedFiles()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();

        var knownPath = scope.CreateLibraryFile("docs\\known.txt", "known");
        var newPath = scope.CreateLibraryFile("docs\\new.txt", "new");
        var missingPath = Path.Combine(scope.LibraryRootPath, "docs", "missing.txt");

        var service = new FileLibraryCatalogService(store);
        var knownItem = new LibraryFileItem { Id = Guid.NewGuid(), DisplayName = "known.txt", FullPath = knownPath };
        var missingItem = new LibraryFileItem { Id = Guid.NewGuid(), DisplayName = "missing.txt", FullPath = missingPath };
        await store.UpsertFileItemAsync(knownItem);
        await store.UpsertFileItemAsync(missingItem);

        var result = await service.SyncAsync([knownItem, missingItem], scope.LibraryRootPath);

        Assert.Contains(result.RemovedItems, item => item.Id == missingItem.Id);
        Assert.Contains(result.AddedItems, item => string.Equals(item.FullPath, newPath, StringComparison.OrdinalIgnoreCase));

        var reloaded = await store.LoadAsync();
        Assert.DoesNotContain(reloaded.FileItems, item => item.Id == missingItem.Id);
        Assert.Contains(reloaded.FileItems, item => string.Equals(item.FullPath, newPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AddItemsAsync_SkipsMissingAndDuplicatePaths()
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();

        var existingPath = scope.CreateFile("manual\\existing.txt", "existing");
        var newPath = scope.CreateFile("manual\\new.txt", "new");
        var missingPath = Path.Combine(scope.RootPath, "manual", "missing.txt");

        var existingItem = new LibraryFileItem { Id = Guid.NewGuid(), DisplayName = "existing.txt", FullPath = existingPath };
        await store.UpsertFileItemAsync(existingItem);

        var service = new FileLibraryCatalogService(store);
        var added = await service.AddItemsAsync([existingPath, newPath, newPath, missingPath], [existingItem]);

        var addedItem = Assert.Single(added);
        Assert.Equal(newPath, addedItem.FullPath);
    }
}
