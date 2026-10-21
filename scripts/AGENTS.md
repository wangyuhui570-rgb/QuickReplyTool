# Scripts Guide

## Purpose

PowerShell scripts for local packaging and release evidence on Windows.

## Primary scripts

- `build-portable.ps1` — portable zip of the desktop app
- `build-installer.ps1` — Inno Setup installer
- `run-test-gates.ps1` — build + Desktop.Tests (+ optional packaging checks)
- Signing / rollback helpers remain for portable and installer artifacts

## Removed

- `publish-local-cloud-service.ps1`
- `build-app-update-package.ps1` / `publish-app-update-release.ps1`
- `build-patch-package.ps1`
- `check-log-health.ps1` depended on ReplayEval and is retired

Do not package a `LocalCloudService` folder anymore.
