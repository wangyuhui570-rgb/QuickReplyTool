param(
    [string]$ProjectPath = "src/QuickReply.Desktop/QuickReply.Desktop.csproj",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [object]$SelfContained = $true,
    [string]$OutputRoot = "",
    [string]$PatchVersion = "",
    [string]$ReleaseChannel = "stable",
    [string[]]$IssueIds = @(),
    [string]$ReleaseNotes = "",
    [string]$ReleaseNotesFile = ""
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

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectFullPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ProjectPath))
if (-not (Test-Path $projectFullPath)) {
    throw "Project not found: $projectFullPath"
}

[xml]$projectXml = Get-Content -Path $projectFullPath
$versionNode = $projectXml.Project.PropertyGroup |
    Where-Object { $_.Version } |
    Select-Object -First 1
$projectVersion = if ($versionNode) { $versionNode.Version.Trim() } else { "" }
if ([string]::IsNullOrWhiteSpace($projectVersion)) {
    throw "Version was not found in the project file."
}

if ([string]::IsNullOrWhiteSpace($PatchVersion)) {
    $PatchVersion = $projectVersion
}

if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesFile)) {
    $releaseNotesFullPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ReleaseNotesFile))
    if (-not (Test-Path $releaseNotesFullPath)) {
        throw "Release notes file not found: $releaseNotesFullPath"
    }

    $ReleaseNotes = Get-Content -Raw -Path $releaseNotesFullPath
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\patches\QuickReplyTool-Patch-$PatchVersion"
}

$outputRootFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
$packageRoot = Join-Path $outputRootFullPath "package"
$appOutputPath = Join-Path $packageRoot "app"
$localCloudOutputPath = Join-Path $appOutputPath "LocalCloudService"
$metadataPath = Join-Path $packageRoot "patch-info.json"
$zipPath = Join-Path $outputRootFullPath ("QuickReplyTool-Patch-v{0}.zip" -f $PatchVersion)
$packageSidecarPath = Join-Path $outputRootFullPath "patch-package-info.json"

if (Test-Path $outputRootFullPath) {
    Remove-Item -Path $outputRootFullPath -Recurse -Force
}

New-Item -Path $appOutputPath -ItemType Directory -Force | Out-Null

$publishArgs = @(
    "publish",
    $projectFullPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", $SelfContained.ToString().ToLowerInvariant(),
    "-p:PublishSingleFile=false",
    "-p:PublishTrimmed=false",
    "-p:UseAppHost=true",
    "-o", $appOutputPath
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\publish-local-cloud-service.ps1') `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -SelfContained:$SelfContained `
    -OutputRoot $localCloudOutputPath
if ($LASTEXITCODE -ne 0) {
    throw "Local cloud-service publish failed with exit code $LASTEXITCODE"
}

Get-ChildItem -Path $appOutputPath -Recurse -File -Include *.pdb |
    Remove-Item -Force -ErrorAction SilentlyContinue

$normalizedReleaseNotes = if ($null -eq $ReleaseNotes) { "" } else { [string]$ReleaseNotes }

$metadata = [ordered]@{
    patchVersion = $PatchVersion
    appVersion = $projectVersion
    releaseChannel = $ReleaseChannel
    issueIds = @($IssueIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    releaseNotes = $normalizedReleaseNotes
    generatedAt = (Get-Date).ToString("o")
    runtime = $Runtime
    selfContained = $SelfContained
}

$metadata.releaseNotes = ($metadata.releaseNotes -as [string]).Trim()

$metadataJson = $metadata | ConvertTo-Json -Depth 6
Set-Content -Path $metadataPath -Value $metadataJson -Encoding UTF8

if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal

$zipHash = Get-FileHash -Path $zipPath -Algorithm SHA256
$zipInfo = Get-Item -Path $zipPath
$packageSidecar = [ordered]@{
    packageKind = "issue_patch_zip"
    patchVersion = $PatchVersion
    appVersion = $projectVersion
    releaseChannel = $ReleaseChannel
    artifactFileName = $zipInfo.Name
    artifactPath = $zipInfo.FullName
    sizeBytes = [int64]$zipInfo.Length
    sha256 = $zipHash.Hash
    generatedAt = (Get-Date).ToString("o")
    runtime = $Runtime
    selfContained = $SelfContained
}

$packageSidecar | ConvertTo-Json -Depth 6 | Set-Content -Path $packageSidecarPath -Encoding UTF8

Write-Host "Patch folder created: $outputRootFullPath"
Write-Host "Patch zip created: $zipPath"
Write-Host "Patch metadata: $metadataPath"
Write-Host "Patch package metadata: $packageSidecarPath"
