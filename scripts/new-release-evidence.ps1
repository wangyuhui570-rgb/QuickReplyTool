param(
    [Parameter(Mandatory = $true)]
    [string]$VersionLabel,
    [string]$OutputRoot = ""
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
        throw 'VersionLabel must contain at least one valid file-name character.'
    }

    return $result
}

function Write-TextFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$Lines
    )

    $content = [string]::Join([Environment]::NewLine, $Lines)
    Set-Content -Path $Path -Value $content -Encoding UTF8
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$safeVersion = New-SafeName -Value $VersionLabel

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputRoot = Join-Path $repoRoot "artifacts\release-evidence\$timestamp-$safeVersion"
}

$outputRootFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
$directoryNames = @(
    'build',
    'automated-tests',
    'manual-regression',
    'packaging-upgrade',
    'screenshots'
)

foreach ($directoryName in $directoryNames) {
    New-Item -ItemType Directory -Path (Join-Path $outputRootFullPath $directoryName) -Force | Out-Null
}

$metadata = [ordered]@{
    versionLabel = $VersionLabel
    createdAt = (Get-Date).ToString('O')
    root = $outputRootFullPath
    expectedEvidence = @(
        'build',
        'automated-tests',
        'manual-regression',
        'packaging-upgrade'
    )
}

$metadataPath = Join-Path $outputRootFullPath 'metadata.json'
$metadata | ConvertTo-Json -Depth 4 | Set-Content -Path $metadataPath -Encoding UTF8

$createdAtDisplay = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'

Write-TextFile -Path (Join-Path $outputRootFullPath 'summary.md') -Lines @(
    '# Release Evidence',
    '',
    "- Version: $VersionLabel",
    "- CreatedAt: $createdAtDisplay",
    "- Root: $outputRootFullPath",
    '',
    '## Required Evidence',
    '',
    '- build/',
    '- automated-tests/',
    '- manual-regression/',
    '- packaging-upgrade/',
    '',
    '## Suggested Contents',
    '',
    '- build/: build commands, build logs, version notes',
    '- automated-tests/: gate logs, .trx files, waivers',
    '- manual-regression/: candidate checklist, screenshots, open risks',
    '- packaging-upgrade/: installer, portable, ota, patch verification plus signature and rollback drill evidence',
    '- screenshots/: optional screenshots or short clips'
)

Write-TextFile -Path (Join-Path $outputRootFullPath 'build\build-notes.md') -Lines @(
    '# Build Notes',
    '',
    '- Version:',
    '- Configuration:',
    '- Build command:',
    '- Result:',
    '- Log path:',
    '- Notes:'
)

Write-TextFile -Path (Join-Path $outputRootFullPath 'automated-tests\automated-test-summary.md') -Lines @(
    '# Automated Test Notes',
    '',
    '- Gate command:',
    '- Desktop tests:',
    '- TRX path:',
    '- Waivers:',
    '- Notes:'
)

Write-TextFile -Path (Join-Path $outputRootFullPath 'manual-regression\manual-regression-report.md') -Lines @(
    '# Manual Regression Notes',
    '',
    '- Operator:',
    '- Executed at:',
    '- Package type:',
    '- Passed scenarios:',
    '- Failed scenarios:',
    '- Risks:',
    '- Evidence paths:'
)

Write-TextFile -Path (Join-Path $outputRootFullPath 'manual-regression\manual-regression-checklist.md') -Lines @(
    '# Manual Regression Checklist',
    '',
    '- Version:',
    '- Package type:',
    '- Operator:',
    '- Executed at:',
    '- Evidence root:',
    '',
    '## Core Scenarios',
    '',
    '1. [ ] Notepad text input and send',
    '   - Preconditions: open Notepad and focus an editable area',
    '   - Expected: text is inserted correctly; send action matches current shortcut',
    '   - Evidence:',
    '2. [ ] IM text send',
    '   - Preconditions: open a real IM chat window and focus the input box',
    '   - Expected: focus returns to the target window and no text is sent to the wrong window',
    '   - Evidence:',
    '3. [ ] Browser text input and send',
    '   - Preconditions: open a regular browser text input',
    '   - Expected: paste remains stable across host controls',
    '   - Evidence:',
    '4. [ ] Enter / Ctrl+Enter / Alt+S send modes',
    '   - Preconditions: switch the setting across all three send shortcuts',
    '   - Expected: each mode uses the current configuration with no stale shortcut behavior',
    '   - Evidence:',
    '5. [ ] Text plus single image send',
    '   - Preconditions: prepare a phrase with one image',
    '   - Expected: text and image order matches the configured mode; failures show a clear message',
    '   - Evidence:',
    '6. [ ] Multiple images or file send',
    '   - Preconditions: prepare multiple files or images in the library',
    '   - Expected: all files reach the target window or missing files are blocked clearly',
    '   - Evidence:',
    '7. [ ] Search panel mode switch and recommendation send',
    '   - Preconditions: ensure enough search samples and recommendation candidates exist',
    '   - Expected: empty state, recommendation state, and mode switching all behave correctly',
    '   - Evidence:',
    '8. [ ] First-run flow and settings persistence',
    '   - Preconditions: use a clean data directory or fresh machine profile',
    '   - Expected: onboarding completes and changed settings persist after restart',
    '   - Evidence:',
    '9. [ ] Issue report local files and upload attempt',
    '   - Preconditions: configure the issue report directory and server if available',
    '   - Expected: local md/json artifacts are created and upload status is readable',
    '   - Evidence:',
    '10. [ ] Manual cloud sync and update check',
    '    - Preconditions: configure integration environment, client id, and access token',
    '    - Expected: timestamps advance and failures keep readable messages',
    '    - Evidence:',
    '11. [ ] Zip patch self-update',
    '    - Preconditions: provide a downloadable patch zip or integration endpoint',
    '    - Expected: plan files, logs, status markers, and restart behavior are correct',
    '    - Evidence:',
    '12. [ ] Screenshot tool auto-start, shutdown, and settings write',
    '    - Preconditions: install Python and screenshot tool dependencies on the machine',
    '    - Expected: startup behavior and persisted settings stay aligned with the app settings',
    '    - Evidence:',
    '',
    '## Summary',
    '',
    '- Passed scenarios:',
    '- Failed scenarios:',
    '- Risks:',
    '- Follow-ups:'
)

Write-TextFile -Path (Join-Path $outputRootFullPath 'packaging-upgrade\installer-release-checklist.md') -Lines @(
    '# Installer Release Checklist',
    '',
    '- Version:',
    '- Operator:',
    '- Executed at:',
    '- Package path:',
    '',
    '## Installer Flow',
    '',
    '1. [ ] Install on a clean machine or clean user profile',
    '   - Expected: setup completes without missing-file or permission errors',
    '   - Evidence:',
    '2. [ ] Launch after install',
    '   - Expected: main window or tray starts normally and no first-run crash appears',
    '   - Evidence:',
    '3. [ ] Verify first-run flow and persisted settings',
    '   - Expected: onboarding works and settings survive a restart',
    '   - Evidence:',
    '4. [ ] Run a Notepad send smoke test',
    '   - Expected: text input and send still work on the installed build',
    '   - Evidence:',
    '5. [ ] Verify issue reporting and update check entry points',
    '   - Expected: both flows can be opened and produce readable status',
    '   - Evidence:',
    '6. [ ] Uninstall or cleanup validation',
    '   - Expected: uninstall path is understandable and leaves no unexpected running process',
    '   - Evidence:',
    '',
    '## Summary',
    '',
    '- Passed items:',
    '- Failed items:',
    '- Risks:',
    '- Follow-ups:'
)

Write-TextFile -Path (Join-Path $outputRootFullPath 'packaging-upgrade\portable-release-checklist.md') -Lines @(
    '# Portable Release Checklist',
    '',
    '- Version:',
    '- Operator:',
    '- Executed at:',
    '- Package path:',
    '',
    '## Portable Flow',
    '',
    '1. [ ] Extract portable zip without administrator rights',
    '   - Expected: package extracts cleanly and launcher files are present',
    '   - Evidence:',
    '2. [ ] Launch through START_HERE.bat or Launch-QuickReplyTool.bat',
    '   - Expected: app starts normally and points to the intended data location',
    '   - Evidence:',
    '3. [ ] Run a Notepad send smoke test',
    '   - Expected: text input and send still work on the portable build',
    '   - Evidence:',
    '4. [ ] Restart and verify settings persistence',
    '   - Expected: changed settings survive close and reopen on the portable package',
    '   - Evidence:',
    '5. [ ] Verify issue reporting, sync, and update entry points',
    '   - Expected: diagnostics and cloud-related actions show readable results',
    '   - Evidence:',
    '6. [ ] Verify screenshots or helper files are discoverable',
    '   - Expected: launcher helpers and optional screenshot integration remain usable',
    '   - Evidence:',
    '',
    '## Summary',
    '',
    '- Passed items:',
    '- Failed items:',
    '- Risks:',
    '- Follow-ups:'
)

Write-TextFile -Path (Join-Path $outputRootFullPath 'packaging-upgrade\ota-release-checklist.md') -Lines @(
    '# OTA Release Checklist',
    '',
    '- Version:',
    '- Operator:',
    '- Executed at:',
    '- OTA package path:',
    '- Upgrade from:',
    '',
    '## OTA Flow',
    '',
    '1. [ ] Confirm the baseline version before checking updates',
    '   - Expected: the pre-update version is recorded and matches the release plan',
    '   - Evidence:',
    '2. [ ] Trigger "Check For Updates Now" against an integration manifest',
    '   - Expected: the app detects the new version and shows release notes',
    '   - Evidence:',
    '3. [ ] Confirm automatic download and package validation',
    '   - Expected: OTA zip downloads successfully and validation errors remain readable',
    '   - Evidence:',
    '4. [ ] Apply the OTA package and inspect generated plan files',
    '   - Expected: updater plan files, logs, and restart command are generated correctly',
    '   - Evidence:',
    '5. [ ] Restart into the updated app',
    '   - Expected: app launches normally after OTA apply and reports the target version',
    '   - Evidence:',
    '6. [ ] Verify user data and settings persistence after OTA',
    '   - Expected: %AppData% data stays intact and changed settings remain available',
    '   - Evidence:',
    '',
    '## Summary',
    '',
    '- Passed items:',
    '- Failed items:',
    '- Risks:',
    '- Follow-ups:'
)

Write-TextFile -Path (Join-Path $outputRootFullPath 'packaging-upgrade\patch-release-checklist.md') -Lines @(
    '# Patch Release Checklist',
    '',
    '- Version:',
    '- Operator:',
    '- Executed at:',
    '- Patch package path:',
    '- Upgrade from:',
    '',
    '## Patch Flow',
    '',
    '1. [ ] Confirm the baseline version before patching',
    '   - Expected: the pre-patch version is recorded and matches the release plan',
    '   - Evidence:',
    '2. [ ] Download or stage the patch zip',
    '   - Expected: patch package is reachable and matches the expected version label',
    '   - Evidence:',
    '3. [ ] Apply the patch and inspect generated plan files',
    '   - Expected: updater plan files and patch metadata are generated correctly',
    '   - Evidence:',
    '4. [ ] Restart into the patched app',
    '   - Expected: app launches normally after patch apply',
    '   - Evidence:',
    '5. [ ] Verify status markers and issue/update surfaces',
    '   - Expected: installed patch status is recorded and update-related views remain readable',
    '   - Evidence:',
    '6. [ ] Run a post-patch send smoke test',
    '   - Expected: core input/send behavior still works after patching',
    '   - Evidence:',
    '',
    '## Summary',
    '',
    '- Passed items:',
    '- Failed items:',
    '- Risks:',
    '- Follow-ups:'
)

Write-TextFile -Path (Join-Path $outputRootFullPath 'packaging-upgrade\signature-verification-checklist.md') -Lines @(
    '# Signature Verification Checklist',
    '',
    '- Version:',
    '- Operator:',
    '- Executed at:',
    '- Signer profile:',
    '',
    '## Signature Smoke',
    '',
    '1. [ ] Generate a signature sidecar for each release artifact',
    '   - Expected: signature json is created for installer/portable/ota/patch or the applicable package types',
    '   - Evidence:',
    '2. [ ] Verify every generated signature sidecar',
    '   - Expected: verification receipt is generated and hash values match the package metadata',
    '   - Evidence:',
    '3. [ ] Record smoke-only signer caveat',
    '   - Expected: release notes or evidence clearly state whether the signer is smoke-only or production-backed',
    '   - Evidence:',
    '',
    '## Summary',
    '',
    '- Passed items:',
    '- Failed items:',
    '- Risks:',
    '- Follow-ups:'
)

Write-TextFile -Path (Join-Path $outputRootFullPath 'packaging-upgrade\rollback-drill-checklist.md') -Lines @(
    '# Rollback Drill Checklist',
    '',
    '- Version:',
    '- Operator:',
    '- Executed at:',
    '- Rollback package path:',
    '',
    '## Rollback Smoke',
    '',
    '1. [ ] Generate rollback package scaffold',
    '   - Expected: rollback zip, rollback-plan.json, and rollback-package-info.json are created',
    '   - Evidence:',
    '2. [ ] Sign and verify the rollback package scaffold',
    '   - Expected: rollback signature sidecar and verification receipt are generated',
    '   - Evidence:',
    '3. [ ] Run rollback drill in dry-run mode',
    '   - Expected: rollback-drill-summary.json and rollback-drill-report.md are produced without touching user data',
    '   - Evidence:',
    '4. [ ] Confirm manual evidence checklist is complete',
    '   - Expected: plan includes backup, restore, version verification, and signoff steps',
    '   - Evidence:',
    '',
    '## Summary',
    '',
    '- Passed items:',
    '- Failed items:',
    '- Risks:',
    '- Follow-ups:'
)

Write-TextFile -Path (Join-Path $outputRootFullPath 'packaging-upgrade\packaging-verification.md') -Lines @(
    '# Packaging And Upgrade Notes',
    '',
    '- Version:',
    '- Operator:',
    '- Executed at:',
    '- Upgrade from:',
    '- Upgrade to:',
    '',
    '## Installer',
    '',
    '- Package path:',
    '- Fresh install result:',
    '- Launch result:',
    '- Uninstall or cleanup notes:',
    '',
    '## Portable',
    '',
    '- Package path:',
    '- Extract result:',
    '- Launch result:',
    '- Data path result:',
    '',
    '## OTA',
    '',
    '- Package path:',
    '- Check-for-updates result:',
    '- Apply result:',
    '- Restart result:',
    '- Data persistence result:',
    '',
    '## Patch',
    '',
    '- Package path:',
    '- Apply result:',
    '- Restart result:',
    '- Status marker result:',
    '',
    '## Signature',
    '',
    '- Signature sidecars:',
    '- Verification receipts:',
    '- Production signer or smoke signer:',
    '',
    '## Rollback',
    '',
    '- Rollback package path:',
    '- Rollback drill result:',
    '- Manual rollback owner:',
    '',
    '## Evidence',
    '',
    '- Logs:',
    '- Screenshots:',
    '- Open risks:'
)

Write-Host "Release evidence scaffold created: $outputRootFullPath"
