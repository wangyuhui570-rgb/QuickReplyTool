param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,
    [Parameter(Mandatory = $true)]
    [string]$PrivateKeyPath
)

# Signs an update manifest (adds/overwrites "latestAppSignature").
# The canonical payload MUST stay in sync with
# UpdateManifestSignatureVerifier.BuildCanonicalPayload (C#).
# Works on Windows PowerShell 5.1 and PowerShell 7.

$ErrorActionPreference = "Stop"

function Get-TrimmedText([object]$Value) {
    if ($null -eq $Value) { return "" }
    return ([string]$Value).Trim()
}

function Get-CanonicalManifestPayload($Manifest) {
    $size = ""
    if ($null -ne $Manifest.latestAppSizeBytes -and [int64]$Manifest.latestAppSizeBytes -gt 0) {
        $size = ([int64]$Manifest.latestAppSizeBytes).ToString([System.Globalization.CultureInfo]::InvariantCulture)
    }

    $mandatory = "false"
    if ($Manifest.latestAppMandatory -eq $true) { $mandatory = "true" }

    $lines = @(
        "quickreply-update-manifest-v1",
        (Get-TrimmedText $Manifest.latestAppVersion),
        (Get-TrimmedText $Manifest.latestAppDownloadUrl),
        (Get-TrimmedText $Manifest.latestAppSha256).ToLowerInvariant(),
        $size,
        (Get-TrimmedText $Manifest.latestAppPackageKind).ToLowerInvariant(),
        $mandatory
    )
    return ($lines -join "`n")
}

$manifestFullPath = [System.IO.Path]::GetFullPath($ManifestPath)
$keyFullPath = [System.IO.Path]::GetFullPath($PrivateKeyPath)
$manifest = [System.IO.File]::ReadAllText($manifestFullPath) | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace((Get-TrimmedText $manifest.latestAppSha256))) {
    throw "Manifest has no latestAppSha256; refusing to sign."
}

$payloadBytes = [System.Text.Encoding]::UTF8.GetBytes((Get-CanonicalManifestPayload $manifest))

$rsa = New-Object System.Security.Cryptography.RSACng
try {
    $rsa.FromXmlString([System.IO.File]::ReadAllText($keyFullPath))
    $signatureBytes = $rsa.SignData(
        $payloadBytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
}
finally {
    $rsa.Dispose()
}

$signature = [Convert]::ToBase64String($signatureBytes)
if ($manifest.PSObject.Properties.Name -contains 'latestAppSignature') {
    $manifest.latestAppSignature = $signature
}
else {
    $manifest | Add-Member -NotePropertyName latestAppSignature -NotePropertyValue $signature
}

$json = $manifest | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($manifestFullPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Signed update manifest: $manifestFullPath"
