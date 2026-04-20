using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;

namespace QuickReply.Desktop.Tests;

public sealed class SelfUpdateServiceTests
{
    [Fact]
    public void CanSelfApplyPackage_OnlyAcceptsZipUrls()
    {
        using var scope = new TestAppDataScope();
        var service = new SelfUpdateService(
            scope.CreateDataStore(),
            static () => new HttpClient(),
            static _ => null,
            static () => "C:\\Apps\\QuickReplyTool.exe",
            static () => [],
            static () => false);

        Assert.True(service.CanSelfApplyPackage("https://updates.example.com/package.zip"));
        Assert.True(service.CanSelfApplyPackage("https://updates.example.com/download?id=123", AppUpdatePackageResolver.ZipOtaKind));
        Assert.False(service.CanSelfApplyPackage("https://updates.example.com/package.exe"));
        Assert.False(service.CanSelfApplyPackage("not-a-url"));
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_DownloadsStagesAndInvokesUpdater()
    {
        using var scope = new TestAppDataScope();
        var packageBytes = CreateUpdatePackageBytes();
        var downloadHandler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            HttpResponseFactory.Bytes(packageBytes, "application/zip")));

        ProcessStartInfo? capturedStartInfo = null;
        var service = new SelfUpdateService(
            scope.CreateDataStore(),
            () => new HttpClient(downloadHandler),
            startInfo =>
            {
                capturedStartInfo = startInfo;
                return null;
            },
            () => Path.Combine(scope.InstallRootPath, "QuickReplyTool.exe"),
            () => ["QuickReplyTool.exe", "--restart-after-pid", "1234", "--profile", "support"],
            static () => false);

        var result = await service.DownloadAndLaunchAsync(
            "https://updates.example.com/quickreply.zip",
            "2.2.0",
            "app_update",
            ComputeSha256(packageBytes),
            packageBytes.LongLength,
            AppUpdatePackageResolver.ZipOtaKind);

        var downloadsPath = Path.Combine(scope.AppRootPath, "updates", "downloads");
        var stagingPath = Path.Combine(scope.AppRootPath, "updates", "staging");

        Assert.True(result.Success);
        Assert.True(result.LaunchedUpdater);
        Assert.NotNull(capturedStartInfo);
        Assert.Equal("powershell.exe", capturedStartInfo!.FileName);
        Assert.Single(Directory.GetFiles(downloadsPath, "*.zip"));
        var configPath = Directory.GetFiles(stagingPath, "*.json").Single();
        Assert.Single(Directory.GetFiles(stagingPath, "*.ps1"));
        Assert.Contains("--profile", File.ReadAllText(configPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_UsesElevationForProgramFilesInstall()
    {
        using var scope = new TestAppDataScope();
        var packageBytes = CreateUpdatePackageBytes();
        var downloadHandler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            HttpResponseFactory.Bytes(packageBytes, "application/zip")));

        ProcessStartInfo? capturedStartInfo = null;
        var service = new SelfUpdateService(
            scope.CreateDataStore(),
            () => new HttpClient(downloadHandler),
            startInfo =>
            {
                capturedStartInfo = startInfo;
                return null;
            },
            static () => "C:\\Program Files\\QuickReplyTool\\QuickReplyTool.exe",
            static () => [],
            static () => false);

        var result = await service.DownloadAndLaunchAsync(
            "https://updates.example.com/quickreply.zip",
            "2.2.0",
            "app_update",
            packageKind: AppUpdatePackageResolver.ZipOtaKind);

        Assert.True(result.Success);
        Assert.Equal("runas", capturedStartInfo?.Verb);
    }

    [Fact]
    public void ConsumeLastStatus_ReadsAndDeletesMarker()
    {
        using var scope = new TestAppDataScope();
        var service = new SelfUpdateService(
            scope.CreateDataStore(),
            static () => new HttpClient(),
            static _ => null,
            static () => "C:\\Apps\\QuickReplyTool.exe",
            static () => [],
            static () => false);

        var updatesRoot = Path.Combine(scope.AppRootPath, "updates");
        Directory.CreateDirectory(updatesRoot);
        var markerPath = Path.Combine(updatesRoot, "last-apply.json");
        File.WriteAllText(
            markerPath,
            """
            {
              "Success": true,
              "VersionLabel": "2.2.0",
              "SourceKind": "app_update",
              "Message": "ok",
              "CompletedAt": "2026-04-14T08:30:00+08:00",
              "LogPath": "C:\\temp\\apply.log"
            }
            """);

        var status = service.ConsumeLastStatus();

        Assert.NotNull(status);
        Assert.True(status!.Value.Success);
        Assert.Equal("2.2.0", status.Value.VersionLabel);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_ReturnsFailureForNonZipPackage()
    {
        using var scope = new TestAppDataScope();
        var service = new SelfUpdateService(
            scope.CreateDataStore(),
            static () => new HttpClient(),
            static _ => null,
            static () => "C:\\Apps\\QuickReplyTool.exe",
            static () => [],
            static () => false);

        var result = await service.DownloadAndLaunchAsync(
            "https://updates.example.com/quickreply.exe",
            "2.2.0",
            "app_update");

        Assert.False(result.Success);
        Assert.False(result.LaunchedUpdater);
        Assert.Equal("当前仅支持自动安装 zip 格式的更新包。", result.Message);
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_ReturnsFailureWhenSha256DoesNotMatch()
    {
        using var scope = new TestAppDataScope();
        var packageBytes = CreateUpdatePackageBytes();
        var downloadHandler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            HttpResponseFactory.Bytes(packageBytes, "application/zip")));

        var service = new SelfUpdateService(
            scope.CreateDataStore(),
            () => new HttpClient(downloadHandler),
            static _ => null,
            () => Path.Combine(scope.InstallRootPath, "QuickReplyTool.exe"),
            static () => [],
            static () => false);

        var result = await service.DownloadAndLaunchAsync(
            "https://updates.example.com/quickreply.zip",
            "2.2.0",
            "app_update",
            expectedSha256: new string('A', 64),
            packageKind: AppUpdatePackageResolver.ZipOtaKind);

        Assert.False(result.Success);
        Assert.Contains("校验失败", result.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(result.LogPath));
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_ReturnsFailureWhenDownloadedPackageIsEmpty()
    {
        using var scope = new TestAppDataScope();
        var downloadHandler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            HttpResponseFactory.Bytes([])));

        var service = new SelfUpdateService(
            scope.CreateDataStore(),
            () => new HttpClient(downloadHandler),
            static _ => null,
            () => Path.Combine(scope.InstallRootPath, "QuickReplyTool.exe"),
            static () => [],
            static () => false);

        var result = await service.DownloadAndLaunchAsync(
            "https://updates.example.com/quickreply.zip",
            "2.2.0",
            "app_update");

        Assert.False(result.Success);
        Assert.False(result.LaunchedUpdater);
        Assert.Contains("文件为空", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_ReturnsFailureWhenPackageIsCorrupted()
    {
        using var scope = new TestAppDataScope();
        var downloadHandler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            HttpResponseFactory.Bytes(Encoding.UTF8.GetBytes("not-a-zip"), "application/zip")));

        var service = new SelfUpdateService(
            scope.CreateDataStore(),
            () => new HttpClient(downloadHandler),
            static _ => null,
            () => Path.Combine(scope.InstallRootPath, "QuickReplyTool.exe"),
            static () => [],
            static () => false);

        var result = await service.DownloadAndLaunchAsync(
            "https://updates.example.com/quickreply.zip",
            "2.2.0",
            "app_update",
            packageKind: AppUpdatePackageResolver.ZipOtaKind);

        Assert.False(result.Success);
        Assert.Contains("已损坏", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_ReturnsFailureWhenExecutableMissingFromPackage()
    {
        using var scope = new TestAppDataScope();
        var packageBytes = CreateUpdatePackageBytes(includeExecutable: false);
        var downloadHandler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            HttpResponseFactory.Bytes(packageBytes, "application/zip")));

        var service = new SelfUpdateService(
            scope.CreateDataStore(),
            () => new HttpClient(downloadHandler),
            static _ => null,
            () => Path.Combine(scope.InstallRootPath, "QuickReplyTool.exe"),
            static () => [],
            static () => false);

        var result = await service.DownloadAndLaunchAsync(
            "https://updates.example.com/quickreply.zip",
            "2.2.0",
            "app_update",
            packageKind: AppUpdatePackageResolver.ZipOtaKind);

        Assert.False(result.Success);
        Assert.Contains("未找到 QuickReplyTool.exe", result.Message, StringComparison.Ordinal);
    }

    private static byte[] CreateUpdatePackageBytes(bool includeExecutable = true)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (includeExecutable)
            {
                var exeEntry = archive.CreateEntry("app/QuickReplyTool.exe");
                using var exeStream = new StreamWriter(exeEntry.Open(), Encoding.UTF8, 1024, leaveOpen: false);
                exeStream.Write("stub");
            }

            var metadataEntry = archive.CreateEntry("app-update-info.json");
            using var metadataStream = new StreamWriter(metadataEntry.Open(), Encoding.UTF8, 1024, leaveOpen: false);
            metadataStream.Write("""{"packageKind":"zip_ota","appVersion":"2.2.0"}""");
        }

        return stream.ToArray();
    }

    private static string ComputeSha256(byte[] payload)
    {
        return Convert.ToHexString(SHA256.HashData(payload));
    }
}
