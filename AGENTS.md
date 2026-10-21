<!-- Local-only: CloudService and ReplayEval projects removed. -->
# QuickReplyTool Repository Guide

## Purpose

- This repository contains a Windows desktop tool for customer-service reply workflows.
- The product is a **local-only** WPF/.NET 8 app under `src/QuickReply.Desktop`.
- `src/QuickReply.Core` holds shared models and deterministic business logic with no UI.
- Cloud sync, remote knowledge review, OTA self-update, and the former CloudService / ReplayEval projects have been removed.

## Instruction layering

- Keep shared rules here and put narrower, directory-specific guidance in nested `AGENTS.md` files.
- Nested files should add or tighten local rules instead of copying this whole file.

## Working environment

- Primary environment is Windows with PowerShell.
- Prefer PowerShell-friendly commands and Windows paths.
- Assume local development happens from this repository root unless a nested guide says otherwise.

## Source of truth

- Trust the real code and scripts over older prose when they disagree.
- Verified build commands:
  - `dotnet build 回复效率工具.sln`
  - `dotnet build src\QuickReply.Desktop\QuickReply.Desktop.csproj`
  - `dotnet build src\QuickReply.Core\QuickReply.Core.csproj`
- Verified test command:
  - `dotnet test tests\QuickReply.Desktop.Tests\QuickReply.Desktop.Tests.csproj`
- Do not assume `QuickReplyTool.slnx` is the only build entry; the `.sln` is the reliable default.

## Directory boundaries

- Editable areas: `src`, `tests`, `scripts`, `packaging`, `docs`, `截图工具源码`
- Exclude generated output: `**/bin`, `**/obj`, `dist`, `artifacts`, `**/__pycache__`

## Data and runtime paths

- User data lives under `%AppData%\QuickReplyTool`.
- Important runtime files:
  - `%AppData%\QuickReplyTool\quick-reply.db`
  - `%AppData%\QuickReplyTool\logs\conversation-logs.db`
  - `%AppData%\QuickReplyTool\logs\`
  - `%AppData%\QuickReplyTool\ScreenshotTool\lite_shot_settings.json`
  - `%AppData%\QuickReplyTool\feedback\` (local issue packages)

## Validation defaults

- For desktop code changes, prefer targeted `dotnet build`, then the smallest relevant manual flow.
- For test-only changes, keep scope aligned with the touched runtime project.
- For script or packaging changes, validate paths against the current scripts (portable / installer only; no LocalCloudService).

## Cross-cutting expectations

- Keep Chinese documentation natural and concise; preserve code identifiers and commands in English.
- Prefer pure models and UI-agnostic helpers in `src/QuickReply.Core`; keep `QuickReply.Desktop` focused on WPF, Win32, hotkeys, clipboard, screenshot integration, and other Windows-only behavior.
- Do not reintroduce remote cloud sync, knowledge review servers, or OTA update clients unless the product direction explicitly changes.
