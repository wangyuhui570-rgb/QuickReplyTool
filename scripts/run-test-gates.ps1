param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$IncludePackagingChecks,
    [switch]$IncludeInstallerCheck,
    [string]$LogHealthInputPath = '',
    [string]$ReleaseEvidenceVersion = '',
    [string]$ReleaseEvidenceOutputRoot = '',
    [ValidateRange(0, 100)]
    [double]$MinimumSuiteLineCoveragePercent = 15
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputRoot = Join-Path $repoRoot "artifacts\test-gates\$timestamp"
$testResultsRoot = Join-Path $outputRoot 'test-results'
$coverageOutputRoot = Join-Path $outputRoot 'coverage'
$logHealthOutputRoot = ''
$releaseEvidenceRoot = ''
$signingOutputRoot = ''
$rollbackOutputRoot = ''
$solutionFile = Get-ChildItem -Path $repoRoot -File |
    Where-Object { $_.Extension -eq '.sln' } |
    Select-Object -First 1

if (-not $solutionFile) {
    throw "No .sln file was found under $repoRoot"
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $testResultsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $coverageOutputRoot -Force | Out-Null

$executedSteps = [System.Collections.Generic.List[string]]::new()
$skippedSteps = [System.Collections.Generic.List[object]]::new()
$coverageResults = [System.Collections.Generic.List[object]]::new()

function Test-IsccAvailable {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $true
    }

    foreach ($candidate in @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )) {
        if (Test-Path $candidate) {
            return $true
        }
    }

    return $false
}

function New-SafeName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $invalidChars = [System.IO.Path]::GetInvalidFileNameChars()
    $builder = [System.Text.StringBuilder]::new()
    foreach ($character in $Value.ToCharArray()) {
        if ($invalidChars -contains $character) {
            [void]$builder.Append('-')
        }
        else {
            [void]$builder.Append($character)
        }
    }

    $result = $builder.ToString().Trim()
    if ([string]::IsNullOrWhiteSpace($result)) {
        throw 'ReleaseEvidenceVersion must contain at least one valid file-name character.'
    }

    return $result
}

function Add-SkippedStep {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$Reason
    )

    Write-Host "==> $Name"
    Write-Host "    skipped: $Reason"
    $script:skippedSteps.Add([pscustomobject]@{
        name = $Name
        reason = $Reason
    })
}

function Copy-PathIfExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    if (-not (Test-Path $SourcePath)) {
        return
    }

    $destinationParent = Split-Path -Parent $DestinationPath
    if ($destinationParent) {
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    }

    Copy-Item -Path $SourcePath -Destination $DestinationPath -Recurse -Force
}

function Assert-PathExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [string]$Description = 'Required path'
    )

    if (-not (Test-Path $Path)) {
        throw "$Description was not found: $Path"
    }
}

function Read-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    Assert-PathExists -Path $Path -Description 'JSON file'
    return Get-Content -Raw -Path $Path | ConvertFrom-Json
}

function Invoke-SigningSmoke {
    param(
        [Parameter(Mandatory = $true)]
        [string]$NamePrefix,
        [Parameter(Mandatory = $true)]
        [string]$ArtifactPath,
        [Parameter(Mandatory = $true)]
        [string]$MetadataPath
    )

    $signaturePath = Join-Path $signingOutputRoot ("{0}.signature.json" -f $NamePrefix)
    $receiptPath = Join-Path $signingOutputRoot ("{0}.receipt.json" -f $NamePrefix)

    Invoke-LoggedCommand -Name "sign-$NamePrefix" -Command @(
        'powershell.exe',
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $repoRoot 'scripts\sign-release-artifact.ps1'),
        '-ArtifactPath', $ArtifactPath,
        '-MetadataPath', $MetadataPath,
        '-OutputPath', $signaturePath)

    Assert-PathExists -Path $signaturePath -Description "Signature sidecar for $NamePrefix"

    Invoke-LoggedCommand -Name "verify-$NamePrefix-signature" -Command @(
        'powershell.exe',
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $repoRoot 'scripts\verify-release-artifact-signature.ps1'),
        '-SignaturePath', $signaturePath,
        '-ArtifactPath', $ArtifactPath,
        '-MetadataPath', $MetadataPath,
        '-OutputPath', $receiptPath)

    Assert-PathExists -Path $receiptPath -Description "Signature verification receipt for $NamePrefix"

    return [pscustomobject]@{
        signaturePath = $signaturePath
        receiptPath = $receiptPath
    }
}

function Invoke-RollbackSmoke {
    param(
        [Parameter(Mandatory = $true)]
        [string]$NamePrefix,
        [Parameter(Mandatory = $true)]
        [string]$ArtifactPath,
        [Parameter(Mandatory = $true)]
        [string]$MetadataPath,
        [Parameter(Mandatory = $true)]
        [string]$BaselineVersion,
        [Parameter(Mandatory = $true)]
        [string]$RollbackVersion
    )

    $rollbackRoot = Join-Path $rollbackOutputRoot $NamePrefix
    $drillRoot = Join-Path $rollbackRoot 'drill'

    Invoke-LoggedCommand -Name "build-$NamePrefix-rollback" -Command @(
        'powershell.exe',
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $repoRoot 'scripts\build-rollback-package.ps1'),
        '-CurrentArtifactPath', $ArtifactPath,
        '-CurrentMetadataPath', $MetadataPath,
        '-BaselineVersion', $BaselineVersion,
        '-RollbackVersion', $RollbackVersion,
        '-OutputRoot', $rollbackRoot)

    $rollbackZip = Get-ChildItem -Path $rollbackRoot -Filter '*.zip' -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if (-not $rollbackZip) {
        throw "Rollback zip was not generated under $rollbackRoot"
    }

    $rollbackMetadataPath = Join-Path $rollbackRoot 'rollback-package-info.json'
    Assert-PathExists -Path $rollbackMetadataPath -Description "Rollback metadata for $NamePrefix"

    $signatureResult = Invoke-SigningSmoke -NamePrefix "$NamePrefix-rollback" -ArtifactPath $rollbackZip.FullName -MetadataPath $rollbackMetadataPath

    Invoke-LoggedCommand -Name "drill-$NamePrefix-rollback" -Command @(
        'powershell.exe',
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $repoRoot 'scripts\invoke-rollback-drill.ps1'),
        '-RollbackPackagePath', $rollbackZip.FullName,
        '-CurrentArtifactPath', $ArtifactPath,
        '-CurrentMetadataPath', $MetadataPath,
        '-SignaturePath', $signatureResult.signaturePath,
        '-OutputRoot', $drillRoot)

    Assert-PathExists -Path (Join-Path $drillRoot 'rollback-drill-summary.json') -Description "Rollback drill summary for $NamePrefix"
    Assert-PathExists -Path (Join-Path $drillRoot 'rollback-drill-report.md') -Description "Rollback drill report for $NamePrefix"
}

function Get-CoverageReportFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    if (-not (Test-Path $RootPath)) {
        return @()
    }

    return @(Get-ChildItem -Path $RootPath -Filter 'coverage.cobertura.xml' -Recurse -File)
}

function Convert-RateToPercent {
    param(
        [Parameter(Mandatory = $true)]
        [double]$Rate
    )

    return [math]::Round($Rate * 100, 2)
}

function Register-CoverageResult {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SuiteName,
        [Parameter(Mandatory = $true)]
        [string]$CoverageReportPath
    )

    Assert-PathExists -Path $CoverageReportPath -Description 'Coverage report'

    [xml]$coverageXml = Get-Content -Raw -Path $CoverageReportPath
    $coverageNode = $coverageXml.coverage
    if (-not $coverageNode) {
        throw "Coverage report for $SuiteName is missing the <coverage> root node: $CoverageReportPath"
    }

    $copiedReportPath = Join-Path $coverageOutputRoot "$SuiteName.coverage.cobertura.xml"
    Copy-PathIfExists -SourcePath $CoverageReportPath -DestinationPath $copiedReportPath

    $result = [pscustomobject][ordered]@{
        suite                = $SuiteName
        reportPath           = $copiedReportPath
        lineCoveragePercent  = Convert-RateToPercent -Rate ([double]$coverageNode.'line-rate')
        branchCoveragePercent = Convert-RateToPercent -Rate ([double]$coverageNode.'branch-rate')
        linesCovered         = [int64]$coverageNode.'lines-covered'
        linesValid           = [int64]$coverageNode.'lines-valid'
        branchesCovered      = [int64]$coverageNode.'branches-covered'
        branchesValid        = [int64]$coverageNode.'branches-valid'
    }

    $script:coverageResults.Add($result)
    return $result
}

function Initialize-ReleaseEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [string]$VersionLabel,
        [string]$RequestedOutputRoot
    )

    $targetRoot = $RequestedOutputRoot
    if ([string]::IsNullOrWhiteSpace($targetRoot)) {
        $safeVersion = New-SafeName -Value $VersionLabel
        $targetRoot = Join-Path $repoRoot "artifacts\release-evidence\$timestamp-$safeVersion"
    }

    $targetRoot = [System.IO.Path]::GetFullPath($targetRoot)

    $command = @(
        'powershell.exe',
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $repoRoot 'scripts\new-release-evidence.ps1'),
        '-VersionLabel', $VersionLabel,
        '-OutputRoot', $targetRoot
    )

    $logPath = Join-Path $outputRoot 'scaffold-release-evidence.log'
    Write-Host '==> scaffold-release-evidence'
    Write-Host "    $($command -join ' ')"

    & $command[0] $command[1..($command.Length - 1)] 2>&1 | Tee-Object -FilePath $logPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Step 'scaffold-release-evidence' failed. See $logPath"
    }

    $script:executedSteps.Add('scaffold-release-evidence')
    return $targetRoot
}

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string[]]$Command
    )

    $logPath = Join-Path $outputRoot "$Name.log"
    Write-Host "==> $Name"
    Write-Host "    $($Command -join ' ')"

    & $Command[0] $Command[1..($Command.Length - 1)] 2>&1 | Tee-Object -FilePath $logPath
    if ($LASTEXITCODE -ne 0) {
        throw "Step '$Name' failed. See $logPath"
    }

    $script:executedSteps.Add($Name)
}

function Invoke-TestWithCoverage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,
        [Parameter(Mandatory = $true)]
        [string]$TrxFileName,
        [Parameter(Mandatory = $true)]
        [string]$SuiteName
    )

    $coverageReportsBefore = @(Get-CoverageReportFiles -RootPath $testResultsRoot | ForEach-Object { $_.FullName })

    Invoke-LoggedCommand -Name $Name -Command @(
        'dotnet',
        'test',
        $ProjectPath,
        '-c', $Configuration,
        '--no-build',
        '--logger', "trx;LogFileName=$TrxFileName",
        '--results-directory', $testResultsRoot,
        '--collect', 'XPlat Code Coverage',
        '--',
        'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura'
    )

    $coverageReportsAfter = @(Get-CoverageReportFiles -RootPath $testResultsRoot)
    $newCoverageReports = @(
        $coverageReportsAfter |
            Where-Object { $coverageReportsBefore -notcontains $_.FullName } |
            Sort-Object LastWriteTimeUtc -Descending
    )

    $selectedCoverageReport = $null
    if ($newCoverageReports.Count -gt 0) {
        $selectedCoverageReport = $newCoverageReports[0].FullName
    }
    else {
        $fallbackCoverageReport = $coverageReportsAfter | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($fallbackCoverageReport) {
            $selectedCoverageReport = $fallbackCoverageReport.FullName
        }
    }

    if ([string]::IsNullOrWhiteSpace($selectedCoverageReport)) {
        throw "No code coverage report was generated for $SuiteName under $testResultsRoot"
    }

    Register-CoverageResult -SuiteName $SuiteName -CoverageReportPath $selectedCoverageReport | Out-Null
}

Push-Location $repoRoot
try {
    Invoke-LoggedCommand -Name 'build-solution' -Command @('dotnet', 'build', $solutionFile.Name, '-c', $Configuration)
    Invoke-TestWithCoverage -Name 'test-desktop' -ProjectPath 'tests\QuickReply.Desktop.Tests\QuickReply.Desktop.Tests.csproj' -TrxFileName 'QuickReply.Desktop.Tests.trx' -SuiteName 'QuickReply.Desktop.Tests'
    Invoke-TestWithCoverage -Name 'test-replayeval' -ProjectPath 'tests\QuickReply.ReplayEval.Tests\QuickReply.ReplayEval.Tests.csproj' -TrxFileName 'QuickReply.ReplayEval.Tests.trx' -SuiteName 'QuickReply.ReplayEval.Tests'
    Invoke-TestWithCoverage -Name 'test-cloudservice' -ProjectPath 'tests\QuickReply.CloudService.Tests\QuickReply.CloudService.Tests.csproj' -TrxFileName 'QuickReply.CloudService.Tests.trx' -SuiteName 'QuickReply.CloudService.Tests'

    foreach ($coverageResult in $coverageResults) {
        if ($coverageResult.lineCoveragePercent -lt $MinimumSuiteLineCoveragePercent) {
            throw "Coverage threshold failed for $($coverageResult.suite): $($coverageResult.lineCoveragePercent)% < $MinimumSuiteLineCoveragePercent%"
        }
    }

    $aggregateLinesCovered = [int64](($coverageResults | Measure-Object -Property linesCovered -Sum).Sum)
    $aggregateLinesValid = [int64](($coverageResults | Measure-Object -Property linesValid -Sum).Sum)
    $aggregateBranchesCovered = [int64](($coverageResults | Measure-Object -Property branchesCovered -Sum).Sum)
    $aggregateBranchesValid = [int64](($coverageResults | Measure-Object -Property branchesValid -Sum).Sum)
    $aggregateLineCoveragePercent = if ($aggregateLinesValid -gt 0) {
        [math]::Round(($aggregateLinesCovered / $aggregateLinesValid) * 100, 2)
    }
    else {
        0
    }
    $aggregateBranchCoveragePercent = if ($aggregateBranchesValid -gt 0) {
        [math]::Round(($aggregateBranchesCovered / $aggregateBranchesValid) * 100, 2)
    }
    else {
        0
    }

    $coverageSummary = [ordered]@{
        generatedAt                     = (Get-Date).ToString('O')
        minimumSuiteLineCoveragePercent = $MinimumSuiteLineCoveragePercent
        suites                          = @($coverageResults)
        aggregate                       = [ordered]@{
            lineCoveragePercent   = $aggregateLineCoveragePercent
            branchCoveragePercent = $aggregateBranchCoveragePercent
            linesCovered          = $aggregateLinesCovered
            linesValid            = $aggregateLinesValid
            branchesCovered       = $aggregateBranchesCovered
            branchesValid         = $aggregateBranchesValid
        }
    }
    $coverageSummaryPath = Join-Path $coverageOutputRoot 'summary.json'
    $coverageSummary | ConvertTo-Json -Depth 6 | Set-Content -Path $coverageSummaryPath -Encoding UTF8

    if (-not [string]::IsNullOrWhiteSpace($LogHealthInputPath)) {
        $logHealthOutputRoot = Join-Path $outputRoot 'log-health'
        Invoke-LoggedCommand -Name 'check-log-health' -Command @(
            'powershell.exe',
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $repoRoot 'scripts\check-log-health.ps1'),
            '-InputPath', $LogHealthInputPath,
            '-Configuration', $Configuration,
            '-OutputRoot', $logHealthOutputRoot,
            '-NoBuild')
    }

    if ($IncludePackagingChecks) {
        $signingOutputRoot = Join-Path $outputRoot 'signing-smoke'
        $rollbackOutputRoot = Join-Path $outputRoot 'rollback-smoke'
        New-Item -ItemType Directory -Path $signingOutputRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $rollbackOutputRoot -Force | Out-Null

        $portableRoot = Join-Path $outputRoot 'portable-smoke'
        Invoke-LoggedCommand -Name 'package-portable' -Command @(
            'powershell.exe',
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $repoRoot 'scripts\build-portable.ps1'),
            '-Configuration', $Configuration,
            '-SelfContained:$false',
            '-ReleaseChannel', 'ci-smoke',
            '-OutputRoot', $portableRoot)
        Assert-PathExists -Path (Join-Path $portableRoot 'app\QuickReplyTool.exe') -Description 'Portable app executable'
        Assert-PathExists -Path (Join-Path $portableRoot 'app\LocalCloudService\QuickReply.CloudService.exe') -Description 'Portable local cloud-service executable'
        Assert-PathExists -Path (Join-Path $portableRoot 'START_HERE.bat') -Description 'Portable launcher'
        Assert-PathExists -Path (Join-Path $portableRoot 'Launch-QuickReplyTool.bat') -Description 'Portable helper launcher'
        Assert-PathExists -Path "${portableRoot}.zip" -Description 'Portable zip artifact'
        $portableMetadata = Read-JsonFile -Path (Join-Path $portableRoot 'portable-package-info.json')
        if ($portableMetadata.packageKind -ne 'portable_zip') {
            throw "Portable metadata packageKind mismatch: $($portableMetadata.packageKind)"
        }
        if ($portableMetadata.releaseChannel -ne 'ci-smoke') {
            throw "Portable metadata releaseChannel mismatch: $($portableMetadata.releaseChannel)"
        }
        if ([string]::IsNullOrWhiteSpace($portableMetadata.sha256)) {
            throw 'Portable metadata sha256 must not be empty.'
        }
        if ([int64]$portableMetadata.sizeBytes -le 0) {
            throw "Portable metadata sizeBytes mismatch: $($portableMetadata.sizeBytes)"
        }
        Invoke-SigningSmoke -NamePrefix 'portable' -ArtifactPath "${portableRoot}.zip" -MetadataPath (Join-Path $portableRoot 'portable-package-info.json') | Out-Null

        $patchRoot = Join-Path $outputRoot 'patch-smoke'
        Invoke-LoggedCommand -Name 'package-patch' -Command @(
            'powershell.exe',
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $repoRoot 'scripts\build-patch-package.ps1'),
            '-Configuration', $Configuration,
            '-SelfContained:$false',
            '-PatchVersion', '0.0.0-smoke',
            '-ReleaseChannel', 'ci-smoke',
            '-ReleaseNotes', 'smoke test package',
            '-OutputRoot', $patchRoot)
        Assert-PathExists -Path (Join-Path $patchRoot 'package\app\QuickReplyTool.exe') -Description 'Patch app executable'
        Assert-PathExists -Path (Join-Path $patchRoot 'package\app\LocalCloudService\QuickReply.CloudService.exe') -Description 'Patch local cloud-service executable'
        Assert-PathExists -Path (Join-Path $patchRoot 'QuickReplyTool-Patch-v0.0.0-smoke.zip') -Description 'Patch zip artifact'
        $patchMetadata = Read-JsonFile -Path (Join-Path $patchRoot 'package\patch-info.json')
        if ($patchMetadata.patchVersion -ne '0.0.0-smoke') {
            throw "Patch metadata patchVersion mismatch: $($patchMetadata.patchVersion)"
        }
        if ($patchMetadata.releaseChannel -ne 'ci-smoke') {
            throw "Patch metadata releaseChannel mismatch: $($patchMetadata.releaseChannel)"
        }
        $patchPackageMetadata = Read-JsonFile -Path (Join-Path $patchRoot 'patch-package-info.json')
        if ($patchPackageMetadata.packageKind -ne 'issue_patch_zip') {
            throw "Patch package metadata packageKind mismatch: $($patchPackageMetadata.packageKind)"
        }
        if ($patchPackageMetadata.releaseChannel -ne 'ci-smoke') {
            throw "Patch package metadata releaseChannel mismatch: $($patchPackageMetadata.releaseChannel)"
        }
        if ([string]::IsNullOrWhiteSpace($patchPackageMetadata.sha256)) {
            throw 'Patch package metadata sha256 must not be empty.'
        }
        if ([int64]$patchPackageMetadata.sizeBytes -le 0) {
            throw "Patch package metadata sizeBytes mismatch: $($patchPackageMetadata.sizeBytes)"
        }
        Invoke-SigningSmoke -NamePrefix 'patch' -ArtifactPath (Join-Path $patchRoot 'QuickReplyTool-Patch-v0.0.0-smoke.zip') -MetadataPath (Join-Path $patchRoot 'patch-package-info.json') | Out-Null
        Invoke-RollbackSmoke -NamePrefix 'patch' -ArtifactPath (Join-Path $patchRoot 'QuickReplyTool-Patch-v0.0.0-smoke.zip') -MetadataPath (Join-Path $patchRoot 'patch-package-info.json') -BaselineVersion '0.0.0-smoke-baseline' -RollbackVersion '0.0.0-smoke-rollback' | Out-Null

        $otaRoot = Join-Path $outputRoot 'app-update-smoke'
        Invoke-LoggedCommand -Name 'package-app-update' -Command @(
            'powershell.exe',
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $repoRoot 'scripts\build-app-update-package.ps1'),
            '-Configuration', $Configuration,
            '-SelfContained:$false',
            '-AppVersion', '0.0.0-smoke',
            '-ReleaseChannel', 'ci-smoke',
            '-ReleaseNotes', 'smoke test ota package',
            '-OutputRoot', $otaRoot)
        Assert-PathExists -Path (Join-Path $otaRoot 'package\app\QuickReplyTool.exe') -Description 'App update executable'
        Assert-PathExists -Path (Join-Path $otaRoot 'package\app\LocalCloudService\QuickReply.CloudService.exe') -Description 'App update local cloud-service executable'
        Assert-PathExists -Path (Join-Path $otaRoot 'package\app-update-info.json') -Description 'App update metadata'
        Assert-PathExists -Path (Join-Path $otaRoot 'release-manifest.snippet.json') -Description 'App update release manifest snippet'
        Assert-PathExists -Path (Join-Path $otaRoot 'QuickReplyTool-AppUpdate-v0.0.0-smoke.zip') -Description 'App update zip artifact'
        $otaMetadata = Read-JsonFile -Path (Join-Path $otaRoot 'package\app-update-info.json')
        if ($otaMetadata.packageKind -ne 'zip_ota') {
            throw "App update metadata packageKind mismatch: $($otaMetadata.packageKind)"
        }
        $otaManifest = Read-JsonFile -Path (Join-Path $otaRoot 'release-manifest.snippet.json')
        if ($otaManifest.latestAppPackageKind -ne 'zip_ota') {
            throw "App update manifest latestAppPackageKind mismatch: $($otaManifest.latestAppPackageKind)"
        }
        if ($otaManifest.latestAppReleaseChannel -ne 'ci-smoke') {
            throw "App update manifest latestAppReleaseChannel mismatch: $($otaManifest.latestAppReleaseChannel)"
        }
        if ([string]::IsNullOrWhiteSpace($otaManifest.latestAppSha256)) {
            throw 'App update manifest latestAppSha256 must not be empty.'
        }
        if ([int64]$otaManifest.latestAppSizeBytes -le 0) {
            throw "App update manifest latestAppSizeBytes mismatch: $($otaManifest.latestAppSizeBytes)"
        }
        Invoke-SigningSmoke -NamePrefix 'app-update' -ArtifactPath (Join-Path $otaRoot 'QuickReplyTool-AppUpdate-v0.0.0-smoke.zip') -MetadataPath (Join-Path $otaRoot 'package\app-update-info.json') | Out-Null
        Invoke-RollbackSmoke -NamePrefix 'app-update' -ArtifactPath (Join-Path $otaRoot 'QuickReplyTool-AppUpdate-v0.0.0-smoke.zip') -MetadataPath (Join-Path $otaRoot 'package\app-update-info.json') -BaselineVersion '0.0.0-smoke-baseline' -RollbackVersion '0.0.0-smoke-rollback' | Out-Null

        $otaPublishRoot = Join-Path $outputRoot 'ota-publish-smoke'
        Invoke-LoggedCommand -Name 'publish-app-update-release' -Command @(
            'powershell.exe',
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $repoRoot 'scripts\publish-app-update-release.ps1'),
            '-Configuration', $Configuration,
            '-Runtime', 'win-x64',
            '-SelfContained:$false',
            '-PackageOutputRoot', $otaRoot,
            '-PublishRoot', $otaPublishRoot,
            '-AppVersion', '0.0.0-smoke',
            '-ReleaseChannel', 'ci-smoke',
            '-ReleaseNotes', 'smoke test ota package',
            '-PublicDownloadBaseUrl', 'https://ci.example.test/downloads/ota')
        Assert-PathExists -Path (Join-Path $otaPublishRoot 'downloads\ota\QuickReplyTool-AppUpdate-v0.0.0-smoke.zip') -Description 'Published OTA zip'
        $publishedManifest = Read-JsonFile -Path (Join-Path $otaPublishRoot 'update-manifest.json')
        if ($publishedManifest.latestAppDownloadUrl -ne 'https://ci.example.test/downloads/ota/QuickReplyTool-AppUpdate-v0.0.0-smoke.zip') {
            throw "Published OTA manifest downloadUrl mismatch: $($publishedManifest.latestAppDownloadUrl)"
        }

        if ($IncludeInstallerCheck) {
            if (Test-IsccAvailable) {
                $installerRoot = Join-Path $outputRoot 'installer-smoke'
                Invoke-LoggedCommand -Name 'package-installer' -Command @(
                    'powershell.exe',
                    '-NoProfile',
                    '-ExecutionPolicy', 'Bypass',
                    '-File', (Join-Path $repoRoot 'scripts\build-installer.ps1'),
                    '-Configuration', $Configuration,
                    '-SelfContained:$false',
                    '-ReleaseChannel', 'ci-smoke',
                    '-OutputRoot', $installerRoot)
                Assert-PathExists -Path (Join-Path $installerRoot 'app\QuickReplyTool.exe') -Description 'Installer publish executable'
                Assert-PathExists -Path (Join-Path $installerRoot 'app\LocalCloudService\QuickReply.CloudService.exe') -Description 'Installer local cloud-service executable'
                $installerExe = Get-ChildItem -Path (Join-Path $installerRoot 'installer') -Filter *.exe -File | Select-Object -First 1
                if (-not $installerExe) {
                    throw "Installer executable was not generated under $installerRoot\installer"
                }
                $installerMetadata = Read-JsonFile -Path (Join-Path $installerRoot 'installer-package-info.json')
                if ($installerMetadata.packageKind -ne 'installer_exe') {
                    throw "Installer metadata packageKind mismatch: $($installerMetadata.packageKind)"
                }
                if ($installerMetadata.releaseChannel -ne 'ci-smoke') {
                    throw "Installer metadata releaseChannel mismatch: $($installerMetadata.releaseChannel)"
                }
                if ([string]::IsNullOrWhiteSpace($installerMetadata.sha256)) {
                    throw 'Installer metadata sha256 must not be empty.'
                }
                if ([int64]$installerMetadata.sizeBytes -le 0) {
                    throw "Installer metadata sizeBytes mismatch: $($installerMetadata.sizeBytes)"
                }
                Invoke-SigningSmoke -NamePrefix 'installer' -ArtifactPath $installerExe.FullName -MetadataPath (Join-Path $installerRoot 'installer-package-info.json') | Out-Null
            }
            else {
                Add-SkippedStep -Name 'package-installer' -Reason 'ISCC.exe was not found on this machine.'
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($ReleaseEvidenceVersion)) {
        $releaseEvidenceRoot = Initialize-ReleaseEvidence -VersionLabel $ReleaseEvidenceVersion -RequestedOutputRoot $ReleaseEvidenceOutputRoot
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'build-solution.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'build\build-solution.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'test-desktop.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'automated-tests\test-desktop.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'test-replayeval.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'automated-tests\test-replayeval.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'test-cloudservice.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'automated-tests\test-cloudservice.log')
        Copy-PathIfExists -SourcePath $testResultsRoot -DestinationPath (Join-Path $releaseEvidenceRoot 'automated-tests\test-results')
        Copy-PathIfExists -SourcePath $coverageOutputRoot -DestinationPath (Join-Path $releaseEvidenceRoot 'automated-tests\coverage')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'check-log-health.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'automated-tests\check-log-health.log')
        Copy-PathIfExists -SourcePath $logHealthOutputRoot -DestinationPath (Join-Path $releaseEvidenceRoot 'automated-tests\log-health')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'package-portable.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\package-portable.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'package-patch.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\package-patch.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'package-app-update.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\package-app-update.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'package-installer.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\package-installer.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'sign-portable.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\sign-portable.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'verify-portable-signature.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\verify-portable-signature.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'sign-patch.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\sign-patch.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'verify-patch-signature.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\verify-patch-signature.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'build-patch-rollback.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\build-patch-rollback.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'sign-patch-rollback.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\sign-patch-rollback.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'verify-patch-rollback-signature.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\verify-patch-rollback-signature.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'drill-patch-rollback.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\drill-patch-rollback.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'sign-app-update.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\sign-app-update.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'verify-app-update-signature.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\verify-app-update-signature.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'build-app-update-rollback.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\build-app-update-rollback.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'sign-app-update-rollback.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\sign-app-update-rollback.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'verify-app-update-rollback-signature.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\verify-app-update-rollback-signature.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'drill-app-update-rollback.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\drill-app-update-rollback.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'sign-installer.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\sign-installer.log')
        Copy-PathIfExists -SourcePath (Join-Path $outputRoot 'verify-installer-signature.log') -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\verify-installer-signature.log')
        Copy-PathIfExists -SourcePath $signingOutputRoot -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\signing-smoke')
        Copy-PathIfExists -SourcePath $rollbackOutputRoot -DestinationPath (Join-Path $releaseEvidenceRoot 'packaging-upgrade\rollback-smoke')
    }

    $summary = [ordered]@{
        generatedAt            = (Get-Date).ToString('O')
        configuration          = $Configuration
        solution               = $solutionFile.Name
        outputRoot             = $outputRoot
        testResultsRoot        = $testResultsRoot
        coverageOutputRoot     = $coverageOutputRoot
        coverageSummaryPath    = $coverageSummaryPath
        minimumSuiteLineCoveragePercent = $MinimumSuiteLineCoveragePercent
        coverage               = @($coverageResults)
        coverageAggregate      = $coverageSummary['aggregate']
        signingOutputRoot      = $signingOutputRoot
        rollbackOutputRoot     = $rollbackOutputRoot
        logHealthInputPath     = $LogHealthInputPath
        logHealthOutputRoot    = $logHealthOutputRoot
        releaseEvidenceRoot    = $releaseEvidenceRoot
        includePackagingChecks = [bool]$IncludePackagingChecks
        includeInstallerCheck  = [bool]$IncludeInstallerCheck
        executedSteps          = @($executedSteps)
        skippedSteps           = @($skippedSteps)
    }

    $summaryPath = Join-Path $outputRoot 'summary.json'
    $summary | ConvertTo-Json -Depth 4 | Set-Content -Path $summaryPath -Encoding UTF8
    if (-not [string]::IsNullOrWhiteSpace($releaseEvidenceRoot)) {
        Copy-PathIfExists -SourcePath $summaryPath -DestinationPath (Join-Path $releaseEvidenceRoot 'automated-tests\gate-summary.json')
    }
    Write-Host ""
    Write-Host "All test gates passed."
    Write-Host "Logs: $outputRoot"
    if (-not [string]::IsNullOrWhiteSpace($releaseEvidenceRoot)) {
        Write-Host "Release evidence: $releaseEvidenceRoot"
    }
}
finally {
    Pop-Location
}
