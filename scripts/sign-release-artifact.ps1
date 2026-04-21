param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactPath,
    [string]$MetadataPath = '',
    [string]$OutputPath = '',
    [string]$SignerName = 'QuickReplyTool Smoke Signer',
    [string]$KeyId = 'quickreply-smoke-key-v1',
    [string]$SigningSecret = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'release-safety-common.ps1')

$artifactFullPath = Get-ReleaseSafetyFullPath -Path $ArtifactPath -Description 'Artifact'
$metadataFullPath = ''
if (-not [string]::IsNullOrWhiteSpace($MetadataPath)) {
    $metadataFullPath = Get-ReleaseSafetyFullPath -Path $MetadataPath -Description 'Package metadata'
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $artifactDirectory = Split-Path -Parent $artifactFullPath
    $artifactLeaf = Split-Path -Leaf $artifactFullPath
    $OutputPath = Join-Path $artifactDirectory ("{0}.signature.json" -f $artifactLeaf)
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputFullPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$secret = if ([string]::IsNullOrWhiteSpace($SigningSecret)) { Get-ReleaseSafetyDefaultSigningSecret } else { $SigningSecret }
$payload = New-ReleaseSafetySignaturePayload -ArtifactPath $artifactFullPath -MetadataPath $metadataFullPath -SignerName $SignerName -KeyId $KeyId
$signature = Get-ReleaseSafetySignatureValue -PayloadRecord $payload -SigningSecret $secret

$record = [ordered]@{
    schemaVersion = 'quickreply.release-signature-envelope.v1'
    smokeOnly = $true
    payload = $payload
    signature = $signature
    verificationHint = 'Use verify-release-artifact-signature.ps1 with the matching artifact and metadata paths.'
}

$record | ConvertTo-Json -Depth 10 | Set-Content -Path $outputFullPath -Encoding UTF8

Write-Host "Release artifact signature created: $outputFullPath"
