param(
    [string]$ProjectPath = "src/QuickReply.CloudService/QuickReply.CloudService.csproj",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [object]$SelfContained = $true,
    [string]$OutputRoot = ""
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

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\local-cloud-service"
}

$outputRootFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
if (Test-Path $outputRootFullPath) {
    Remove-Item -Path $outputRootFullPath -Recurse -Force
}

New-Item -Path $outputRootFullPath -ItemType Directory -Force | Out-Null

$publishArgs = @(
    "publish",
    $projectFullPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", $SelfContained.ToString().ToLowerInvariant(),
    "-p:PublishSingleFile=false",
    "-p:PublishTrimmed=false",
    "-p:UseAppHost=true",
    "-o", $outputRootFullPath
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Get-ChildItem -Path $outputRootFullPath -Recurse -File -Include *.pdb |
    Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "Local cloud service published: $outputRootFullPath"
