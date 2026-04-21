param(
    [string]$RollbackPackagePath = '',
    [string]$RollbackPlanPath = '',
    [string]$CurrentArtifactPath = '',
    [string]$CurrentMetadataPath = '',
    [string]$SignaturePath = '',
    [string]$SigningSecret = '',
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'release-safety-common.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'

if ([string]::IsNullOrWhiteSpace($RollbackPackagePath) -and [string]::IsNullOrWhiteSpace($RollbackPlanPath)) {
    throw 'Either RollbackPackagePath or RollbackPlanPath must be supplied.'
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\rollback-drills\$timestamp"
}

$outputRootFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $outputRootFullPath -Force | Out-Null

$packageFullPath = ''
$packageMetadataPath = ''
$expandedRoot = ''

if (-not [string]::IsNullOrWhiteSpace($RollbackPackagePath)) {
    $packageFullPath = Get-ReleaseSafetyFullPath -Path $RollbackPackagePath -Description 'Rollback package'
    $packageDirectory = Split-Path -Parent $packageFullPath
    $packageMetadataPath = Join-Path $packageDirectory 'rollback-package-info.json'
    $expandedRoot = Join-Path $outputRootFullPath 'expanded-package'
    if (Test-Path -LiteralPath $expandedRoot) {
        Remove-Item -Path $expandedRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $expandedRoot -Force | Out-Null
    Expand-Archive -Path $packageFullPath -DestinationPath $expandedRoot -Force

    if ([string]::IsNullOrWhiteSpace($RollbackPlanPath)) {
        $RollbackPlanPath = Join-Path $expandedRoot 'rollback-plan.json'
    }
}

$planFullPath = Get-ReleaseSafetyFullPath -Path $RollbackPlanPath -Description 'Rollback plan'
$plan = Read-ReleaseSafetyJsonFile -Path $planFullPath
$planValidation = Test-ReleaseSafetyRollbackPlan -Plan $plan

if (-not $planValidation.isValid) {
    throw ('Rollback plan validation failed: ' + ($planValidation.errors -join '; '))
}

$signatureResult = [ordered]@{
    status = 'skipped'
    receiptPath = ''
}

if (-not [string]::IsNullOrWhiteSpace($SignaturePath)) {
    $signatureFullPath = Get-ReleaseSafetyFullPath -Path $SignaturePath -Description 'Rollback package signature'
    $rollbackPackageMetadataPath = ''
    if (-not [string]::IsNullOrWhiteSpace($packageMetadataPath) -and (Test-Path -LiteralPath $packageMetadataPath)) {
        $rollbackPackageMetadataPath = $packageMetadataPath
    }

    if ([string]::IsNullOrWhiteSpace($packageFullPath)) {
        throw 'Signature verification for rollback drill requires RollbackPackagePath.'
    }

    $secret = if ([string]::IsNullOrWhiteSpace($SigningSecret)) { Get-ReleaseSafetyDefaultSigningSecret } else { $SigningSecret }
    $signatureRecord = Read-ReleaseSafetyJsonFile -Path $signatureFullPath
    $signatureValidation = Test-ReleaseSafetySignatureRecord -SignatureRecord $signatureRecord -ArtifactPath $packageFullPath -MetadataPath $rollbackPackageMetadataPath -SigningSecret $secret
    if (-not $signatureValidation.isValid) {
        throw ('Rollback package signature verification failed: ' + ($signatureValidation.errors -join '; '))
    }

    $receiptPath = Join-Path $outputRootFullPath 'rollback-signature-verification.receipt.json'
    $receipt = [ordered]@{
        schemaVersion = 'quickreply.rollback-signature-receipt.v1'
        validatedAt = (Get-Date).ToString('O')
        smokeOnly = $true
        status = 'verified'
        signaturePath = $signatureFullPath
        artifactPath = $packageFullPath
        metadataPath = $rollbackPackageMetadataPath
    }
    $receipt | ConvertTo-Json -Depth 6 | Set-Content -Path $receiptPath -Encoding UTF8
    $signatureResult = [ordered]@{
        status = 'verified'
        receiptPath = $receiptPath
    }
}

$artifactPlanMatch = [ordered]@{
    status = 'skipped'
    details = 'Current artifact was not supplied.'
}

if (-not [string]::IsNullOrWhiteSpace($CurrentArtifactPath)) {
    $currentArtifactFullPath = Get-ReleaseSafetyFullPath -Path $CurrentArtifactPath -Description 'Current target artifact'
    $currentArtifactInfo = Get-Item -LiteralPath $currentArtifactFullPath
    $currentArtifactHash = Get-ReleaseSafetyFileSha256 -Path $currentArtifactFullPath

    if ($plan.targetPackage.artifactFileName -ne $currentArtifactInfo.Name -or $plan.targetPackage.artifactSha256 -ne $currentArtifactHash) {
        throw "Rollback plan target artifact mismatch: expected $($plan.targetPackage.artifactFileName) / $($plan.targetPackage.artifactSha256)"
    }

    $artifactPlanMatch = [ordered]@{
        status = 'verified'
        details = 'Current artifact matches rollback plan target snapshot.'
    }
}

$metadataPlanMatch = [ordered]@{
    status = 'skipped'
    details = 'Current package metadata was not supplied.'
}

if (-not [string]::IsNullOrWhiteSpace($CurrentMetadataPath)) {
    $currentMetadataFullPath = Get-ReleaseSafetyFullPath -Path $CurrentMetadataPath -Description 'Current target metadata'
    $currentMetadataInfo = Get-Item -LiteralPath $currentMetadataFullPath
    $currentMetadataHash = Get-ReleaseSafetyFileSha256 -Path $currentMetadataFullPath

    if ($plan.targetPackage.metadataFileName -ne $currentMetadataInfo.Name -or $plan.targetPackage.metadataSha256 -ne $currentMetadataHash) {
        throw "Rollback plan target metadata mismatch: expected $($plan.targetPackage.metadataFileName) / $($plan.targetPackage.metadataSha256)"
    }

    $metadataPlanMatch = [ordered]@{
        status = 'verified'
        details = 'Current package metadata matches rollback plan target snapshot.'
    }
}

$drillSteps = @(
    [ordered]@{ id = 'plan-structure'; status = 'verified'; details = 'Rollback plan schema and required steps are present.' },
    [ordered]@{ id = 'signature'; status = $signatureResult.status; details = 'Rollback package signature validation.' },
    [ordered]@{ id = 'target-artifact'; status = $artifactPlanMatch.status; details = $artifactPlanMatch.details },
    [ordered]@{ id = 'target-metadata'; status = $metadataPlanMatch.status; details = $metadataPlanMatch.details },
    [ordered]@{ id = 'dry-run-restore-sequence'; status = 'verified'; details = 'Backup, restore, verify, and signoff steps are present for manual execution.' }
)

$summary = [ordered]@{
    schemaVersion = 'quickreply.rollback-drill-summary.v1'
    generatedAt = (Get-Date).ToString('O')
    smokeOnly = $true
    rollbackPlanPath = $planFullPath
    rollbackPackagePath = $packageFullPath
    expandedPackageRoot = $expandedRoot
    signature = $signatureResult
    steps = $drillSteps
    result = 'passed'
}

$summaryPath = Join-Path $outputRootFullPath 'rollback-drill-summary.json'
$summary | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryPath -Encoding UTF8

$reportPath = Join-Path $outputRootFullPath 'rollback-drill-report.md'
$reportLines = @(
    '# Rollback Drill Summary',
    '',
    "- Rollback plan: $planFullPath",
    "- Rollback package: $packageFullPath",
    "- Signature status: $($signatureResult.status)",
    "- Result: passed",
    '',
    '## Verified Steps',
    ''
)

foreach ($step in $drillSteps) {
    $reportLines += "- $($step.id): $($step.status) - $($step.details)"
}

$reportLines | Set-Content -Path $reportPath -Encoding UTF8

Write-Host "Rollback drill completed: $summaryPath"
