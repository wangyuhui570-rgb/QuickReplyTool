using System.IO;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class AppUpdateServiceTests
{
    private const string Sha = "3a6ecdef8c02469f269a950a01055173f3930b3f21f80674f73f849a8bb5d961";

    [Theory]
    [InlineData("v2.1.15-douyin-test", "2.1.15")]
    [InlineData("2.1.15", "2.1.15")]
    [InlineData("V3.0", "3.0.0")]
    [InlineData("v2.1.15.2", "2.1.15.2")]
    public void TryParseVersion_IgnoresPrefixAndSuffix(string input, string expected)
    {
        var version = AppUpdatePolicy.TryParseVersion(input);
        Assert.NotNull(version);
        Assert.Equal(expected, AppUpdatePolicy.VersionText(version!));
    }

    [Theory]
    [InlineData("update-manifest")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseVersion_RejectsNonVersions(string? input) => Assert.Null(AppUpdatePolicy.TryParseVersion(input));

    [Theory]
    [InlineData("v2.1.15-douyin-test", "2.1.14", true)]
    [InlineData("v2.1.14-douyin-test", "2.1.14", false)]
    [InlineData("v2.1.9", "2.1.14", false)]
    [InlineData("v2.2.0", "2.1.99", true)]
    [InlineData("v2.1.10", "2.1.9", true)]
    public void IsNewer_ComparesNumerically(string candidate, string current, bool expected) =>
        Assert.Equal(expected, AppUpdatePolicy.IsNewer(candidate, current));

    [Fact]
    public void ExtractSha256_FindsHashInReleaseNotes()
    {
        Assert.Equal(Sha, AppUpdatePolicy.ExtractSha256($"zip\nSHA256: {Sha.ToUpperInvariant()}\n"));
        Assert.Null(AppUpdatePolicy.ExtractSha256("no hash here"));
    }

    [Fact]
    public void BuildDownloadCandidates_MirrorFirstThenDirect()
    {
        var url = "https://github.com/a/b/releases/download/v1/x.zip";
        Assert.Equal(new[] { "https://ghfast.top/" + url, url }, AppUpdatePolicy.BuildDownloadCandidates(url));
        Assert.Equal(new[] { "https://ghfast.top/" + url, url }, AppUpdatePolicy.BuildDownloadCandidates("https://ghfast.top/" + url));
        Assert.Equal(new[] { "https://example.com/x.zip" }, AppUpdatePolicy.BuildDownloadCandidates("https://example.com/x.zip"));
    }

    [Fact]
    public void SelectLatestFromReleasesJson_PicksHighestVersionWithZipAndSha()
    {
        var json = $$"""
        [
          {"tag_name":"update-manifest","draft":false,"body":"","assets":[{"name":"latest.json","browser_download_url":"u"}]},
          {"tag_name":"v2.1.9-douyin-test","draft":false,"body":"SHA256: {{Sha}}","assets":[{"name":"QuickReplyTool-Portable-2.1.9-douyin-test.zip","browser_download_url":"https://github.com/a/b/9.zip","size":5}]},
          {"tag_name":"v2.1.15-douyin-test","draft":false,"prerelease":true,"body":"sha256 `{{Sha}}`","assets":[{"name":"QuickReplyTool-Portable-2.1.15-douyin-test.zip","browser_download_url":"https://github.com/a/b/15.zip","size":79856007}]},
          {"tag_name":"v2.1.16","draft":true,"body":"SHA256: {{Sha}}","assets":[{"name":"QuickReplyTool-Portable-2.1.16.zip","browser_download_url":"d","size":1}]},
          {"tag_name":"v2.1.17","draft":false,"body":"no sha","assets":[{"name":"QuickReplyTool-Portable-2.1.17.zip","browser_download_url":"d","size":1}]}
        ]
        """;
        var info = AppUpdatePolicy.SelectLatestFromReleasesJson(json);
        Assert.NotNull(info);
        Assert.Equal("2.1.15", info!.Version);
        Assert.Equal("https://github.com/a/b/15.zip", info.ZipUrl);
        Assert.Equal(79856007, info.SizeBytes);
        Assert.Equal(Sha, info.Sha256);
    }

    [Fact]
    public void ParseManifest_ReadsCiManifest()
    {
        var json = $$"""{"version":"2.1.15","tag":"v2.1.15-douyin-test","zipName":"Q.zip","url":"https://github.com/a/b/releases/download/v/Q.zip","sha256":"{{Sha.ToUpperInvariant()}}","size":123}""";
        var info = AppUpdatePolicy.ParseManifest(json);
        Assert.NotNull(info);
        Assert.Equal("2.1.15", info!.Version);
        Assert.Equal(Sha, info.Sha256);
        Assert.Equal(123, info.SizeBytes);
        Assert.Null(AppUpdatePolicy.ParseManifest("""{"version":"2.1.15","url":"https://x/y.zip","sha256":"bad"}"""));
    }

    [Fact]
    public void ResolveInstallRoot_RequiresAppFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "QuickReplyTool");
        Assert.Equal(root, AppUpdatePolicy.ResolveInstallRoot(Path.Combine(root, "app") + Path.DirectorySeparatorChar));
        Assert.Null(AppUpdatePolicy.ResolveInstallRoot(Path.Combine(root, "bin", "Release")));
    }

    [Fact]
    public async Task DownloadAndStage_RejectsShaMismatchAndKeepsNothing()
    {
        var work = Path.Combine(Path.GetTempPath(), "qr-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new StaticHandler(new byte[] { 1, 2, 3 });
            var service = new AppUpdateService(() => new HttpClient(handler), work);
            var update = new AppUpdateInfo("9.9.9", "v9.9.9", "Q.zip", "https://example.com/Q.zip", Sha, 3, "");
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAndStageAsync(update, null));
            Assert.Empty(Directory.GetFiles(work));
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    [Fact]
    public async Task DownloadAndStage_ExtractsVerifiedZip()
    {
        var work = Path.Combine(Path.GetTempPath(), "qr-update-test-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        try
        {
            Directory.CreateDirectory(Path.Combine(src, "app"));
            File.WriteAllText(Path.Combine(src, "app", "QuickReplyTool.exe"), "x");
            var zipPath = Path.Combine(work, "pkg.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(src, zipPath);
            var bytes = File.ReadAllBytes(zipPath);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var updateRoot = Path.Combine(work, "update");
            var service = new AppUpdateService(() => new HttpClient(new StaticHandler(bytes)), updateRoot);
            var staging = await service.DownloadAndStageAsync(new AppUpdateInfo("9.9.9", "v", "Q.zip", "https://example.com/Q.zip", sha, bytes.Length, ""), null);
            Assert.True(File.Exists(Path.Combine(staging, "app", "QuickReplyTool.exe")));
            Assert.False(File.Exists(Path.Combine(updateRoot, "Q.zip")));
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    [Fact]
    public async Task Check_FallsBackToManifestWhenApiFails()
    {
        var manifest = $$"""{"version":"99.0.0","url":"https://github.com/a/b/releases/download/v/Q.zip","sha256":"{{Sha}}"}""";
        var handler = new RoutingHandler(uri => uri.Host == "ghfast.top" ? manifest : null);
        var service = new AppUpdateService(() => new HttpClient(handler), Path.GetTempPath());
        var result = await service.CheckAsync("2.1.15");
        Assert.Equal(AppUpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("99.0.0", result.Update!.Version);
    }

    [Fact]
    public async Task Check_ReportsFailureWhenEverythingBlocked()
    {
        var service = new AppUpdateService(() => new HttpClient(new RoutingHandler(_ => null)), Path.GetTempPath());
        var result = await service.CheckAsync("2.1.15");
        Assert.Equal(AppUpdateCheckStatus.Failed, result.Status);
    }

    [Fact]
    public void UpdaterScript_KeepsBackupAndRollsBack()
    {
        Assert.Contains("app.bak", AppUpdateService.UpdaterScript);
        Assert.Contains("rolling back", AppUpdateService.UpdaterScript);
        Assert.Contains("Start-Process -FilePath $exe", AppUpdateService.UpdaterScript);
    }

    private sealed class StaticHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
    }

    private sealed class RoutingHandler(Func<Uri, string?> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = route(request.RequestUri!);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
