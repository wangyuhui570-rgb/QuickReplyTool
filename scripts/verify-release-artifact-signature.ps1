param(
    [Parameter(Mandatory = $true)]
    [string]$SignaturePath,
    [Parameter(Mandatory = $true)]
    [string]$ArtifactPath,
    [string]$MetadataPath = '',
    [string]$OutputPath = '',
    [string]$SigningSecret = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'release-safety-common.ps1')

$signatureFullPath = Get-ReleaseSafetyFullPath -Path $SignaturePath -Description 'Signature sidecar'
$artifactFullPath = Get-ReleaseSafetyFullPath -Path $ArtifactPath -Description 'Artifact'
$metadataFullPath = ''
if (-not [string]::IsNullOrWhiteSpace($MetadataPath)) {
    $metadataFullPath = Get-ReleaseSafetyFullPath -Path $MetadataPath -Description 'Package metadata'
}

$secret = if ([string]::IsNullOrWhiteSpace($SigningSecret)) { Get-ReleaseSafetyDefaultSigningSecret } else { $SigningSecret }
$signatureRecord = Read-ReleaseSafetyJsonFile -Path $signatureFullPath
$validation = Test-ReleaseSafetySignatureRecord -SignatureRecord $signatureRecord -ArtifactPath $artifactFullPath -MetadataPath $metadataFullPath -SigningSecret $secret

if (-not $validation.isValid) {
    throw ('Signature verification failed: ' + ($validation.errors -join '; '))
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $signatureDirectory = Split-Path -Parent $signatureFullPath
    $signatureLeaf = [System.IO.Path]::GetFileNameWithoutExtension((Split-Path -Leaf $signatureFullPath))
    $OutputPath = Join-Path $signatureDirectory ("{0}.receipt.json" -f $signatureLeaf)
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputFullPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$receipt = [ordered]@{
    schemaVersion = 'quickreply.release-signature-receipt.v1'
    validatedAt = (Get-Date).ToString('O')
    smokeOnly = $true
    signaturePath = $signatureFullPath
    artifactPath = $artifactFullPath
    metadataPath = $metadataFullPath
    artifactSha256 = $validation.artifactSha256
    metadataSha256 = $validation.metadataSha256
    status = 'verified'
}

$receipt | ConvertTo-Json -Depth 6 | Set-Content -Path $outputFullPath -Encoding UTF8

Write-Host "Release artifact signature verified: $outputFullPath"
