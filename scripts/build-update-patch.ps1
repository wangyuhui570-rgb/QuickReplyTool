param(
    [Parameter(Mandatory = $true)][string]$PreviousZip,
    [Parameter(Mandatory = $true)][string]$CurrentZip,
    [Parameter(Mandatory = $true)][string]$FromVersion,
    [Parameter(Mandatory = $true)][string]$ToVersion,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

# Builds the incremental patch AppUpdatePatcher applies: only the files of the current package
# that are new or differ from the previous one, plus patch-manifest.json listing the SHA256 of
# every file in the current package so the client can prove the result equals the full zip.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-ZipHashes([string]$zipPath) {
    $hashes = @{}
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $stream = $entry.Open()
            try {
                $sha = [System.Security.Cryptography.SHA256]::Create()
                $hash = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant()
                $sha.Dispose()
            } finally {
                $stream.Dispose()
            }
            $hashes[$entry.FullName.Replace('\', '/')] = $hash
        }
    } finally {
        $archive.Dispose()
    }
    return $hashes
}

$previous = Get-ZipHashes $PreviousZip
$current = Get-ZipHashes $CurrentZip
$changed = @($current.Keys | Where-Object { -not $previous.ContainsKey($_) -or $previous[$_] -ne $current[$_] } | Sort-Object)

$files = [ordered]@{}
foreach ($key in ($current.Keys | Sort-Object)) { $files[$key] = $current[$key] }
$manifest = [ordered]@{ from = $FromVersion; to = $ToVersion; files = $files } | ConvertTo-Json -Depth 4

if (Test-Path -LiteralPath $OutputPath) { Remove-Item -LiteralPath $OutputPath -Force }
$source = [System.IO.Compression.ZipFile]::OpenRead($CurrentZip)
$target = [System.IO.Compression.ZipFile]::Open($OutputPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $changed) {
        $entry = $source.GetEntry($name)
        if ($null -eq $entry) { $entry = $source.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq $name } | Select-Object -First 1 }
        $out = $target.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
        $in = $entry.Open(); $dst = $out.Open()
        try { $in.CopyTo($dst) } finally { $dst.Dispose(); $in.Dispose() }
    }
    $manifestEntry = $target.CreateEntry('patch-manifest.json', [System.IO.Compression.CompressionLevel]::Optimal)
    $writer = New-Object System.IO.StreamWriter($manifestEntry.Open(), (New-Object System.Text.UTF8Encoding($false)))
    try { $writer.Write($manifest) } finally { $writer.Dispose() }
} finally {
    $target.Dispose()
    $source.Dispose()
}

$removed = @($previous.Keys | Where-Object { -not $current.ContainsKey($_) }).Count
"patch $FromVersion -> ${ToVersion}: $($changed.Count) changed, $removed removed, $($current.Count) files total, $((Get-Item -LiteralPath $OutputPath).Length) bytes"
