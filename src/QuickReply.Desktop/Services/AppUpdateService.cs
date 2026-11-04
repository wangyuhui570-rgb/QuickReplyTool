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
    string Notes,
    AppUpdatePatch? Patch = null);

/// <summary>
/// Changed files only, built by CI against the release before. Usable only when the installed
/// version equals <see cref="FromVersion"/>; anything else falls back to the full zip.
/// </summary>
internal sealed record AppUpdatePatch(
    string FromVersion,
    string ZipName,
    string Url,
    string Sha256,
    long SizeBytes);

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
    public const string PatchPrefix = "QuickReplyTool-Patch-";
    public const string PatchManifestFileName = "patch-manifest.json";

    public static string ReleasesApiUrl => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=15";

    public static string ManifestDirectUrl =>
        $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{ManifestTag}/{ManifestFileName}";

    private static readonly Regex VersionRegex = new(@"^\s*[vV]?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?", RegexOptions.Compiled);
    // The lookbehind keeps "PatchSHA256:" from being read as the full zip's digest.
    private static readonly Regex ShaRegex = new(@"(?i)(?<!patch-?)sha-?256[^0-9a-f]{0,40}([0-9a-f]{64})", RegexOptions.Compiled);
    private static readonly Regex PatchShaRegex = new(@"(?i)patch-?sha-?256[^0-9a-f]{0,40}([0-9a-f]{64})", RegexOptions.Compiled);
    private static readonly Regex PatchNameRegex = new(@"^QuickReplyTool-Patch-(?<from>[0-9.]+)-to-(?<to>[0-9.]+)\.zip$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

    public static string? ExtractPatchSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = PatchShaRegex.Match(text);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
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
            AppUpdatePatch? patch = null;
            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = GetString(asset, "name");
                    var assetSize = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var s) ? s : 0;
                    if (zipName is null
                        && name.StartsWith(ZipPrefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipName = name;
                        zipUrl = GetString(asset, "browser_download_url");
                        size = assetSize;
                    }
                    else if (patch is null
                             && PatchNameRegex.Match(name) is { Success: true } patchName
                             && ExtractPatchSha256(body) is { } patchSha)
                    {
                        patch = new AppUpdatePatch(
                            patchName.Groups["from"].Value,
                            name,
                            GetString(asset, "browser_download_url"),
                            patchSha,
                            assetSize);
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
                best = new AppUpdateInfo(VersionText(version), tag, zipName, zipUrl, sha, size, body, patch);
            }
        }

        return best;
    }

    /// <summary>
    /// ECDSA P-256 public key (SubjectPublicKeyInfo, base64). The private half is the
    /// UPDATE_SIGNING_KEY secret used by .github/workflows/release.yml to sign latest.json.
    /// </summary>
    public const string ManifestSigningPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXkfKo1yuId8Wa/O7WyTQs2RI8luzyHSmBjVNMnhXOsQPvIYzf+F3RXExlcDg4soA2gZXJGiezvn5AO510t3BDg==";

    public static string ReleaseDownloadPrefix => $"https://github.com/{RepoOwner}/{RepoName}/releases/download/";

    /// <summary>The exact text CI signs: version, zipName, url and sha256, one per line.</summary>
    public static string BuildManifestSigningPayload(string version, string zipName, string url, string sha256) =>
        string.Join("\n", version.Trim(), zipName.Trim(), url.Trim(), sha256.Trim().ToLowerInvariant());

    /// <summary>
    /// A manifest fetched through the third-party mirror is trusted only with a valid signature and
    /// a zip URL inside this repository's releases. Otherwise the mirror could name any zip and
    /// supply a matching SHA256, and the updater would install it.
    /// </summary>
    public static AppUpdateInfo? ParseSignedManifest(string json) =>
        ParseSignedManifest(json, ManifestSigningPublicKey);

    internal static AppUpdateInfo? ParseSignedManifest(string json, string publicKeyBase64)
    {
        var info = ParseManifest(json);
        if (info is null || !info.ZipUrl.StartsWith(ReleaseDownloadPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var signatureText = GetString(root, "signature").Trim();
        if (signatureText.Length == 0)
        {
            return null;
        }

        try
        {
            var payload = BuildManifestSigningPayload(
                GetString(root, "version"),
                GetString(root, "zipName"),
                GetString(root, "url"),
                GetString(root, "sha256"));
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            if (!ecdsa.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(signatureText), HashAlgorithmName.SHA256))
            {
                return null;
            }

            // The patch carries its own signature. Without a valid one only the full zip is used.
            return info.Patch is { } patch && VerifyPatchSignature(ecdsa, root, patch)
                ? info
                : info with { Patch = null };
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The text CI signs for a patch: from version, zipName, url and sha256, one per line.</summary>
    public static string BuildPatchSigningPayload(string fromVersion, string zipName, string url, string sha256) =>
        string.Join("\n", "patch", fromVersion.Trim(), zipName.Trim(), url.Trim(), sha256.Trim().ToLowerInvariant());

    private static bool VerifyPatchSignature(ECDsa ecdsa, JsonElement root, AppUpdatePatch patch)
    {
        if (!patch.Url.StartsWith(ReleaseDownloadPrefix, StringComparison.Ordinal) ||
            !root.TryGetProperty("patch", out var element))
        {
            return false;
        }

        var signature = GetString(element, "signature").Trim();
        if (signature.Length == 0)
        {
            return false;
        }

        var payload = BuildPatchSigningPayload(
            GetString(element, "from"),
            GetString(element, "zipName"),
            GetString(element, "url"),
            GetString(element, "sha256"));
        return ecdsa.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
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
        return new AppUpdateInfo(VersionText(version), GetString(root, "tag"), zipName, url, sha, size, GetString(root, "notes"), ParsePatch(root));
    }

    private static AppUpdatePatch? ParsePatch(JsonElement root)
    {
        if (!root.TryGetProperty("patch", out var patch) || patch.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var from = TryParseVersion(GetString(patch, "from"));
        var sha = GetString(patch, "sha256").Trim().ToLowerInvariant();
        var url = GetString(patch, "url");
        var zipName = GetString(patch, "zipName");
        if (from is null || !Regex.IsMatch(sha, "^[0-9a-f]{64}$") || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(zipName))
        {
            return null;
        }

        var size = patch.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var s) ? s : 0;
        return new AppUpdatePatch(VersionText(from), zipName, url, sha, size);
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

/// <summary>Checks GitHub (api → direct manifest → signed mirror manifest), downloads, verifies and hands off to an updater script.</summary>
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
            // Direct GitHub first: TLS to github.com vouches for the manifest. The mirror is only
            // used for a signed manifest, since whoever runs it could rewrite url and sha256 together.
            info = await TryFetchAsync(AppUpdatePolicy.ManifestDirectUrl, TimeSpan.FromSeconds(8), AppUpdatePolicy.ParseManifest, errors, cancellationToken);
            info ??= await TryFetchAsync(
                AppUpdatePolicy.MirrorPrefix + AppUpdatePolicy.ManifestDirectUrl,
                TimeSpan.FromSeconds(8),
                AppUpdatePolicy.ParseSignedManifest,
                errors,
                cancellationToken);
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

    /// <summary>
    /// Stages the update and returns the staging root. With <paramref name="installRoot"/> and
    /// <paramref name="currentVersion"/> it first tries the small patch built against the installed
    /// version; any problem there falls back to downloading the full zip.
    /// </summary>
    public async Task<string> DownloadAndStageAsync(
        AppUpdateInfo update,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default,
        string? installRoot = null,
        string? currentVersion = null)
    {
        Directory.CreateDirectory(_workRoot);
        CleanupOldStaging();

        if (update.Patch is { } patch &&
            installRoot is not null &&
            currentVersion is not null &&
            AppUpdatePolicy.TryParseVersion(currentVersion) is { } installed &&
            AppUpdatePolicy.TryParseVersion(patch.FromVersion) is { } patchFrom &&
            installed == patchFrom)
        {
            try
            {
                var stagedFromPatch = await StageFromPatchAsync(update, patch, installRoot, progress, cancellationToken);
                AppUpdateLog.Write($"staged from patch {patch.ZipName}");
                return stagedFromPatch;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                AppUpdateLog.Write($"patch failed, using full zip: {ex.GetType().Name}: {ex.Message}");
                CleanupOldStaging();
            }
        }

        var zipPath = Path.Combine(_workRoot, update.ZipName);
        AppUpdateLog.Write($"download start version={update.Version} zip={update.ZipName} url={update.ZipUrl} expectedSha={update.Sha256}");
        await DownloadVerifiedAsync(update.ZipUrl, zipPath, update.Sha256, update.SizeBytes, $"正在下载 {update.Version}", progress, cancellationToken);

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

    /// <summary>Downloads (mirror first) until one copy matches <paramref name="expectedSha"/>; throws when none does.</summary>
    private async Task DownloadVerifiedAsync(
        string sourceUrl,
        string targetPath,
        string expectedSha,
        long expectedSize,
        string progressLabel,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(targetPath) &&
            string.Equals(await ComputeSha256Async(targetPath, cancellationToken), expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var errors = new List<string>();
        foreach (var url in AppUpdatePolicy.BuildDownloadCandidates(sourceUrl))
        {
            try
            {
                progress?.Report($"{progressLabel}（{SafeHost(url)}）…");
                AppUpdateLog.Write("GET " + url);
                await DownloadFileAsync(url, targetPath + ".part", expectedSize, progress, cancellationToken);
                var sha = await ComputeSha256Async(targetPath + ".part", cancellationToken);
                AppUpdateLog.Write($"sha256 {SafeHost(url)} {sha}");
                if (!string.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{SafeHost(url)} 校验不一致");
                    AppUpdateLog.Write($"sha mismatch {SafeHost(url)} expected={expectedSha} actual={sha}");
                    TryDeleteFile(targetPath + ".part");
                    continue;
                }

                File.Move(targetPath + ".part", targetPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{SafeHost(url)} {ex.Message}");
                AppUpdateLog.Write($"download failed {SafeHost(url)} {ex.GetType().Name}: {ex.Message}");
                TryDeleteFile(targetPath + ".part");
            }
        }

        var failure = "下载失败：" + string.Join("；", errors);
        AppUpdateLog.Write(failure);
        throw new InvalidOperationException(failure);
    }

    /// <summary>
    /// Copies the installed app into staging, lays the patch's changed files over it, removes files
    /// the new version no longer has, and checks every file against the hashes in the patch
    /// manifest. The result is byte-for-byte what the full zip would have extracted.
    /// </summary>
    private async Task<string> StageFromPatchAsync(
        AppUpdateInfo update,
        AppUpdatePatch patch,
        string installRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var patchPath = Path.Combine(_workRoot, patch.ZipName);
        AppUpdateLog.Write($"patch download from={patch.FromVersion} zip={patch.ZipName} url={patch.Url} expectedSha={patch.Sha256}");
        await DownloadVerifiedAsync(patch.Url, patchPath, patch.Sha256, patch.SizeBytes, $"正在下载 {update.Version} 增量补丁", progress, cancellationToken);

        progress?.Report("补丁下载完成，正在应用…");
        var stagingRoot = Path.Combine(_workRoot, "staging-" + update.Version);
        await Task.Run(() => AppUpdatePatcher.Apply(installRoot, patchPath, stagingRoot, patch.FromVersion, update.Version), cancellationToken);
        TryDeleteFile(patchPath);
        return stagingRoot;
    }

    /// <summary>Writes the updater script and starts it hidden. Caller must exit the app right after.</summary>
    public void LaunchUpdater(string stagingRoot, string installRoot, int processId)
    {
        AppUpdateLog.Write($"launch updater pid={processId} install={installRoot} staging={stagingRoot}");
        var scriptPath = Path.Combine(_workRoot, "apply-update.ps1");
        File.WriteAllText(scriptPath, UpdaterScript, new UTF8Encoding(true));
        var logPath = Path.Combine(_workRoot, "update.log");
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            // Without this the script inherits the app's working directory, which is app\ itself.
            // A process whose current directory is app\ keeps it from being renamed, so every
            // update failed "in use" and rolled back, with nothing else running.
            WorkingDirectory = _workRoot,
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

        var started = Process.Start(startInfo);
        AppUpdateLog.Write(started is null
            ? "powershell did not start"
            : $"powershell pid={started.Id} script={scriptPath}");
    }

    private async Task DownloadFileAsync(string url, string destination, long expectedSize, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory();
        // Infinite covers a slow but steady body. Headers have their own short deadline so a
        // stuck mirror cannot sit for 15 minutes with no file and no error.
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerCts.CancelAfter(TimeSpan.FromSeconds(25));
        HttpResponseMessage responseMessage;
        try
        {
            responseMessage = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headerCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("连接超时（25 秒内没有响应）");
        }

        using var response = responseMessage;
        response.EnsureSuccessStatusCode();
        // The request token stays tied to the response stream. Leave the 25s timer armed and it
        // aborts a normal 80MB download with SocketException 995.
        headerCts.CancelAfter(Timeout.InfiniteTimeSpan);
        var total = response.Content.Headers.ContentLength ?? expectedSize;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(destination);
        AppUpdateLog.Write($"saving {destination} total={total}");
        var buffer = new byte[81920];
        long readTotal = 0;
        var lastReport = DateTime.MinValue;
        // One stall timer for the whole body. A new CancellationTokenSource per chunk aborts the
        // socket when it is disposed (SocketException 995) and those faults were never shown.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            stall.CancelAfter(TimeSpan.FromSeconds(45));
            int read;
            try
            {
                read = await source.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("下载停滞超过 45 秒，已放弃这个地址");
            }

            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            readTotal += read;
            if (total > 0 && DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(1))
            {
                lastReport = DateTime.UtcNow;
                progress?.Report($"正在下载… {readTotal * 100 / total}%（{readTotal / 1024 / 1024}/{Math.Max(total, 1) / 1024 / 1024} MB）");
            }
        }

        if (!stall.IsCancellationRequested)
        {
            stall.CancelAfter(Timeout.InfiniteTimeSpan);
        }
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
# Never sit inside the folder being replaced, whatever directory this was started from.
try { Set-Location -LiteralPath ([System.IO.Path]::GetTempPath()) } catch {}
function Log([string]$m) { try { Add-Content -LiteralPath $LogPath -Value ("{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $m) -Encoding UTF8 } catch {} }
function Retry([scriptblock]$Action, [string]$What, [int]$Attempts = 20) {
    for ($i = 1; $i -le $Attempts; $i++) {
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
# Anything still running from the app folder blocks renaming it: other instances, and the
# screenshot tool (pythonw.exe running app\ScreenshotTool\lite_shot.py). Match by exe path or
# command line, since pythonw itself lives outside the app folder.
function Stop-AppFolderProcesses {
    try {
        Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
            $_.ProcessId -ne $PID -and (
                ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase)) -or
                ($_.CommandLine -and $_.CommandLine.IndexOf($appDir, [StringComparison]::OrdinalIgnoreCase) -ge 0))
        } | ForEach-Object { Log "killing leftover $($_.ProcessId) $($_.Name)"; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    } catch {
        Log "process scan failed: $($_.Exception.Message)"
        Get-Process -Name 'QuickReplyTool' -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    }
}
Stop-AppFolderProcesses
Start-Sleep -Milliseconds 500

$replaced = $false
$mode = 'rename'
try {
    if (-not (Test-Path -LiteralPath (Join-Path $newApp 'QuickReplyTool.exe'))) { throw 'staging app missing' }
    if (Test-Path -LiteralPath $bakDir) { Retry { Remove-Item -LiteralPath $bakDir -Recurse -Force } 'remove old app.bak' }
    if (Test-Path -LiteralPath $appDir) {
        try {
            Retry { Stop-AppFolderProcesses; Rename-Item -LiteralPath $appDir -NewName 'app.bak' } 'backup app' 8
        } catch {
            # The folder itself can stay held open (a working directory, an Explorer window,
            # antivirus) while its files are free. Back up by copying and overwrite in place.
            Log 'rename blocked, falling back to in-place copy'
            $mode = 'copy'
            Retry { Copy-Item -LiteralPath $appDir -Destination $bakDir -Recurse -Force } 'copy backup'
        }
    }
    if ($mode -eq 'rename') {
        Retry { Copy-Item -LiteralPath $newApp -Destination $appDir -Recurse -Force } 'copy new app'
    } else {
        Retry { Copy-Item -Path (Join-Path $newApp '*') -Destination $appDir -Recurse -Force } 'overwrite app'
    }
    if (-not (Test-Path -LiteralPath $exe)) { throw 'new exe missing after copy' }
    $replaced = $true
    # Refresh helper files next to the app folder (no user data lives there).
    Get-ChildItem -LiteralPath $StagingRoot -File | ForEach-Object { try { Copy-Item -LiteralPath $_.FullName -Destination $InstallRoot -Force } catch { Log "copy $($_.Name) failed" } }
    Log 'replace ok'
} catch {
    Log "replace failed: $($_.Exception.Message); rolling back"
    try {
        if (Test-Path -LiteralPath $bakDir) {
            if ($mode -eq 'copy') {
                Copy-Item -Path (Join-Path $bakDir '*') -Destination $appDir -Recurse -Force
            } else {
                if (Test-Path -LiteralPath $appDir) { Remove-Item -LiteralPath $appDir -Recurse -Force -ErrorAction SilentlyContinue }
                Rename-Item -LiteralPath $bakDir -NewName 'app'
            }
            Log "rollback ok ($mode)"
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

/// <summary>Durable update trace. The Yes path used to fail with no file under logs\.</summary>
internal static class AppUpdateLog
{
    private static readonly object Sync = new();

    internal static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuickReplyTool",
        "logs",
        "update.log");

    internal static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                var directory = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(
                    LogFilePath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
