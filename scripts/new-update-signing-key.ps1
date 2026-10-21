param(
    [Parameter(Mandatory = $true)]
    [string]$PrivateKeyPath,
    [int]$KeySize = 3072
)

# Generates an RSA key pair for signing update manifests.
# - The private key (XML) is written to $PrivateKeyPath. Keep it OFF the repository and back it up safely.
# - The public key (XML) is printed. Paste it into
#   src/QuickReply.Desktop/Services/UpdateManifestSignatureVerifier.cs -> TrustedPublicKeyXml
# Works on Windows PowerShell 5.1 and PowerShell 7.

$ErrorActionPreference = "Stop"

$fullPath = [System.IO.Path]::GetFullPath($PrivateKeyPath)
if (Test-Path $fullPath) {
    throw "Refusing to overwrite existing key file: $fullPath"
}

$rsa = New-Object System.Security.Cryptography.RSACng -ArgumentList $KeySize
try {
    $privateXml = $rsa.ToXmlString($true)
    $publicXml = $rsa.ToXmlString($false)
}
finally {
    $rsa.Dispose()
}

$directory = Split-Path -Parent $fullPath
if ($directory) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
[System.IO.File]::WriteAllText($fullPath, $privateXml, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Private key written to: $fullPath"
Write-Host "DO NOT commit this file. Back it up; losing it means clients with this public key cannot verify future updates."
Write-Host ""
Write-Host "Public key (paste into TrustedPublicKeyXml):"
Write-Output $publicXml
