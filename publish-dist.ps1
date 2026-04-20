$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot "src\QuickReply.Desktop\QuickReply.Desktop.csproj"
$runtimeIdentifier = "win-x64"

function Get-VersionedDistDirectories {
    param(
        [string]$RootPath
    )

    Get-ChildItem $RootPath -Directory -Filter "QuickReplyTool-v*" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^QuickReplyTool-v(?<version>\d+(?:\.\d+){1,3})$' } |
        ForEach-Object {
            [PSCustomObject]@{
                Name = $_.Name
                FullName = $_.FullName
                Version = [version]$Matches.version
            }
        } |
        Sort-Object Version
}

function Get-QuickReplyProcessesInDirectory {
    param(
        [string]$DirectoryPath
    )

    $normalizedDirectory = [System.IO.Path]::GetFullPath($DirectoryPath)
    @(Get-Process QuickReplyTool -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                $processPath = $_.Path
                if ([string]::IsNullOrWhiteSpace($processPath)) {
                    return $false
                }

                $normalizedProcessPath = [System.IO.Path]::GetFullPath($processPath)
                return $normalizedProcessPath.StartsWith($normalizedDirectory, [System.StringComparison]::OrdinalIgnoreCase)
            }
            catch {
                return $false
            }
        })
}

function Stop-QuickReplyProcessesInDirectory {
    param(
        [string]$DirectoryPath
    )

    $processes = @(Get-QuickReplyProcessesInDirectory -DirectoryPath $DirectoryPath)
    foreach ($process in $processes) {
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
        catch {
        }
    }

    foreach ($process in $processes) {
        try {
            $process.WaitForExit(5000) | Out-Null
        }
        catch {
        }
    }
}

function Remove-DistDirectory {
    param(
        [string]$DirectoryPath
    )

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        Stop-QuickReplyProcessesInDirectory -DirectoryPath $DirectoryPath

        if (-not (Test-Path $DirectoryPath)) {
            return
        }

        try {
            Remove-Item $DirectoryPath -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -eq 5) {
                throw
            }

            Start-Sleep -Milliseconds 400
        }
    }
}

[xml]$projectFile = Get-Content $projectPath
$version = $projectFile.Project.PropertyGroup.Version

if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Version was not found in the project file."
}

$distRoot = Join-Path $repoRoot "dist"
$packageDir = Join-Path $distRoot ("QuickReplyTool-v{0}" -f $version)
$publishDir = Join-Path $packageDir "App"

if (-not (Test-Path $distRoot)) {
    New-Item -ItemType Directory -Path $distRoot | Out-Null
}

if (Test-Path $packageDir) {
    Remove-DistDirectory -DirectoryPath $packageDir
}

New-Item -ItemType Directory -Path $packageDir | Out-Null

dotnet publish $projectPath `
    -c Release `
    -r $runtimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:UseAppHost=true `
    -o $publishDir

$launcherPath = Join-Path $packageDir "Launch QuickReplyTool.cmd"
$launcherContent = @"
@echo off
setlocal
set "APP_DIR=%~dp0App"
if not exist "%APP_DIR%\QuickReplyTool.exe" (
    echo 鏈壘鍒扮▼搴忎富鏂囦欢锛?APP_DIR%\QuickReplyTool.exe
    pause
    exit /b 1
)
start "" /D "%APP_DIR%" "%APP_DIR%\QuickReplyTool.exe"
"@
Set-Content -Path $launcherPath -Value $launcherContent -Encoding ASCII

$versionedDirectories = @(Get-VersionedDistDirectories -RootPath $distRoot)
if ($versionedDirectories.Count -gt 2) {
    $directoriesToDelete = $versionedDirectories | Select-Object -First ($versionedDirectories.Count - 2)
    foreach ($directory in $directoriesToDelete) {
        Remove-DistDirectory -DirectoryPath $directory.FullName
    }
}

Write-Host "Published self-contained green build to $packageDir"
