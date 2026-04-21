param(
    [Parameter(Mandatory = $true)]
    [string]$CurrentArtifactPath,
    [Parameter(Mandatory = $true)]
    [string]$CurrentMetadataPath,
    [string]$RollbackVersion = '',
    [string]$BaselineVersion = '',
    [string]$OutputRoot = '',
    [string]$ReleaseChannel = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'release-safety-common.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactFullPath = Get-ReleaseSafetyFullPath -Path $CurrentArtifactPath -Description 'Current artifact'
$metadataFullPath = Get-ReleaseSafetyFullPath -Path $CurrentMetadataPath -Description 'Current package metadata'
$metadata = Read-ReleaseSafetyJsonFile -Path $metadataFullPath

$targetPackageKind = Resolve-ReleaseSafetyPackageKind -Metadata $metadata
$targetVersion = Resolve-ReleaseSafetyVersionLabel -Metadata $metadata
$resolvedReleaseChannel = if ([string]::IsNullOrWhiteSpace($ReleaseChannel)) { Resolve-ReleaseSafetyReleaseChannel -Metadata $metadata } else { $ReleaseChannel }
$resolvedRollbackVersion = if ([string]::IsNullOrWhiteSpace($RollbackVersion)) { "$targetVersion-rollback-smoke" } else { $RollbackVersion }
$resolvedBaselineVersion = if ([string]::IsNullOrWhiteSpace($BaselineVersion)) { "$targetVersion-last-known-good" } else { $BaselineVersion }

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\rollback\QuickReplyTool-Rollback-$resolvedRollbackVersion"
}

$outputRootFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
$packageRoot = Join-Path $outputRootFullPath 'package'
$snapshotRoot = Join-Path $packageRoot 'snapshots'
$planPath = Join-Path $packageRoot 'rollback-plan.json'
$readmePath = Join-Path $packageRoot 'README.md'
$zipPath = Join-Path $outputRootFullPath ("QuickReplyTool-Rollback-v{0}.zip" -f $resolvedRollbackVersion)
$packageInfoPath = Join-Path $outputRootFullPath 'rollback-package-info.json'

if (Test-Path -LiteralPath $outputRootFullPath) {
    Remove-Item -Path $outputRootFullPath -Recurse -Force
}

New-Item -ItemType Directory -Path $snapshotRoot -Force | Out-Null

$artifactInfo = Get-Item -LiteralPath $artifactFullPath
$metadataInfo = Get-Item -LiteralPath $metadataFullPath
$artifactHash = Get-ReleaseSafetyFileSha256 -Path $artifactFullPath
$metadataHash = Get-ReleaseSafetyFileSha256 -Path $metadataFullPath

Copy-Item -LiteralPath $metadataFullPath -Destination (Join-Path $snapshotRoot $metadataInfo.Name) -Force

$rollbackPlan = [ordered]@{
    schemaVersion = 'quickreply.rollback-plan.v1'
    smokeOnly = $true
    rollbackMode = 'dry_run_skeleton'
    generatedAt = (Get-Date).ToString('O')
    releaseChannel = $resolvedReleaseChannel
    baselineVersion = $resolvedBaselineVersion
    rollbackVersion = $resolvedRollbackVersion
    targetPackage = [ordered]@{
        packageKind = $targetPackageKind
        versionLabel = $targetVersion
        artifactFileName = $artifactInfo.Name
        artifactSha256 = $artifactHash
        artifactSizeBytes = [int64]$artifactInfo.Length
        metadataFileName = $metadataInfo.Name
        metadataSha256 = $metadataHash
    }
    restorePayload = [ordered]@{
        included = $false
        description = 'This smoke scaffold only packages the rollback plan and metadata snapshot. Replace it with a real signed restore payload before production use.'
        requiredRuntimeFiles = @(
            'QuickReplyTool.exe',
            'LocalCloudService\QuickReply.CloudService.exe'
        )
    }
    rollbackSteps = @(
        [ordered]@{ id = 'stop-processes'; description = 'Stop QuickReplyTool and helper/update processes before replacing files.' },
        [ordered]@{ id = 'backup-current-install'; description = 'Snapshot the current installation directory and updater plan/log files.' },
        [ordered]@{ id = 'restore-known-good-package'; description = 'Restore the previously approved package or reinstall the known-good version.' },
        [ordered]@{ id = 'verify-binaries'; description = 'Confirm QuickReplyTool.exe and LocalCloudService are present after restore.' },
        [ordered]@{ id = 'verify-version-and-startup'; description = 'Launch the app, confirm the expected version, and run a minimal send smoke.' },
        [ordered]@{ id = 'retain-backup-until-signoff'; description = 'Keep rollback backup assets until manual signoff completes.' }
    )
    evidenceChecklist = @(
        'Record the pre-rollback version and package path.',
        'Capture the rollback command/log output.',
        'Capture the post-rollback version and startup evidence.',
        'Attach updater logs and manual regression notes.'
    )
}

$rollbackPlan | ConvertTo-Json -Depth 10 | Set-Content -Path $planPath -Encoding UTF8

$readmeLines = @(
    '# QuickReplyTool Rollback Package Smoke Scaffold',
    '',
    "- Target package kind: $targetPackageKind",
    "- Target version: $targetVersion",
    "- Baseline version: $resolvedBaselineVersion",
    "- Rollback version label: $resolvedRollbackVersion",
    '',
    'This package is a dry-run scaffold used for CI and release-drill validation.',
    'It does not contain a production restore payload or a real certificate-backed signature.',
    'Replace the smoke signer, payload acquisition, and install/restore actions before using it in production.'
)

$readmeLines | Set-Content -Path $readmePath -Encoding UTF8

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -Path $zipPath -Force
}

Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal

$zipInfo = Get-Item -LiteralPath $zipPath
$zipHash = Get-ReleaseSafetyFileSha256 -Path $zipPath

$packageInfo = [ordered]@{
    schemaVersion = 'quickreply.rollback-package-info.v1'
    smokeOnly = $true
    packageKind = 'rollback_bundle_zip'
    targetPackageKind = $targetPackageKind
    targetVersion = $targetVersion
    baselineVersion = $resolvedBaselineVersion
    rollbackVersion = $resolvedRollbackVersion
    releaseChannel = $resolvedReleaseChannel
    artifactFileName = $zipInfo.Name
    artifactPath = $zipInfo.FullName
    sizeBytes = [int64]$zipInfo.Length
    sha256 = $zipHash
    generatedAt = (Get-Date).ToString('O')
}

$packageInfo | ConvertTo-Json -Depth 8 | Set-Content -Path $packageInfoPath -Encoding UTF8

Write-Host "Rollback package scaffold created: $zipPath"
Write-Host "Rollback metadata: $packageInfoPath"
