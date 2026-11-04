using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuickReply.Desktop.Services;

/// <summary>
/// Turns an installed version plus a patch zip into the same folder the full zip would extract to.
/// A patch zip holds only the files that changed, at their package paths, and patch-manifest.json:
/// <c>{"from":"2.1.20","to":"2.1.21","files":{"app/QuickReplyTool.dll":"&lt;sha256&gt;", ...}}</c>,
/// where "files" lists every file of the new package. scripts/build-update-patch.ps1 writes it.
/// </summary>
internal static class AppUpdatePatcher
{
    /// <summary>Builds <paramref name="stagingRoot"/>; throws when anything does not match, leaving no staging folder.</summary>
    public static void Apply(string installRoot, string patchZipPath, string stagingRoot, string expectedFrom, string expectedTo)
    {
        if (Directory.Exists(stagingRoot))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }

        try
        {
            ApplyCore(installRoot, patchZipPath, stagingRoot, expectedFrom, expectedTo);
        }
        catch
        {
            try
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
            catch
            {
            }

            throw;
        }
    }

    private static void ApplyCore(string installRoot, string patchZipPath, string stagingRoot, string expectedFrom, string expectedTo)
    {
        using var archive = ZipFile.OpenRead(patchZipPath);
        var manifestEntry = archive.GetEntry(AppUpdatePolicy.PatchManifestFileName)
            ?? throw new InvalidDataException("补丁缺少 " + AppUpdatePolicy.PatchManifestFileName);
        var manifest = ReadManifest(manifestEntry);
        if (!SameVersion(manifest.From, expectedFrom) || !SameVersion(manifest.To, expectedTo))
        {
            throw new InvalidDataException($"补丁版本不符：{manifest.From}→{manifest.To}，需要 {expectedFrom}→{expectedTo}");
        }

        var installedApp = Path.Combine(installRoot, "app");
        if (!Directory.Exists(installedApp))
        {
            throw new DirectoryNotFoundException("找不到已安装的 app 文件夹：" + installedApp);
        }

        // 1. Start from the installed files the new version still lists.
        foreach (var relative in manifest.Files.Keys)
        {
            var source = Path.Combine(installRoot, ToLocalPath(relative));
            if (File.Exists(source))
            {
                var target = Path.Combine(stagingRoot, ToLocalPath(relative));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }
        }

        // 2. Lay the changed files over them.
        var stagingFull = Path.GetFullPath(stagingRoot) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') ||
                string.Equals(entry.FullName, AppUpdatePolicy.PatchManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(stagingRoot, ToLocalPath(entry.FullName)));
            if (!target.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("补丁里有越界路径：" + entry.FullName);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }

        // 3. Every file of the new package must be there with the expected content.
        foreach (var (relative, expectedSha) in manifest.Files)
        {
            var path = Path.Combine(stagingRoot, ToLocalPath(relative));
            if (!File.Exists(path))
            {
                throw new InvalidDataException("应用补丁后缺少文件：" + relative);
            }

            if (!string.Equals(ComputeSha256(path), expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("应用补丁后校验不一致：" + relative);
            }
        }

        if (!File.Exists(Path.Combine(stagingRoot, "app", "QuickReplyTool.exe")))
        {
            throw new InvalidDataException("应用补丁后缺少 app\\QuickReplyTool.exe");
        }
    }

    internal sealed record PatchManifest(string From, string To, IReadOnlyDictionary<string, string> Files);

    internal static PatchManifest ReadManifest(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("files", out var filesElement) && filesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var file in filesElement.EnumerateObject())
            {
                var relative = file.Name.Replace('\\', '/').TrimStart('/');
                if (relative.Length == 0 || relative.Split('/').Any(part => part == ".."))
                {
                    throw new InvalidDataException("补丁清单里有越界路径：" + file.Name);
                }

                files[relative] = file.Value.GetString() ?? string.Empty;
            }
        }

        if (files.Count == 0)
        {
            throw new InvalidDataException("补丁清单没有文件列表");
        }

        return new PatchManifest(
            root.TryGetProperty("from", out var from) ? from.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("to", out var to) ? to.GetString() ?? string.Empty : string.Empty,
            files);
    }

    private static bool SameVersion(string a, string b) =>
        AppUpdatePolicy.TryParseVersion(a) is { } left &&
        AppUpdatePolicy.TryParseVersion(b) is { } right &&
        left == right;

    private static string ToLocalPath(string relative) =>
        relative.Replace('/', Path.DirectorySeparatorChar);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
