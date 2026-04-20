# Scripts Guide

This directory contains operational PowerShell scripts. Inherit the repository root guide and follow these local rules.

## Script categories

- Environment/bootstrap:
  - `bootstrap-dev-env.ps1`
  - `fix-text-encoding.ps1`
- Build/package:
  - `build-app-update-package.ps1`
  - `build-installer.ps1`
  - `build-portable.ps1`
  - `build-patch-package.ps1`
  - `publish-local-cloud-service.ps1`
  - `publish-app-update-release.ps1`
  - `new-release-evidence.ps1`
  - `run-test-gates.ps1`
  - `sync-portable-launchers.ps1`
- Diagnostics/inspection:
  - `capture-image-send-diagnostics.ps1`
  - `capture-search-focus-diagnostics.ps1`
  - `check-log-health.ps1`
  - `inspect-phrase-image-state.ps1`

## Expectations

- Keep scripts PowerShell-native and Windows-friendly.
- Validate real paths and script parameters against current repository layout.
- When a script shells out to external tools, keep the dependency obvious in comments or parameter names.

## Safe output boundaries

- Scripts may create, clean, or rebuild repository output folders such as `artifacts` when that is their stated purpose.
- Do not broaden cleanup behavior to user-data paths like `%AppData%\QuickReplyTool`.
- Keep destructive behavior tightly scoped to explicit build/output directories.

## Known tool dependencies

- `build-app-update-package.ps1` depends on `dotnet publish`, archive creation, and manifest/hash generation.
- `build-installer.ps1` depends on `dotnet publish` and Inno Setup `ISCC.exe`.
- `build-portable.ps1` depends on `dotnet publish`.
- `build-patch-package.ps1` depends on `dotnet publish` and archive creation.
- `publish-local-cloud-service.ps1` publishes the bundled `QuickReply.CloudService` runtime that installed/portable/OTA builds now carry for local review and AI-auth fallback.
- `publish-app-update-release.ps1` is the current one-step OTA staging script: build package, stage zip, write public download URL into manifest, and prepare publish-ready files.
- `new-release-evidence.ps1` scaffolds release-evidence folders, checklists, and packaging verification templates.
- `run-test-gates.ps1` is the current local/CI gate entrypoint for build, desktop tests, replay-eval tests, cloud-service tests, packaging smoke, OTA publish staging smoke, log-health, and release-evidence capture.
- `check-log-health.ps1` depends on `QuickReply.ReplayEval` and existing log artifacts or `logs.zip`.
- `bootstrap-dev-env.ps1` installs core tooling, but it does not provision the Python dependencies used by `截图工具源码`.
- Package/build scripts should keep artifact sidecar metadata in sync with the real output file name, `packageKind`, `releaseChannel`, `sha256`, and `sizeBytes`.

## Validation

- For script edits, verify the script's referenced files, output names, and command invocations still match `src/`, `packaging/`, and current build artifacts conventions.
- Packaging scripts must keep the packaged desktop app and bundled `LocalCloudService` directory aligned. Do not ship installer/portable/OTA/patch artifacts that only contain `QuickReplyTool.exe` but not the local cloud-service runtime.
- If a desktop or screenshot-tool bug is fixed in source, verify the corresponding packaged/runtime output is refreshed too; do not close the task while the repo source is new but the actual shipped `QuickReplyTool` or `ScreenshotTool` copy is still old.
- Keep `run-test-gates.ps1` and `.github/workflows/ci.yml` aligned whenever the default validation matrix changes; local gates and CI must not silently diverge.
- If a script changes release/package structure, also review `packaging/` templates and relevant docs.
