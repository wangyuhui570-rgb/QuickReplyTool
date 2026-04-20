# Packaging Guide

This directory contains packaging templates and launcher assets, not core business logic.

## Scope

- `installer/` holds the Inno Setup template.
- `portable/` holds portable-distribution launchers, upgrade helpers, and end-user text files.
- Packaging behavior is driven jointly by these template files and the PowerShell scripts under `scripts/`.

## Change rules

- Keep packaging files aligned with the scripts that feed them.
- When changing filenames, output directories, or launcher behavior, update both the packaging template side and the calling script side together.
- Do not add application logic here that belongs in `src/QuickReply.Desktop`.

## Important couplings

- `packaging\installer\QuickReplyTool.iss` must stay compatible with `scripts\build-installer.ps1` defines and expected output file names.
- `packaging\portable\*` must stay compatible with `scripts\build-portable.ps1` and `scripts\sync-portable-launchers.ps1`.
- Portable launcher/update files should keep user-data assumptions consistent with `%AppData%\QuickReplyTool`.

## Validation

- After packaging-related edits, verify that script parameters, output paths, and copied filenames still line up.
- Keep user-facing packaging text truthful about current behavior and limitations.
