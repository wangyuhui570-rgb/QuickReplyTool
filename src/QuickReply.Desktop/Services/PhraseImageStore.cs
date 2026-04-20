using System.IO;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class PhraseImageStore
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".gif",
        ".webp"
    };

    private readonly string _dataRootPath;
    private string _imagesRootPath;

    public PhraseImageStore(string dataRootPath)
    {
        _dataRootPath = dataRootPath;
        _imagesRootPath = GetDefaultImagesRootPath(dataRootPath);
        Directory.CreateDirectory(_imagesRootPath);
    }

    public string ImagesRootPath => _imagesRootPath;

    public static string GetDefaultImagesRootPath(string dataRootPath)
    {
        return Path.Combine(dataRootPath, "phrase-images");
    }

    public string GetEffectiveRootPath(string? configuredRootPath)
    {
        var normalizedPath = configuredRootPath?.Trim() ?? string.Empty;
        var targetRootPath = string.IsNullOrWhiteSpace(normalizedPath)
            ? GetDefaultImagesRootPath(_dataRootPath)
            : normalizedPath;

        return Path.GetFullPath(targetRootPath);
    }

    public void UpdateRootPath(string? configuredRootPath)
    {
        _imagesRootPath = GetEffectiveRootPath(configuredRootPath);
        Directory.CreateDirectory(_imagesRootPath);
    }

    public async Task<int> MigrateLibraryAsync(string? previousConfiguredRootPath, string? nextConfiguredRootPath)
    {
        var previousRootPath = GetEffectiveRootPath(previousConfiguredRootPath);
        var nextRootPath = GetEffectiveRootPath(nextConfiguredRootPath);

        if (string.Equals(previousRootPath, nextRootPath, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(previousRootPath))
        {
            UpdateRootPath(nextConfiguredRootPath);
            return 0;
        }

        var shouldExcludeNestedTargetFiles = IsNestedUnder(nextRootPath, previousRootPath);
        var sourcePaths = EnumerateManagedStoredFiles(previousRootPath)
            .Where(sourcePath => !shouldExcludeNestedTargetFiles || !IsNestedUnder(sourcePath, nextRootPath))
            .ToList();

        Directory.CreateDirectory(nextRootPath);

        var copiedFileCount = 0;
        foreach (var sourcePath in sourcePaths)
        {
            var relativePath = Path.GetRelativePath(previousRootPath, sourcePath);
            var destinationPath = Path.Combine(nextRootPath, relativePath);
            var destinationFolderPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationFolderPath))
            {
                Directory.CreateDirectory(destinationFolderPath);
            }

            await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await sourceStream.CopyToAsync(destinationStream);
            copiedFileCount++;
        }

        UpdateRootPath(nextConfiguredRootPath);
        return copiedFileCount;
    }

    public static bool IsSupportedImageFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return SupportedExtensions.Contains(Path.GetExtension(path));
    }

    public string? ResolveAbsolutePath(string? relativePath)
    {
        return ResolveAbsolutePaths(relativePath).FirstOrDefault();
    }

    public async Task<IReadOnlyList<string>> MigrateLegacyPhraseImagesAsync(Guid phraseId, string? serializedRelativePaths)
    {
        var currentRelativePaths = ReplyPhrase.DeserializeImageRelativePaths(serializedRelativePaths);
        var legacyFolderPath = GetLegacyPhraseFolderPath(phraseId);
        var hasLegacyRelativePaths = currentRelativePaths.Any(ContainsDirectorySeparator);
        var hasLegacyFolder = Directory.Exists(legacyFolderPath);

        if (!hasLegacyRelativePaths && !hasLegacyFolder)
        {
            return currentRelativePaths;
        }

        var sourcePaths = ResolveAbsolutePaths(serializedRelativePaths).ToList();
        if (sourcePaths.Count == 0 && hasLegacyFolder)
        {
            sourcePaths = Directory
                .EnumerateFiles(legacyFolderPath, "*", SearchOption.TopDirectoryOnly)
                .Where(IsSupportedImageFile)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();
        }

        if (sourcePaths.Count == 0)
        {
            TryDeleteDirectory(legacyFolderPath);
            return [];
        }

        var allSourcesAlreadyFlat = sourcePaths.All(sourcePath => !IsNestedUnder(sourcePath, legacyFolderPath));
        if (!hasLegacyRelativePaths && allSourcesAlreadyFlat)
        {
            return currentRelativePaths;
        }

        return await SavePhraseImagesAsync(phraseId, sourcePaths);
    }

    public int CleanupLegacyPhraseFolders(IEnumerable<Guid> preservedPhraseIds)
    {
        if (!Directory.Exists(_imagesRootPath))
        {
            return 0;
        }

        var preservedSet = preservedPhraseIds.ToHashSet();
        var deletedCount = 0;

        foreach (var directoryPath in EnumerateLegacyPhraseFolders(_imagesRootPath))
        {
            var folderName = Path.GetFileName(directoryPath);
            if (!Guid.TryParseExact(folderName, "N", out var phraseId) || preservedSet.Contains(phraseId))
            {
                continue;
            }

            TryDeleteDirectory(directoryPath);
            if (!Directory.Exists(directoryPath))
            {
                deletedCount++;
            }
        }

        return deletedCount;
    }

    public IReadOnlyList<string> ResolveAbsolutePaths(string? serializedRelativePaths)
    {
        var relativePaths = ReplyPhrase.DeserializeImageRelativePaths(serializedRelativePaths);
        return relativePaths
            .Select(relativePath => Path.Combine(_imagesRootPath, relativePath))
            .Where(File.Exists)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> SavePhraseImagesAsync(Guid phraseId, IEnumerable<string>? sourcePaths)
    {
        var normalizedSources = sourcePaths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Where(path => File.Exists(path) && IsSupportedImageFile(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList() ?? [];

        SearchOverlayDiagnostics.Log(
            "phrase_images",
            "store_save_started",
            new Dictionary<string, object?>
            {
                ["phraseId"] = phraseId.ToString("N"),
                ["sourceCount"] = normalizedSources.Count,
                ["sourcePaths"] = string.Join("|", normalizedSources)
            });

        Directory.CreateDirectory(_imagesRootPath);

        var phraseKey = phraseId.ToString("N");
        var existingManagedPaths = EnumerateManagedImagePaths(phraseId).ToArray();
        var legacyPhraseFolder = GetLegacyPhraseFolderPath(phraseId);
        PhraseImagePreviewContentFactory.InvalidatePaths(existingManagedPaths);
        PhraseImagePreviewContentFactory.InvalidateFolder(legacyPhraseFolder);

        var operationKey = Guid.NewGuid().ToString("N");
        var replacements = new List<ManagedReplacement>();
        var backupPaths = new List<string>();
        var newFinalPaths = new List<string>();
        var savedRelativePaths = new List<string>();
        var existingFinalPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (var index = 0; index < normalizedSources.Count; index++)
            {
                var sourcePath = normalizedSources[index];
                var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
                var finalFileName = $"{phraseKey}-image-{index + 1}{extension}";
                var finalPath = Path.Combine(_imagesRootPath, finalFileName);
                var tempPath = Path.Combine(_imagesRootPath, $".tmp-{operationKey}-{index + 1}{extension}");

                await using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                await using (var destinationStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await sourceStream.CopyToAsync(destinationStream);
                }

                replacements.Add(new ManagedReplacement(tempPath, finalPath));
                savedRelativePaths.Add(finalFileName);
                newFinalPaths.Add(finalPath);
                if (File.Exists(finalPath))
                {
                    existingFinalPathSet.Add(finalPath);
                }
            }

            foreach (var replacement in replacements)
            {
                if (File.Exists(replacement.FinalPath))
                {
                    var backupPath = Path.Combine(_imagesRootPath, $".bak-{operationKey}-{Path.GetFileName(replacement.FinalPath)}");
                    File.Replace(replacement.TempPath, replacement.FinalPath, backupPath, ignoreMetadataErrors: true);
                    backupPaths.Add(backupPath);
                }
                else
                {
                    File.Move(replacement.TempPath, replacement.FinalPath);
                }
            }

            foreach (var existingManagedPath in existingManagedPaths)
            {
                if (newFinalPaths.Contains(existingManagedPath, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                TryDeleteFile(existingManagedPath);
            }

            TryDeleteDirectory(legacyPhraseFolder);

            foreach (var backupPath in backupPaths)
            {
                TryDeleteFile(backupPath);
            }
        }
        catch
        {
            foreach (var replacement in replacements)
            {
                TryDeleteFile(replacement.TempPath);
            }

            foreach (var backupPath in backupPaths)
            {
                var finalFileName = Path.GetFileName(backupPath).Replace($".bak-{operationKey}-", string.Empty, StringComparison.Ordinal);
                var finalPath = Path.Combine(_imagesRootPath, finalFileName);
                if (File.Exists(finalPath))
                {
                    TryDeleteFile(finalPath);
                }

                File.Move(backupPath, finalPath, overwrite: true);
            }

            foreach (var newFinalPath in newFinalPaths)
            {
                if (!existingFinalPathSet.Contains(newFinalPath))
                {
                    TryDeleteFile(newFinalPath);
                }
            }

            throw;
        }

        PhraseImagePreviewContentFactory.InvalidatePaths(newFinalPaths);

        SearchOverlayDiagnostics.Log(
            "phrase_images",
            "store_save_completed",
            new Dictionary<string, object?>
            {
                ["phraseId"] = phraseId.ToString("N"),
                ["savedCount"] = savedRelativePaths.Count,
                ["savedRelativePaths"] = string.Join("|", savedRelativePaths)
            });

        return savedRelativePaths;
    }

    public void DeletePhraseImage(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var resolvedAbsolutePaths = ResolveAbsolutePaths(relativePath);
        PhraseImagePreviewContentFactory.InvalidatePaths(resolvedAbsolutePaths);

        foreach (var singlePath in ReplyPhrase.DeserializeImageRelativePaths(relativePath))
        {
            var fullPath = Path.Combine(_imagesRootPath, singlePath);
            TryDeleteFile(fullPath);

            var folder = Path.GetDirectoryName(fullPath);
            TryDeleteEmptyDirectory(folder);
        }
    }

    public void DeletePhraseImageFolder(Guid phraseId)
    {
        var legacyPhraseFolder = GetLegacyPhraseFolderPath(phraseId);
        PhraseImagePreviewContentFactory.InvalidateFolder(legacyPhraseFolder);

        foreach (var managedPath in EnumerateManagedImagePaths(phraseId))
        {
            PhraseImagePreviewContentFactory.InvalidatePaths([managedPath]);
            TryDeleteFile(managedPath);
        }

        TryDeleteDirectory(legacyPhraseFolder);
    }

    public void ClearAll()
    {
        if (!Directory.Exists(_imagesRootPath))
        {
            return;
        }

        foreach (var filePath in EnumerateManagedFlatFiles(_imagesRootPath))
        {
            TryDeleteFile(filePath);
        }

        foreach (var directory in EnumerateLegacyPhraseFolders(_imagesRootPath))
        {
            TryDeleteDirectory(directory);
        }
    }

    private IEnumerable<string> EnumerateManagedImagePaths(Guid phraseId)
    {
        if (!Directory.Exists(_imagesRootPath))
        {
            yield break;
        }

        var prefix = $"{phraseId:N}-image-";
        foreach (var filePath in Directory.EnumerateFiles(_imagesRootPath, $"{prefix}*.*", SearchOption.TopDirectoryOnly))
        {
            if (IsSupportedImageFile(filePath))
            {
                yield return filePath;
            }
        }
    }

    private static IEnumerable<string> EnumerateManagedStoredFiles(string rootPath)
    {
        foreach (var filePath in EnumerateManagedFlatFiles(rootPath))
        {
            yield return filePath;
        }

        foreach (var legacyFolder in EnumerateLegacyPhraseFolders(rootPath))
        {
            foreach (var filePath in Directory.EnumerateFiles(legacyFolder, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsSupportedImageFile(filePath))
                {
                    yield return filePath;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateManagedFlatFiles(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsManagedFlatImageFile(filePath))
            {
                yield return filePath;
            }
        }
    }

    private static IEnumerable<string> EnumerateLegacyPhraseFolders(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(rootPath, "*", SearchOption.TopDirectoryOnly))
        {
            var folderName = Path.GetFileName(directoryPath);
            if (Guid.TryParseExact(folderName, "N", out _))
            {
                yield return directoryPath;
            }
        }
    }

    private string GetLegacyPhraseFolderPath(Guid phraseId)
    {
        return Path.Combine(_imagesRootPath, phraseId.ToString("N"));
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(folderPath).Any())
            {
                Directory.Delete(folderPath, recursive: false);
            }
        }
        catch
        {
        }
    }

    private static bool IsNestedUnder(string path, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsDirectorySeparator(string path)
    {
        return path.Contains(Path.DirectorySeparatorChar) || path.Contains(Path.AltDirectorySeparatorChar);
    }

    private static bool IsInternalManagedArtifact(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.StartsWith(".tmp-", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith(".bak-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManagedFlatImageFile(string filePath)
    {
        if (!IsSupportedImageFile(filePath) || IsInternalManagedArtifact(filePath))
        {
            return false;
        }

        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
        {
            return false;
        }

        var separatorIndex = fileNameWithoutExtension.IndexOf("-image-", StringComparison.Ordinal);
        if (separatorIndex != 32)
        {
            return false;
        }

        return Guid.TryParseExact(fileNameWithoutExtension[..32], "N", out _);
    }

    private sealed record ManagedReplacement(string TempPath, string FinalPath);
}
