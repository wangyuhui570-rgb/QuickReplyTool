using System.IO;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal sealed record FileLibrarySyncResult(
    IReadOnlyList<LibraryFileItem> RemovedItems,
    IReadOnlyList<LibraryFileItem> AddedItems);

internal sealed class FileLibraryCatalogService
{
    private readonly SqliteAppDataStore _dataStore;

    public FileLibraryCatalogService(SqliteAppDataStore dataStore)
    {
        _dataStore = dataStore;
    }

    public async Task<FileLibrarySyncResult> SyncAsync(
        IReadOnlyCollection<LibraryFileItem> existingItems,
        string? managedFolderPath)
    {
        var removedItems = existingItems
            .Where(fileItem => !File.Exists(fileItem.FullPath))
            .ToList();

        foreach (var removedItem in removedItems)
        {
            await _dataStore.DeleteFileItemAsync(removedItem.Id);
        }

        var knownPaths = existingItems
            .Select(fileItem => Path.GetFullPath(fileItem.FullPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedItems = new List<LibraryFileItem>();
        foreach (var filePath in EnumerateAutoManagedFilePaths(managedFolderPath))
        {
            if (!knownPaths.Add(filePath))
            {
                continue;
            }

            var now = DateTimeOffset.Now;
            var fileItem = new LibraryFileItem
            {
                Id = Guid.NewGuid(),
                DisplayName = Path.GetFileName(filePath),
                FullPath = filePath,
                CreatedAt = now,
                UpdatedAt = now
            };

            await _dataStore.UpsertFileItemAsync(fileItem);
            addedItems.Add(fileItem);
        }

        return new FileLibrarySyncResult(removedItems, addedItems);
    }

    public async Task<IReadOnlyList<LibraryFileItem>> AddItemsAsync(
        IEnumerable<string> filePaths,
        IReadOnlyCollection<LibraryFileItem> existingItems)
    {
        var addedItems = new List<LibraryFileItem>();
        var normalizedPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var path in normalizedPaths)
        {
            if (!File.Exists(path) || existingItems.Any(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var now = DateTimeOffset.Now;
            var fileItem = new LibraryFileItem
            {
                Id = Guid.NewGuid(),
                DisplayName = Path.GetFileName(path),
                FullPath = path,
                CreatedAt = now,
                UpdatedAt = now
            };

            await _dataStore.UpsertFileItemAsync(fileItem);
            addedItems.Add(fileItem);
        }

        return addedItems;
    }

    public Task DeleteItemAsync(Guid fileItemId)
    {
        return _dataStore.DeleteFileItemAsync(fileItemId);
    }

    internal static IEnumerable<string> EnumerateAutoManagedFilePaths(string? folderPath)
    {
        var normalizedFolder = folderPath?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedFolder) || !Directory.Exists(normalizedFolder))
        {
            yield break;
        }

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFiles(normalizedFolder, "*", SearchOption.AllDirectories).GetEnumerator();
        }
        catch
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = enumerator.MoveNext();
                }
                catch
                {
                    yield break;
                }

                if (!hasNext)
                {
                    yield break;
                }

                var filePath = enumerator.Current;
                string normalizedPath;
                try
                {
                    normalizedPath = Path.GetFullPath(filePath);
                }
                catch
                {
                    continue;
                }

                if (File.Exists(normalizedPath))
                {
                    yield return normalizedPath;
                }
            }
        }
    }
}
