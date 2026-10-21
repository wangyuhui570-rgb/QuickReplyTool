using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuickReply.Desktop.Services;

public readonly record struct PackageUpdateLaunchResult(
    bool Success,
    bool LaunchedUpdater,
    string Message,
    string PackagePath,
    string LogPath);

public readonly record struct PackageUpdateStatus(
    bool Success,
    string VersionLabel,
    string SourceKind,
    string Message,
    DateTimeOffset CompletedAt,
    string LogPath);

internal sealed class PackageUpdatePlan
{
    public int ParentPid { get; init; }

    public string PackagePath { get; init; } = string.Empty;

    public string TargetDir { get; init; } = string.Empty;

    public string RestartExePath { get; init; } = string.Empty;

    public string RestartArgs { get; init; } = string.Empty;

    public string VersionLabel { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string MarkerPath { get; init; } = string.Empty;

    public string LogPath { get; init; } = string.Empty;
}

internal sealed class PackageUpdateMarker
{
    public bool Success { get; init; }

    public string VersionLabel { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset CompletedAt { get; init; }

    public string LogPath { get; init; } = string.Empty;
}

public sealed class SelfUpdateService : IPackageUpdateService
{
    private const string MarkerFileName = "last-apply.json";
    private const string AppExecutableName = "QuickReplyTool.exe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _updatesRootPath;
    private readonly string _downloadsPath;
    private readonly string _stagingPath;
    private readonly string _logsPath;
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly Func<ProcessStartInfo, Process?> _processStarter;
    private readonly Func<string?> _processPathProvider;
    private readonly Func<string[]> _commandLineArgsProvider;
    private readonly Func<bool> _isCurrentProcessElevatedProvider;

    public SelfUpdateService(SqliteAppDataStore dataStore)
        : this(
            dataStore,
            CreateDefaultHttpClient,
            static startInfo => Process.Start(startInfo),
            static () => Environment.ProcessPath,
            static () => Environment.GetCommandLineArgs(),
            static () => StartupRegistrationService.IsCurrentProcessElevated())
    {
    }

    internal SelfUpdateService(
        SqliteAppDataStore dataStore,
        Func<HttpClient> httpClientFactory,
        Func<ProcessStartInfo, Process?> processStarter,
        Func<string?> processPathProvider,
        Func<string[]> commandLineArgsProvider,
        Func<bool> isCurrentProcessElevatedProvider)
    {
        _updatesRootPath = Path.Combine(dataStore.DataRootPath, "updates");
        _downloadsPath = Path.Combine(_updatesRootPath, "downloads");
        _stagingPath = Path.Combine(_updatesRootPath, "staging");
        _logsPath = Path.Combine(_updatesRootPath, "logs");
        _httpClientFactory = httpClientFactory;
        _processStarter = processStarter;
        _processPathProvider = processPathProvider;
        _commandLineArgsProvider = commandLineArgsProvider;
        _isCurrentProcessElevatedProvider = isCurrentProcessElevatedProvider;
    }

    public bool CanSelfApplyPackage(string packageUrl, string? packageKind = null)
    {
        if (!Uri.TryCreate(packageUrl?.Trim(), UriKind.Absolute, out var packageUri))
        {
            return false;
        }

        return AppUpdatePackageResolver.IsZipOta(packageKind, packageUri.AbsoluteUri);
    }

    public async Task<PackageUpdateLaunchResult> DownloadAndLaunchAsync(
        string packageUrl,
        string versionLabel,
        string sourceKind,
        string? expectedSha256 = null,
        long? expectedSizeBytes = null,
        string? packageKind = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedUrl = packageUrl?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var packageUri))
        {
            return new PackageUpdateLaunchResult(false, false, "更新地址无效。", string.Empty, string.Empty);
        }

        if (!CanSelfApplyPackage(normalizedUrl, packageKind))
        {
            return new PackageUpdateLaunchResult(false, false, "当前仅支持自动安装 zip 格式的更新包。", string.Empty, string.Empty);
        }

        var processPath = _processPathProvider() ?? string.Empty;
        var targetDirectory = Path.GetDirectoryName(processPath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(targetDirectory))
        {
            return new PackageUpdateLaunchResult(false, false, "未找到当前程序路径，无法执行自动更新。", string.Empty, string.Empty);
        }

        var preparedPackagePath = string.Empty;
        var preparedLogPath = string.Empty;
        try
        {
            Directory.CreateDirectory(_downloadsPath);
            Directory.CreateDirectory(_stagingPath);
            Directory.CreateDirectory(_logsPath);
            CleanupOldArtifacts(_downloadsPath, "*.zip", keepCount: 6);
            CleanupOldArtifacts(_stagingPath, "*.json", keepCount: 8);
            CleanupOldArtifacts(_stagingPath, "*.ps1", keepCount: 8);
            CleanupOldArtifacts(_logsPath, "*.log", keepCount: 12);

            var now = DateTimeOffset.Now;
            var safeVersion = MakeSafeFileComponent(versionLabel, fallback: "package");
            var packageFileName = $"{now:yyyyMMdd-HHmmss}-{safeVersion}.zip";
            var packagePath = Path.Combine(_downloadsPath, packageFileName);
            var configPath = Path.Combine(_stagingPath, $"{now:yyyyMMdd-HHmmss}-{safeVersion}.json");
            var scriptPath = Path.Combine(_stagingPath, $"{now:yyyyMMdd-HHmmss}-{safeVersion}.ps1");
            var logPath = Path.Combine(_logsPath, $"{now:yyyyMMdd-HHmmss}-{safeVersion}.log");
            var markerPath = Path.Combine(_updatesRootPath, MarkerFileName);
            preparedPackagePath = packagePath;
            preparedLogPath = logPath;
            WritePreparationLog(logPath, $"开始准备自动更新：{versionLabel}");
            WritePreparationLog(logPath, $"更新来源：{normalizedUrl}");

            using (var httpClient = _httpClientFactory())
            using (var response = await httpClient.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                await using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var localStream = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await remoteStream.CopyToAsync(localStream, cancellationToken).ConfigureAwait(false);
            }

            var packageInfo = new FileInfo(packagePath);
            if (!packageInfo.Exists || packageInfo.Length <= 0)
            {
                WritePreparationLog(logPath, "更新包下载失败，文件为空。");
                return new PackageUpdateLaunchResult(false, false, "更新包下载失败，文件为空。", packagePath, logPath);
            }

            if (expectedSizeBytes is > 0 && packageInfo.Length != expectedSizeBytes.Value)
            {
                WritePreparationLog(logPath, $"更新包大小校验失败。期望 {expectedSizeBytes.Value} 字节，实际 {packageInfo.Length} 字节。");
                return new PackageUpdateLaunchResult(false, false, "更新包大小校验失败，请重新下载后再试。", packagePath, logPath);
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actualSha256 = await ComputeSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualSha256, NormalizeSha256(expectedSha256), StringComparison.OrdinalIgnoreCase))
                {
                    WritePreparationLog(logPath, $"更新包 SHA256 校验失败。期望 {NormalizeSha256(expectedSha256)}，实际 {actualSha256}。");
                    return new PackageUpdateLaunchResult(false, false, "更新包校验失败，请重新检查更新后再试。", packagePath, logPath);
                }
            }

            var validationError = ValidatePackageArchive(packagePath);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                WritePreparationLog(logPath, validationError);
                return new PackageUpdateLaunchResult(false, false, validationError, packagePath, logPath);
            }

            var plan = new PackageUpdatePlan
            {
                ParentPid = Environment.ProcessId,
                PackagePath = packagePath,
                TargetDir = targetDirectory,
                RestartExePath = processPath,
                RestartArgs = BuildRestartArguments(),
                VersionLabel = versionLabel?.Trim() ?? string.Empty,
                SourceKind = sourceKind?.Trim() ?? string.Empty,
                MarkerPath = markerPath,
                LogPath = logPath
            };

            var configJson = JsonSerializer.Serialize(plan, JsonOptions);
            await File.WriteAllTextAsync(configPath, configJson, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(scriptPath, UpdateScript, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File {QuoteArgument(scriptPath)} -ConfigPath {QuoteArgument(configPath)}",
                UseShellExecute = true,
                WorkingDirectory = targetDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (RequiresElevation(targetDirectory) && !_isCurrentProcessElevatedProvider())
            {
                startInfo.Verb = "runas";
            }

            _processStarter(startInfo);
            return new PackageUpdateLaunchResult(true, true, "更新包已下载，已启动自动安装。", packagePath, logPath);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            if (!string.IsNullOrWhiteSpace(preparedLogPath))
            {
                WritePreparationLog(preparedLogPath, "已取消管理员授权，自动更新未执行。");
            }

            return new PackageUpdateLaunchResult(false, false, "已取消管理员授权，自动更新未执行。", preparedPackagePath, preparedLogPath);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(preparedLogPath))
            {
                WritePreparationLog(preparedLogPath, $"自动更新准备失败：{ex.Message}");
            }

            return new PackageUpdateLaunchResult(false, false, $"自动更新准备失败：{ex.Message}", preparedPackagePath, preparedLogPath);
        }
    }

    public PackageUpdateStatus? ConsumeLastStatus()
    {
        var markerPath = Path.Combine(_updatesRootPath, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(markerPath, Encoding.UTF8);
            File.Delete(markerPath);
            var marker = JsonSerializer.Deserialize<PackageUpdateMarker>(json);
            if (marker is null)
            {
                return null;
            }

            return new PackageUpdateStatus(
                marker.Success,
                marker.VersionLabel,
                marker.SourceKind,
                marker.Message,
                marker.CompletedAt,
                marker.LogPath);
        }
        catch
        {
            return null;
        }
    }

    private static bool RequiresElevation(string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return false;
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 }.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (targetDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void CleanupOldArtifacts(string directoryPath, string pattern, int keepCount)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        var files = new DirectoryInfo(directoryPath)
            .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();

        foreach (var file in files.Skip(Math.Max(1, keepCount)))
        {
            try
            {
                file.Delete();
            }
            catch
            {
            }
        }
    }

    private string BuildRestartArguments()
    {
        var forwardedArgs = App.StripInternalLaunchArguments(_commandLineArgsProvider().Skip(1));
        if (forwardedArgs.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(" ", forwardedArgs.Select(QuoteArgument));
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        return new HttpClient(SharedHttpMessageHandler.Instance, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string ValidatePackageArchive(string packagePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            if (archive.Entries.Count == 0)
            {
                return "更新包内容为空，无法安装。";
            }

            var hasExecutable = archive.Entries.Any(entry =>
                !string.IsNullOrWhiteSpace(entry.FullName) &&
                !entry.FullName.EndsWith("/", StringComparison.Ordinal) &&
                string.Equals(Path.GetFileName(entry.FullName), AppExecutableName, StringComparison.OrdinalIgnoreCase));

            if (!hasExecutable)
            {
                return "更新包中未找到 QuickReplyTool.exe，无法安装。";
            }

            return string.Empty;
        }
        catch (InvalidDataException ex)
        {
            return $"更新包已损坏，无法解压：{ex.Message}";
        }
    }

    private static string NormalizeSha256(string value)
    {
        return new string(value
            .Where(Uri.IsHexDigit)
            .ToArray())
            .ToUpperInvariant();
    }

    private static void WritePreparationLog(string logPath, string message)
    {
        var logDirectory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
        File.AppendAllText(logPath, $"[{timestamp}] {message}{Environment.NewLine}", new UTF8Encoding(false));
    }

    private static string MakeSafeFileComponent(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return fallback;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            builder.Append(invalidChars.Contains(character) ? '-' : character);
        }

        var result = builder.ToString().Trim('.', ' ');
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private const string UpdateScript = """
param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Add-Content -Path $script:Plan.LogPath -Value "[$timestamp] $Message" -Encoding UTF8
}

function Save-Status {
    param(
        [bool]$Success,
        [string]$Message
    )

    $payload = @{
        Success = $Success
        VersionLabel = $script:Plan.VersionLabel
        SourceKind = $script:Plan.SourceKind
        Message = $Message
        CompletedAt = (Get-Date).ToString('O')
        LogPath = $script:Plan.LogPath
    }

    $json = $payload | ConvertTo-Json -Depth 4
    Set-Content -Path $script:Plan.MarkerPath -Value $json -Encoding UTF8
}

function Show-ErrorDialog {
    param([string]$Message)

    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show(
        $Message,
        '客服效率工具更新',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
}

$script:Plan = $null

try {
    $script:Plan = Get-Content -Raw -Path $ConfigPath | ConvertFrom-Json

    New-Item -ItemType Directory -Path (Split-Path -Parent $script:Plan.LogPath) -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $script:Plan.MarkerPath) -Force | Out-Null

    Write-Log "开始自动更新，目标版本：$($script:Plan.VersionLabel)"
    Write-Log "更新包：$($script:Plan.PackagePath)"
    Write-Log "安装目录：$($script:Plan.TargetDir)"

    if ($script:Plan.ParentPid -gt 0) {
        try {
            Wait-Process -Id $script:Plan.ParentPid -Timeout 120
            Write-Log "主程序已退出。"
        }
        catch {
            Write-Log "等待主程序退出超时，继续尝试更新。"
            Start-Sleep -Seconds 2
        }
    }

    $extractRoot = Join-Path ([System.IO.Path]::GetDirectoryName($script:Plan.PackagePath)) ("extract-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
    Expand-Archive -LiteralPath $script:Plan.PackagePath -DestinationPath $extractRoot -Force
    Write-Log "更新包已解压：$extractRoot"

    $mainExe = Get-ChildItem -Path $extractRoot -Recurse -Filter 'QuickReplyTool.exe' -File | Select-Object -First 1
    if ($null -eq $mainExe) {
        throw "更新包中未找到 QuickReplyTool.exe。"
    }

    $sourceRoot = Split-Path -Parent $mainExe.FullName
    Write-Log "识别到文件源目录：$sourceRoot"

    New-Item -ItemType Directory -Path $script:Plan.TargetDir -Force | Out-Null

    Get-ChildItem -Path $sourceRoot -Recurse -File | ForEach-Object {
        $relativePath = $_.FullName.Substring($sourceRoot.Length).TrimStart('\')
        if ([string]::IsNullOrWhiteSpace($relativePath)) {
            return
        }

        $destinationPath = Join-Path $script:Plan.TargetDir $relativePath
        $destinationDirectory = Split-Path -Parent $destinationPath
        if (-not (Test-Path $destinationDirectory)) {
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        }

        Copy-Item -LiteralPath $_.FullName -Destination $destinationPath -Force
    }

    Write-Log "文件替换完成。"
    Save-Status -Success $true -Message ("已完成自动更新：" + $script:Plan.VersionLabel)

    if (Test-Path $script:Plan.RestartExePath) {
        if ([string]::IsNullOrWhiteSpace($script:Plan.RestartArgs)) {
            Start-Process -FilePath $script:Plan.RestartExePath -WorkingDirectory (Split-Path -Parent $script:Plan.RestartExePath) | Out-Null
        }
        else {
            Start-Process -FilePath $script:Plan.RestartExePath -ArgumentList $script:Plan.RestartArgs -WorkingDirectory (Split-Path -Parent $script:Plan.RestartExePath) | Out-Null
        }

        Write-Log "已请求重启主程序。"
    }
}
catch {
    $errorMessage = $_.Exception.Message

    try {
        if ($null -ne $script:Plan -and -not [string]::IsNullOrWhiteSpace($script:Plan.LogPath)) {
            Write-Log "自动更新失败：$errorMessage"
        }
    }
    catch {
    }

    try {
        if ($null -ne $script:Plan) {
            Save-Status -Success $false -Message $errorMessage
        }
    }
    catch {
    }

    $dialogMessage = if ($null -ne $script:Plan -and -not [string]::IsNullOrWhiteSpace($script:Plan.LogPath)) {
        "自动更新失败：$errorMessage`r`n`r`n日志：$($script:Plan.LogPath)"
    }
    else {
        "自动更新失败：$errorMessage"
    }

    try {
        Show-ErrorDialog -Message $dialogMessage
    }
    catch {
    }

    exit 1
}
""";
}
