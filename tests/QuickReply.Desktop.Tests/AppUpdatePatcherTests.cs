using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class AppUpdatePatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qr-patch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Apply_ProducesTheNewPackage()
    {
        var install = CreateInstall(new() { ["app/QuickReplyTool.exe"] = "exe-1", ["app/QuickReplyTool.dll"] = "dll-1", ["app/old.dll"] = "gone", ["start.cmd"] = "cmd" });
        var target = new Dictionary<string, string> { ["app/QuickReplyTool.exe"] = "exe-1", ["app/QuickReplyTool.dll"] = "dll-2", ["app/new.dll"] = "new", ["start.cmd"] = "cmd" };
        var patch = CreatePatch("2.1.20", "2.1.21", target, changed: ["app/QuickReplyTool.dll", "app/new.dll"]);
        var staging = Path.Combine(_root, "staging");

        AppUpdatePatcher.Apply(install, patch, staging, "2.1.20", "2.1.21");

        Assert.Equal("dll-2", File.ReadAllText(Path.Combine(staging, "app", "QuickReplyTool.dll")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(staging, "app", "new.dll")));
        Assert.Equal("exe-1", File.ReadAllText(Path.Combine(staging, "app", "QuickReplyTool.exe")));
        Assert.Equal("cmd", File.ReadAllText(Path.Combine(staging, "start.cmd")));
        // Files the new version no longer has are not carried over.
        Assert.False(File.Exists(Path.Combine(staging, "app", "old.dll")));
    }

    [Fact]
    public void Apply_RejectsWrongVersionAndLeavesNoStaging()
    {
        var install = CreateInstall(new() { ["app/QuickReplyTool.exe"] = "exe" });
        var patch = CreatePatch("2.1.20", "2.1.21", new() { ["app/QuickReplyTool.exe"] = "exe" }, changed: []);
        var staging = Path.Combine(_root, "staging");

        Assert.Throws<InvalidDataException>(() => AppUpdatePatcher.Apply(install, patch, staging, "2.1.19", "2.1.21"));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void Apply_RejectsAnInstallThatWasModified()
    {
        // The user's copy of an unchanged file differs from the release: the result would not
        // match the full package, so the patch must fail and the full zip be used instead.
        var install = CreateInstall(new() { ["app/QuickReplyTool.exe"] = "locally-edited" });
        var patch = CreatePatch("2.1.20", "2.1.21", new() { ["app/QuickReplyTool.exe"] = "exe" }, changed: []);

        Assert.Throws<InvalidDataException>(() => AppUpdatePatcher.Apply(install, patch, Path.Combine(_root, "staging"), "2.1.20", "2.1.21"));
    }

    [Fact]
    public void Apply_RejectsPathsOutsideStaging()
    {
        var install = CreateInstall(new() { ["app/QuickReplyTool.exe"] = "exe" });
        var patchPath = Path.Combine(_root, "evil.zip");
        using (var zip = ZipFile.Open(patchPath, ZipArchiveMode.Create))
        {
            Write(zip, "patch-manifest.json", Manifest("2.1.20", "2.1.21", new() { ["app/QuickReplyTool.exe"] = "exe" }));
            Write(zip, "../outside.txt", "x");
        }

        Assert.Throws<InvalidDataException>(() => AppUpdatePatcher.Apply(install, patchPath, Path.Combine(_root, "staging"), "2.1.20", "2.1.21"));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public void ReleasesJson_ReadsPatchAssetAndKeepsFullDigestSeparate()
    {
        var full = new string('a', 64);
        var patchSha = new string('b', 64);
        var json = $$"""
        [{"tag_name":"v2.1.21","draft":false,"body":"SHA256: {{full}}\r\nPatchFrom: 2.1.20\r\nPatchSHA256: {{patchSha}}",
          "assets":[
            {"name":"QuickReplyTool-Portable-2.1.21.zip","browser_download_url":"https://github.com/a/b/full.zip","size":80000000},
            {"name":"QuickReplyTool-Patch-2.1.20-to-2.1.21.zip","browser_download_url":"https://github.com/a/b/patch.zip","size":500000}]}]
        """;

        var info = AppUpdatePolicy.SelectLatestFromReleasesJson(json);

        Assert.NotNull(info);
        Assert.Equal(full, info!.Sha256);
        Assert.NotNull(info.Patch);
        Assert.Equal("2.1.20", info.Patch!.FromVersion);
        Assert.Equal(patchSha, info.Patch.Sha256);
        Assert.Equal(500000, info.Patch.SizeBytes);
    }

    [Fact]
    public void SignedManifest_DropsPatchWithoutItsOwnSignature()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
        var sha = new string('c', 64);
        var url = AppUpdatePolicy.ReleaseDownloadPrefix + "v2.1.21/Q.zip";
        var patchUrl = AppUpdatePolicy.ReleaseDownloadPrefix + "v2.1.21/P.zip";
        string Sign(string payload) => Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256));
        var signature = Sign(AppUpdatePolicy.BuildManifestSigningPayload("2.1.21", "Q.zip", url, sha));
        var patchSignature = Sign(AppUpdatePolicy.BuildPatchSigningPayload("2.1.20", "P.zip", patchUrl, sha));

        string Manifest(string patchSig) =>
            JsonSerializer.Serialize(new
            {
                version = "2.1.21",
                zipName = "Q.zip",
                url,
                sha256 = sha,
                signature,
                patch = new { from = "2.1.20", zipName = "P.zip", url = patchUrl, sha256 = sha, size = 1, signature = patchSig }
            });

        Assert.NotNull(AppUpdatePolicy.ParseSignedManifest(Manifest(patchSignature), publicKey)!.Patch);
        var unsignedPatch = AppUpdatePolicy.ParseSignedManifest(Manifest(""), publicKey);
        Assert.NotNull(unsignedPatch);
        Assert.Null(unsignedPatch!.Patch);
    }

    private string CreateInstall(Dictionary<string, string> files)
    {
        var install = Path.Combine(_root, "install");
        foreach (var (relative, content) in files)
        {
            var path = Path.Combine(install, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return install;
    }

    private string CreatePatch(string from, string to, Dictionary<string, string> target, IReadOnlyList<string> changed)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"patch-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(zip, "patch-manifest.json", Manifest(from, to, target));
        foreach (var name in changed)
        {
            Write(zip, name, target[name]);
        }

        return path;
    }

    private static string Manifest(string from, string to, Dictionary<string, string> target)
    {
        var files = target.ToDictionary(pair => pair.Key, pair => Sha(pair.Value));
        return JsonSerializer.Serialize(new { from, to, files });
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Sha(string content) =>
        Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(content))).ToLowerInvariant();
}
