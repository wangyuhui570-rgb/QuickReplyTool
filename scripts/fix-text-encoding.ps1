param(
    [switch]$NormalizeRepo = $true,
    [switch]$InstallProfiles = $true
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

function Test-Utf8Bom {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    return $Bytes.Length -ge 3 -and
        $Bytes[0] -eq 0xEF -and
        $Bytes[1] -eq 0xBB -and
        $Bytes[2] -eq 0xBF
}

function Convert-FileToUtf8Bom {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if (Test-Utf8Bom -Bytes $bytes) {
        return $false
    }

    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    [System.IO.File]::WriteAllText($Path, $text, [System.Text.UTF8Encoding]::new($true))
    return $true
}

function Normalize-RepositoryFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $targetExtensions = @(".cs", ".csproj", ".sln", ".md", ".txt", ".ps1", ".psm1", ".psd1", ".iss", ".xaml")
    $skipSegments = @("\.git\", "\bin\", "\obj\", "\artifacts\", "\dist\")
    $changed = New-Object System.Collections.Generic.List[string]

    Get-ChildItem -Path $RepoRoot -Recurse -File | Where-Object {
        $extension = [System.IO.Path]::GetExtension($_.FullName)
        if ($targetExtensions -notcontains $extension) {
            return $false
        }

        foreach ($segment in $skipSegments) {
            if ($_.FullName -like "*$segment*") {
                return $false
            }
        }

        return $true
    } | ForEach-Object {
        if (Convert-FileToUtf8Bom -Path $_.FullName) {
            $changed.Add($_.FullName)
        }
    }

    return $changed
}

function Get-ProfileContent {
    return @'
$Utf8 = [System.Text.UTF8Encoding]::new($false)

try {
    [Console]::InputEncoding = $Utf8
} catch {
}

try {
    [Console]::OutputEncoding = $Utf8
} catch {
}

$OutputEncoding = $Utf8

if ($Host.Name -eq 'ConsoleHost') {
    try {
        chcp 65001 > $null
    } catch {
    }
}
'@
}

function Install-PowerShellProfiles {
    $documentsPath = [Environment]::GetFolderPath("MyDocuments")
    $profiles = @(
        (Join-Path $documentsPath "WindowsPowerShell\Microsoft.PowerShell_profile.ps1"),
        (Join-Path $documentsPath "PowerShell\Microsoft.PowerShell_profile.ps1")
    )

    $content = Get-ProfileContent
    foreach ($profilePath in $profiles) {
        $directoryPath = Split-Path -Parent $profilePath
        if (-not (Test-Path $directoryPath)) {
            New-Item -Path $directoryPath -ItemType Directory -Force | Out-Null
        }

        [System.IO.File]::WriteAllText($profilePath, $content, [System.Text.UTF8Encoding]::new($true))
    }

    return $profiles
}

$repoRoot = Get-RepoRoot
$normalizedFiles = @()
$profilePaths = @()

if ($NormalizeRepo) {
    $normalizedFiles = @(Normalize-RepositoryFiles -RepoRoot $repoRoot)
}

if ($InstallProfiles) {
    $profilePaths = @(Install-PowerShellProfiles)
}

Write-Host ("Normalized files: {0}" -f $normalizedFiles.Count)
foreach ($path in $normalizedFiles) {
    Write-Host $path
}

Write-Host ("Installed profiles: {0}" -f $profilePaths.Count)
foreach ($path in $profilePaths) {
    Write-Host $path
}
