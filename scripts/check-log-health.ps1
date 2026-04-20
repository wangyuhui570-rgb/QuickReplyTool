param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$OutputRoot = '',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedInputPath = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $InputPath).Path)
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $outputRoot = Join-Path $repoRoot "artifacts\log-health\$timestamp"
}
else {
    $outputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$temporaryRoot = $null

function Resolve-LogRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-Path (Join-Path $Path 'conversation-logs.db')) {
        return $Path
    }

    if (Test-Path (Join-Path $Path 'logs\conversation-logs.db')) {
        return (Join-Path $Path 'logs')
    }

    $database = Get-ChildItem -Path $Path -Recurse -Filter conversation-logs.db -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($database) {
        return $database.Directory.FullName
    }

    throw "未在 $Path 下找到 conversation-logs.db"
}

try {
    $logRoot = $resolvedInputPath
    if ([System.IO.Path]::GetExtension($resolvedInputPath) -ieq '.zip') {
        $temporaryRoot = Join-Path $env:TEMP ("quickreply-log-health-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        Expand-Archive -LiteralPath $resolvedInputPath -DestinationPath $temporaryRoot -Force
        $logRoot = Resolve-LogRoot -Path $temporaryRoot
    }
    elseif (Test-Path $resolvedInputPath -PathType Container) {
        $logRoot = Resolve-LogRoot -Path $resolvedInputPath
    }
    else {
        throw "InputPath 必须是日志目录或 .zip 压缩包：$resolvedInputPath"
    }

    $command = @(
        'dotnet',
        'run',
        '--project', (Join-Path $repoRoot 'src\QuickReply.ReplayEval\QuickReply.ReplayEval.csproj'),
        '-c', $Configuration
    )

    if ($NoBuild) {
        $command += '--no-build'
    }

    $command += @(
        '--',
        '--log-health',
        '--logs-root', $logRoot,
        '--out', $outputRoot
    )

    $logPath = Join-Path $outputRoot 'check-log-health.log'
    Write-Host "==> log-health"
    Write-Host "    Input: $resolvedInputPath"
    Write-Host "    Log root: $logRoot"
    Write-Host "    Output: $outputRoot"
    Write-Host "    $($command -join ' ')"

    & $command[0] $command[1..($command.Length - 1)] 2>&1 | Tee-Object -FilePath $logPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "日志健康检查失败。请查看 $logPath"
    }

    Write-Host ""
    Write-Host "Log health check passed."
    Write-Host "Report: $outputRoot"
}
finally {
    if ($temporaryRoot -and (Test-Path $temporaryRoot)) {
        try {
            Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
        }
        catch {
        }
    }
}
