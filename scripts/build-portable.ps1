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

function New-PortableText([int[]]$CodePoints) {
    return [string]::new([char[]]$CodePoints)
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
    $version = "0.0.0"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\QuickReplyTool-Portable-$version"
}

$outputRootFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
$appOutputPath = Join-Path $outputRootFullPath "app"
$localCloudOutputPath = Join-Path $appOutputPath "LocalCloudService"
$templateRoot = Join-Path $repoRoot "packaging\portable"
$zipPath = Join-Path ([System.IO.Path]::GetDirectoryName($outputRootFullPath)) ((Split-Path $outputRootFullPath -Leaf) + ".zip")
$metadataPath = Join-Path $outputRootFullPath "portable-package-info.json"

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

if (-not (Test-Path $templateRoot)) {
    throw "Portable template folder not found: $templateRoot"
}

Copy-Item -Path (Join-Path $templateRoot "*") -Destination $outputRootFullPath -Recurse -Force

Get-ChildItem -Path $appOutputPath -Recurse -File -Include *.pdb |
    Remove-Item -Force -ErrorAction SilentlyContinue

$versionFileName = [string]::new([char[]](0x7248,0x672C,0x4FE1,0x606F,0x002E,0x0074,0x0078,0x0074))
$versionFilePath = Join-Path $outputRootFullPath $versionFileName
$versionFileContent = @(
    (New-PortableText @(0x5BA2,0x670D,0x6548,0x7387,0x5DE5,0x5177,0x7EFF,0x8272,0x7248)),
    ("{0}{1}" -f (New-PortableText @(0x7248,0x672C,0x53F7,0xFF1A)), $version),
    ("{0}{1}" -f (New-PortableText @(0x6784,0x5EFA,0x914D,0x7F6E,0xFF1A)), $Configuration),
    ("{0}{1}" -f (New-PortableText @(0x8FD0,0x884C,0x5E73,0x53F0,0xFF1A)), $Runtime),
    ("{0}{1}" -f (New-PortableText @(0x662F,0x5426,0x81EA,0x5305,0x542B,0xFF1A)), $SelfContained.ToString().ToLowerInvariant()),
    ("{0}{1}" -f (New-PortableText @(0x6253,0x5305,0x65F6,0x95F4,0xFF1A)), (Get-Date -Format "yyyy-MM-dd HH:mm:ss")),
    ("{0}%AppData%\QuickReplyTool" -f (New-PortableText @(0x7528,0x6237,0x6570,0x636E,0x76EE,0x5F55,0xFF1A)))
) -join [Environment]::NewLine
[System.IO.File]::WriteAllText($versionFilePath, $versionFileContent, [System.Text.UTF8Encoding]::new($true))

if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}

Compress-Archive -Path (Join-Path $outputRootFullPath "*") -DestinationPath $zipPath -CompressionLevel Optimal

$zipHash = Get-FileHash -Path $zipPath -Algorithm SHA256
$zipInfo = Get-Item -Path $zipPath
$metadata = [ordered]@{
    packageKind = "portable_zip"
    appVersion = $version
    releaseChannel = $ReleaseChannel
    artifactFileName = $zipInfo.Name
    artifactPath = $zipInfo.FullName
    sizeBytes = [int64]$zipInfo.Length
    sha256 = $zipHash.Hash
    generatedAt = (Get-Date).ToString("o")
    runtime = $Runtime
    selfContained = $SelfContained
}

$metadata | ConvertTo-Json -Depth 6 | Set-Content -Path $metadataPath -Encoding UTF8

Write-Host "Portable folder created: $outputRootFullPath"
Write-Host "Portable zip created: $zipPath"
Write-Host "Portable metadata: $metadataPath"
