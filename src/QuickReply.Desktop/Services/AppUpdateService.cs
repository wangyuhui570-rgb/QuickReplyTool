using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuickReply.Desktop.Services;

/// <summary>Release information the updater needs.</summary>
internal sealed record AppUpdateInfo(
    string Version,
    string Tag,
    string ZipName,
    string ZipUrl,
    string Sha256,
    long SizeBytes,
    string Notes);

internal enum AppUpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed
}

internal sealed record AppUpdateCheckResult(AppUpdateCheckStatus Status, AppUpdateInfo? Update, string Message);

/// <summary>
/// Pure helpers (version compare, release parsing, path resolution) kept static for unit tests.
/// </summary>
internal static class AppUpdatePolicy
{
    public const string RepoOwner = "wangyuhui570-rgb";
    public const string RepoName = "QuickReplyTool";
    public const string MirrorPrefix = "https://ghfast.top/";
    public const string ManifestTag = "update-manifest";
    public const string ManifestFileName = "latest.json";
    public const string ZipPrefix = "QuickReplyTool-Portable-";

    public static string ReleasesApiUrl => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=15";

    public static string ManifestDirectUrl =>
        $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{ManifestTag}/{ManifestFileName}";

    private static readonly Regex VersionRegex = new(@"^\s*[vV]?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?", RegexOptions.Compiled);
    private static readonly Regex ShaRegex = new(@"(?i)sha-?256[^0-9a-f]{0,40}([0-9a-f]{64})", RegexOptions.Compiled);

    /// <summary>Parses "v2.1.15-douyin-test" / "2.1.15" into a comparable version (suffix ignored).</summary>
    public static Version? TryParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = VersionRegex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        int Part(int index) => match.Groups[index].Success ? int.Parse(match.Groups[index].Value) : 0;
        return new Version(Part(1), Part(2), Part(3), Part(4));
    }

    public static bool IsNewer(string? candidate, string? current)
    {
        var candidateVersion = TryParseVersion(candidate);
        var currentVersion = TryParseVersion(current);
        if (candidateVersion is null)
        {
            return false;
        }

        return currentVersion is null || candidateVersion > currentVersion;
    }

    public static string? ExtractSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = ShaRegex.Match(text);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>Mirror first, then direct GitHub.</summary>
    public static IReadOnlyList<string> BuildDownloadCandidates(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return [];
        }

        if (url.StartsWith(MirrorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return [url, url[MirrorPrefix.Length..]];
        }

        if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
        {
            return [MirrorPrefix + url, url];
        }

        return [url];
    }

    /// <summary>Picks the newest release (prereleases included) from the GitHub releases API JSON.</summary>
    public static AppUpdateInfo? SelectLatestFromReleasesJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        AppUpdateInfo? best = null;
        Version? bestVersion = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (GetBool(release, "draft"))
            {
                continue;
            }

            var tag = GetString(release, "tag_name");
            var version = TryParseVersion(tag);
            if (version is null)
            {
                continue;
            }

            var body = GetString(release, "body");
            string? zipName = null;
            string? zipUrl = null;
            long size = 0;
            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = GetString(asset, "name");
                    if (name.StartsWith(ZipPrefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipName = name;
                        zipUrl = GetString(asset, "browser_download_url");
                        size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var s) ? s : 0;
                        break;
                    }
                }
            }

            var sha = ExtractSha256(body);
            if (zipName is null || string.IsNullOrWhiteSpace(zipUrl) || sha is null)
            {
                continue;
            }

            if (bestVersion is null || version > bestVersion)
            {
                bestVersion = version;
                best = new AppUpdateInfo(VersionText(version), tag, zipName, zipUrl, sha, size, body);
            }
        }

        return best;
    }

    /// <summary>Parses the latest.json manifest published by CI.</summary>
    public static AppUpdateInfo? ParseManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var version = TryParseVersion(GetString(root, "version"));
        var sha = GetString(root, "sha256").Trim().ToLowerInvariant();
        var url = GetString(root, "url");
        if (version is null || sha.Length != 64 || !Regex.IsMatch(sha, "^[0-9a-f]{64}$") || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var zipName = GetString(root, "zipName");
        if (string.IsNullOrWhiteSpace(zipName))
        {
            zipName = Path.GetFileName(new Uri(url).AbsolutePath);
        }

        var size = root.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var s) ? s : 0;
        return new AppUpdateInfo(VersionText(version), GetString(root, "tag"), zipName, url, sha, size, GetString(root, "notes"));
    }

    /// <summary>The running exe must live in a folder named "app"; its parent is the install root.</summary>
    public static string? ResolveInstallRoot(string appDirectory)
    {
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            return null;
        }

        var trimmed = appDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(trimmed), "app", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetDirectoryName(trimmed);
    }

    public static string VersionText(Version version) =>
        version.Revision > 0 ? version.ToString(4) : version.ToString(3);

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>Checks GitHub (api → mirror manifest → direct manifest), downloads, verifies and hands off to an updater script.</summary>
internal sealed class AppUpdateService
{
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly string _workRoot;

    public AppUpdateService()
        : this(CreateDefaultClient, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickReplyTool", "update"))
    {
    }

    internal AppUpdateService(Func<HttpClient> httpClientFactory, string workRoot)
    {
        _httpClientFactory = httpClientFactory;
        _workRoot = workRoot;
    }

    public string WorkRoot => _workRoot;

    public async Task<AppUpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        AppUpdateInfo? info = null;

        info = await TryFetchAsync(AppUpdatePolicy.ReleasesApiUrl, TimeSpan.FromSeconds(6), AppUpdatePolicy.SelectLatestFromReleasesJson, errors, cancellationToken);
        if (info is null)
        {
            foreach (var url in AppUpdatePolicy.BuildDownloadCandidates(AppUpdatePolicy.ManifestDirectUrl))
            {
                info = await TryFetchAsync(url, TimeSpan.FromSeconds(8), AppUpdatePolicy.ParseManifest, errors, cancellationToken);
                if (info is not null)
                {
                    break;
                }
            }
        }

        if (info is null)
        {
            return new AppUpdateCheckResult(AppUpdateCheckStatus.Failed, null, "无法连接更新服务器：" + string.Join("；", errors));
        }

        return AppUpdatePolicy.IsNewer(info.Version, currentVersion)
            ? new AppUpdateCheckResult(AppUpdateCheckStatus.UpdateAvailable, info, $"发现新版本 {info.Version}")
            : new AppUpdateCheckResult(AppUpdateCheckStatus.UpToDate, info, $"已是最新版本（{currentVersion}）");
    }

    private async Task<AppUpdateInfo?> TryFetchAsync(
        string url,
        TimeSpan timeout,
        Func<string, AppUpdateInfo?> parser,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            using var client = _httpClientFactory();
            using var response = await client.GetAsync(url, cts.Token);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(cts.Token);
            var info = parser(text);
            if (info is null)
            {
                errors.Add($"{new Uri(url).Host} 无可用版本");
            }

            return info;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            errors.Add($"{SafeHost(url)} {(ex is OperationCanceledException ? "超时" : ex.Message)}");
            return null;
        }
    }

    /// <summary>Downloads (mirror first) and verifies the zip, then extracts it to a staging folder. Returns the staging root.</summary>
    public async Task<string> DownloadAndStageAsync(AppUpdateInfo update, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_workRoot);
        CleanupOldStaging();
        var zipPath = Path.Combine(_workRoot, update.ZipName);
        var errors = new List<string>();
        var downloaded = false;

        if (File.Exists(zipPath) && string.Equals(await ComputeSha256Async(zipPath, cancellationToken), update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            downloaded = true;
        }

        foreach (var url in downloaded ? Array.Empty<string>() : AppUpdatePolicy.BuildDownloadCandidates(update.ZipUrl))
        {
            try
            {
                progress?.Report($"正在下载 {update.Version}（{SafeHost(url)}）…");
                await DownloadFileAsync(url, zipPath + ".part", update.SizeBytes, progress, cancellationToken);
                var sha = await ComputeSha256Async(zipPath + ".part", cancellationToken);
                if (!string.Equals(sha, update.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{SafeHost(url)} 校验不一致");
                    TryDeleteFile(zipPath + ".part");
                    continue;
                }

                File.Move(zipPath + ".part", zipPath, overwrite: true);
                downloaded = true;
                break;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{SafeHost(url)} {ex.Message}");
                TryDeleteFile(zipPath + ".part");
            }
        }

        if (!downloaded)
        {
            throw new InvalidOperationException("下载失败：" + string.Join("；", errors));
        }

        progress?.Report("下载完成，正在解压…");
        var stagingRoot = Path.Combine(_workRoot, "staging-" + update.Version);
        if (Directory.Exists(stagingRoot))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }

        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, stagingRoot), cancellationToken);
        if (!File.Exists(Path.Combine(stagingRoot, "app", "QuickReplyTool.exe")))
        {
            throw new InvalidOperationException("更新包不完整：缺少 app\\QuickReplyTool.exe");
        }

        TryDeleteFile(zipPath);
        return stagingRoot;
    }

    /// <summary>Writes the updater script and starts it hidden. Caller must exit the app right after.</summary>
    public void LaunchUpdater(string stagingRoot, string installRoot, int processId)
    {
        var scriptPath = Path.Combine(_workRoot, "apply-update.ps1");
        File.WriteAllText(scriptPath, UpdaterScript, new UTF8Encoding(true));
        var logPath = Path.Combine(_workRoot, "update.log");
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var arg in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", scriptPath,
                     "-WaitPid", processId.ToString(), "-InstallRoot", installRoot, "-StagingRoot", stagingRoot, "-LogPath", logPath
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process.Start(startInfo);
    }

    private async Task DownloadFileAsync(string url, string destination, long expectedSize, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(15));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expectedSize;
        await using var source = await response.Content.ReadAsStreamAsync(cts.Token);
        await using var target = File.Create(destination);
        var buffer = new byte[81920];
        long readTotal = 0;
        var lastReport = DateTime.UtcNow;
        int read;
        while ((read = await ReadWithStallTimeoutAsync(source, buffer, cts.Token)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
            readTotal += read;
            if (total > 0 && DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(2))
            {
                lastReport = DateTime.UtcNow;
                progress?.Report($"正在下载… {readTotal * 100 / total}%");
            }
        }
    }

    private static async Task<int> ReadWithStallTimeoutAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(TimeSpan.FromSeconds(30));
        return await source.ReadAsync(buffer, stall.Token);
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void CleanupOldStaging()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(_workRoot, "staging-*"))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string SafeHost(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return url;
        }
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"QuickReplyTool/{AppIdentity.Version}");
        return client;
    }

    /// <summary>PowerShell 5 compatible. Replaces InstallRoot\app with StagingRoot\app, keeps app.bak for rollback, restarts.</summary>
    internal const string UpdaterScript = """
param(
    [int]$WaitPid,
    [string]$InstallRoot,
    [string]$StagingRoot,
    [string]$LogPath
)
$ErrorActionPreference = 'Stop'
function Log([string]$m) { try { Add-Content -LiteralPath $LogPath -Value ("{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $m) -Encoding UTF8 } catch {} }
function Retry([scriptblock]$Action, [string]$What) {
    for ($i = 1; $i -le 20; $i++) {
        try { & $Action; return } catch { Log "$What attempt $i failed: $($_.Exception.Message)"; Start-Sleep -Milliseconds 750 }
    }
    throw "$What failed"
}

$appDir = Join-Path $InstallRoot 'app'
$bakDir = Join-Path $InstallRoot 'app.bak'
$newApp = Join-Path $StagingRoot 'app'
$exe = Join-Path $appDir 'QuickReplyTool.exe'
Log "update start pid=$WaitPid root=$InstallRoot staging=$StagingRoot"

try {
    $p = Get-Process -Id $WaitPid -ErrorAction SilentlyContinue
    if ($p) { if (-not $p.WaitForExit(30000)) { Log 'app did not exit, killing'; Stop-Process -Id $WaitPid -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1 } }
} catch { Log "wait failed: $($_.Exception.Message)" }
# Any other instance started from this app folder must be gone too.
Get-Process -Name 'QuickReplyTool' -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and $_.Path.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase) } catch { $false } } | ForEach-Object { Log "killing leftover $($_.Id)"; Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

$replaced = $false
try {
    if (-not (Test-Path -LiteralPath (Join-Path $newApp 'QuickReplyTool.exe'))) { throw 'staging app missing' }
    if (Test-Path -LiteralPath $bakDir) { Retry { Remove-Item -LiteralPath $bakDir -Recurse -Force } 'remove old app.bak' }
    if (Test-Path -LiteralPath $appDir) { Retry { Rename-Item -LiteralPath $appDir -NewName 'app.bak' } 'backup app' }
    Retry { Copy-Item -LiteralPath $newApp -Destination $appDir -Recurse -Force } 'copy new app'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'new exe missing after copy' }
    $replaced = $true
    # Refresh helper files next to the app folder (no user data lives there).
    Get-ChildItem -LiteralPath $StagingRoot -File | ForEach-Object { try { Copy-Item -LiteralPath $_.FullName -Destination $InstallRoot -Force } catch { Log "copy $($_.Name) failed" } }
    Log 'replace ok'
} catch {
    Log "replace failed: $($_.Exception.Message); rolling back"
    try {
        if (Test-Path -LiteralPath $bakDir) {
            if (Test-Path -LiteralPath $appDir) { Remove-Item -LiteralPath $appDir -Recurse -Force -ErrorAction SilentlyContinue }
            Rename-Item -LiteralPath $bakDir -NewName 'app'
            Log 'rollback ok'
        }
    } catch { Log "rollback failed: $($_.Exception.Message)" }
}

try { Start-Process -FilePath $exe -WorkingDirectory $appDir; Log 'restarted' } catch { Log "restart failed: $($_.Exception.Message)" }

if ($replaced) {
    Start-Sleep -Seconds 3
    try { Remove-Item -LiteralPath $bakDir -Recurse -Force -ErrorAction Stop } catch { Log "remove app.bak failed: $($_.Exception.Message)" }
}
try { Remove-Item -LiteralPath $StagingRoot -Recurse -Force -ErrorAction Stop } catch { Log "remove staging failed: $($_.Exception.Message)" }
Log 'update done'
""";
}
