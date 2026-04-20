param(
    [string]$ProjectPath = "src/QuickReply.Desktop/QuickReply.Desktop.csproj",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [object]$SelfContained = $true,
    [string]$PackageOutputRoot = "",
    [string]$PublishRoot = "",
    [string]$AppVersion = "",
    [string]$ReleaseChannel = "stable",
    [string]$ReleaseNotes = "",
    [string]$ReleaseNotesFile = "",
    [Parameter(Mandatory = $true)]
    [string]$PublicDownloadBaseUrl,
    [string]$ManifestFileName = "update-manifest.json",
    [string]$ReleaseEvidenceVersion = "",
    [string]$ReleaseEvidenceOutputRoot = ""
)

$ErrorActionPreference = "Stop"

function ConvertTo-BoolValue([object]$Value, [bool]$Default = $true) {
    if ($Value -is [bool]) {
        return $Value
    }

    if ($null -eq $Value) {
        return $Default
    }

    if ($Value -is [string]) {
        switch ($Value.Trim().ToLowerInvariant()) {
            '' { return $Default }
            'true' { return $true }
            '$true' { return $true }
            '1' { return $true }
            'false' { return $false }
            '$false' { return $false }
            '0' { return $false }
        }
    }

    throw "Invalid boolean value: $Value"
}

function Copy-PathIfExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    if (-not (Test-Path $SourcePath)) {
        return
    }

    $destinationParent = Split-Path -Parent $DestinationPath
    if ($destinationParent) {
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    }

    Copy-Item -Path $SourcePath -Destination $DestinationPath -Recurse -Force
}

$SelfContained = ConvertTo-BoolValue $SelfContained $true

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($PackageOutputRoot)) {
    $PackageOutputRoot = Join-Path $repoRoot "artifacts\ota-stage"
}

if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $repoRoot "artifacts\published-ota"
}

$packageOutputRootFullPath = [System.IO.Path]::GetFullPath($PackageOutputRoot)
$publishRootFullPath = [System.IO.Path]::GetFullPath($PublishRoot)
$stagedDownloadsRoot = Join-Path $publishRootFullPath "downloads\ota"

$buildArgs = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', (Join-Path $repoRoot 'scripts\build-app-update-package.ps1'),
    '-ProjectPath', $ProjectPath,
    '-Configuration', $Configuration,
    '-Runtime', $Runtime,
    "-SelfContained:$($SelfContained.ToString().ToLowerInvariant())",
    '-OutputRoot', $packageOutputRootFullPath,
    '-AppVersion', $AppVersion,
    '-ReleaseChannel', $ReleaseChannel
)

if (-not [string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    $buildArgs += @('-ReleaseNotes', $ReleaseNotes)
}

if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesFile)) {
    $buildArgs += @('-ReleaseNotesFile', $ReleaseNotesFile)
}

& powershell.exe @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "build-app-update-package.ps1 failed with exit code $LASTEXITCODE"
}

$manifestSnippetPath = Join-Path $packageOutputRootFullPath "release-manifest.snippet.json"
if (-not (Test-Path $manifestSnippetPath)) {
    throw "Manifest snippet not found: $manifestSnippetPath"
}

$zipFile = Get-ChildItem -Path $packageOutputRootFullPath -Filter 'QuickReplyTool-AppUpdate-v*.zip' -File | Select-Object -First 1
if (-not $zipFile) {
    throw "OTA zip artifact was not found under $packageOutputRootFullPath"
}

New-Item -ItemType Directory -Path $stagedDownloadsRoot -Force | Out-Null
$stagedZipPath = Join-Path $stagedDownloadsRoot $zipFile.Name
Copy-Item -Path $zipFile.FullName -Destination $stagedZipPath -Force

$publicDownloadBaseUrl = $PublicDownloadBaseUrl.Trim().TrimEnd('/')
$manifest = Get-Content -Raw -Path $manifestSnippetPath | ConvertFrom-Json
$manifest.latestAppDownloadUrl = "$publicDownloadBaseUrl/$($zipFile.Name)"

New-Item -ItemType Directory -Path $publishRootFullPath -Force | Out-Null
$manifestOutputPath = Join-Path $publishRootFullPath $ManifestFileName
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestOutputPath -Encoding UTF8
Copy-Item -Path $manifestSnippetPath -Destination (Join-Path $publishRootFullPath 'release-manifest.snippet.json') -Force

if (-not [string]::IsNullOrWhiteSpace($ReleaseEvidenceVersion)) {
    $releaseEvidenceArgs = @(
        '-VersionLabel', $ReleaseEvidenceVersion
    )
    if (-not [string]::IsNullOrWhiteSpace($ReleaseEvidenceOutputRoot)) {
        $releaseEvidenceArgs += @('-OutputRoot', ([System.IO.Path]::GetFullPath($ReleaseEvidenceOutputRoot)))
    }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\new-release-evidence.ps1') @releaseEvidenceArgs
    if ($LASTEXITCODE -ne 0) {
        throw "new-release-evidence.ps1 failed with exit code $LASTEXITCODE"
    }
}

Write-Host "OTA publish staging root: $publishRootFullPath"
Write-Host "Staged OTA zip: $stagedZipPath"
Write-Host "Staged update manifest: $manifestOutputPath"
