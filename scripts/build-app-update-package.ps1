param(
    [string]$ProjectPath = "src/QuickReply.Desktop/QuickReply.Desktop.csproj",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [object]$SelfContained = $true,
    [string]$OutputRoot = "",
    [string]$AppVersion = "",
    [string]$ReleaseChannel = "stable",
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

if ([string]::IsNullOrWhiteSpace($AppVersion)) {
    $AppVersion = $projectVersion
}

if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesFile)) {
    $releaseNotesFullPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ReleaseNotesFile))
    if (-not (Test-Path $releaseNotesFullPath)) {
        throw "Release notes file not found: $releaseNotesFullPath"
    }

    $ReleaseNotes = Get-Content -Raw -Path $releaseNotesFullPath
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\ota\QuickReplyTool-AppUpdate-$AppVersion"
}

$outputRootFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
$packageRoot = Join-Path $outputRootFullPath "package"
$appOutputPath = Join-Path $packageRoot "app"
$localCloudOutputPath = Join-Path $appOutputPath "LocalCloudService"
$metadataPath = Join-Path $packageRoot "app-update-info.json"
$manifestSnippetPath = Join-Path $outputRootFullPath "release-manifest.snippet.json"
$zipPath = Join-Path $outputRootFullPath ("QuickReplyTool-AppUpdate-v{0}.zip" -f $AppVersion)

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
    appVersion = $AppVersion
    baseProjectVersion = $projectVersion
    packageKind = "zip_ota"
    releaseChannel = $ReleaseChannel
    releaseNotes = $normalizedReleaseNotes.Trim()
    generatedAt = (Get-Date).ToString("o")
    runtime = $Runtime
    selfContained = $SelfContained
}

$metadataJson = $metadata | ConvertTo-Json -Depth 6
Set-Content -Path $metadataPath -Value $metadataJson -Encoding UTF8

if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal

$fileHash = Get-FileHash -Path $zipPath -Algorithm SHA256
$fileInfo = Get-Item -Path $zipPath
$manifestSnippet = [ordered]@{
    latestAppVersion = $AppVersion
    latestAppDownloadUrl = ""
    latestAppPackageKind = "zip_ota"
    latestAppSha256 = $fileHash.Hash
    latestAppSizeBytes = [int64]$fileInfo.Length
    latestAppReleaseChannel = $ReleaseChannel
    latestAppMandatory = $false
    latestAppReleaseNotes = $metadata.releaseNotes
}

$manifestSnippetJson = $manifestSnippet | ConvertTo-Json -Depth 6
Set-Content -Path $manifestSnippetPath -Value $manifestSnippetJson -Encoding UTF8

Write-Host "App update package folder created: $outputRootFullPath"
Write-Host "App update zip created: $zipPath"
Write-Host "App update metadata: $metadataPath"
Write-Host "Release manifest snippet: $manifestSnippetPath"
