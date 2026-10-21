param(
    [string]$ProjectPath = "src/QuickReply.Desktop/QuickReply.Desktop.csproj",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [object]$SelfContained = $true,
    [string]$OutputRoot = "",
    [string]$ReleaseChannel = "stable"
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

$SelfContained = ConvertTo-BoolValue $SelfContained $true

function Resolve-IsccPath {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    foreach ($candidate in @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "ISCC.exe was not found. Install Inno Setup 6 first."
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectFullPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ProjectPath))
if (-not (Test-Path $projectFullPath)) {
    throw "Project not found: $projectFullPath"
}

[xml]$projectXml = Get-Content -Path $projectFullPath
$versionNode = $projectXml.Project.PropertyGroup |
    Where-Object { $_.Version } |
    Select-Object -First 1
$version = if ($versionNode) { $versionNode.Version.Trim() } else { "" }
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Version was not found in the project file."
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\installer\QuickReplyTool-Setup-$version"
}

$outputRootFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
$publishDir = Join-Path $outputRootFullPath "app"
$installerOutputDir = Join-Path $outputRootFullPath "installer"
$installerScriptPath = Join-Path $repoRoot "packaging\installer\QuickReplyTool.iss"
$isccPath = Resolve-IsccPath
$outputBaseFilename = "QuickReplyTool-Setup-v$version"
$metadataPath = Join-Path $outputRootFullPath "installer-package-info.json"

if (Test-Path $outputRootFullPath) {
    Remove-Item -Path $outputRootFullPath -Recurse -Force
}

New-Item -Path $publishDir -ItemType Directory -Force | Out-Null
New-Item -Path $installerOutputDir -ItemType Directory -Force | Out-Null

$publishArgs = @(
    "publish",
    $projectFullPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", $SelfContained.ToString().ToLowerInvariant(),
    "-p:PublishSingleFile=false",
    "-p:PublishTrimmed=false",
    "-p:UseAppHost=true",
    "-o", $publishDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}


Get-ChildItem -Path $publishDir -Recurse -File -Include *.pdb |
    Remove-Item -Force -ErrorAction SilentlyContinue

$isccArgs = @(
    "/Qp",
    "/DMyAppVersion=$version",
    "/DMyAppExeName=QuickReplyTool.exe",
    "/DMyPublishDir=$publishDir",
    "/DMyOutputDir=$installerOutputDir",
    "/DMyOutputBaseFilename=$outputBaseFilename",
    $installerScriptPath
)

& $isccPath @isccArgs
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE"
}

$installerPath = Join-Path $installerOutputDir ($outputBaseFilename + ".exe")
if (-not (Test-Path $installerPath)) {
    throw "Installer was not generated: $installerPath"
}

$installerHash = Get-FileHash -Path $installerPath -Algorithm SHA256
$installerInfo = Get-Item -Path $installerPath
$metadata = [ordered]@{
    packageKind = "installer_exe"
    appVersion = $version
    releaseChannel = $ReleaseChannel
    artifactFileName = $installerInfo.Name
    artifactPath = $installerInfo.FullName
    sizeBytes = [int64]$installerInfo.Length
    sha256 = $installerHash.Hash
    generatedAt = (Get-Date).ToString("o")
    runtime = $Runtime
    selfContained = $SelfContained
}

$metadata | ConvertTo-Json -Depth 6 | Set-Content -Path $metadataPath -Encoding UTF8

Write-Host "Installer created: $installerPath"
Write-Host "Installer metadata: $metadataPath"
