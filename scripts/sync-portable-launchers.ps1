param(
    [Parameter(Mandatory = $true)]
    [string]$PortableRoot,
    [switch]$Rezip
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$portableRootFullPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $PortableRoot))
$templateRoot = Join-Path $repoRoot "packaging\portable"

if (-not (Test-Path $portableRootFullPath)) {
    throw "Portable root not found: $portableRootFullPath"
}

Copy-Item -Path (Join-Path $templateRoot "Launch-QuickReplyTool.bat") -Destination $portableRootFullPath -Force
Copy-Item -Path (Join-Path $templateRoot "Portable-Readme.txt") -Destination (Join-Path $portableRootFullPath "Portable-Readme.txt") -Force
Copy-Item -Path (Join-Path $templateRoot "Upgrade-Portable.bat") -Destination $portableRootFullPath -Force
Copy-Item -Path (Join-Path $templateRoot "START_HERE.bat") -Destination $portableRootFullPath -Force

$targetPath = Join-Path $portableRootFullPath "START_HERE.bat"
$iconPath = Join-Path $portableRootFullPath "app\QuickReplyTool.exe"
$shortcutPath = Join-Path $portableRootFullPath "START_HERE.lnk"

$wShell = New-Object -ComObject WScript.Shell
$shortcut = $wShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $targetPath
$shortcut.WorkingDirectory = $portableRootFullPath
$shortcut.Description = "Start QuickReplyTool Portable"
$shortcut.IconLocation = "${iconPath},0"
$shortcut.Save()

if ($Rezip) {
    $zipPath = "${portableRootFullPath}.zip"
    if (Test-Path $zipPath) {
        Remove-Item -Path $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $portableRootFullPath "*") -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Portable zip updated: $zipPath"
}

Write-Host "Portable launchers updated: $portableRootFullPath"
