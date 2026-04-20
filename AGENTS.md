# QuickReplyTool Repository Guide

## Purpose

- This repository contains a Windows desktop tool for customer-service reply workflows.
- The main app is a WPF/.NET 8 desktop project under `src/QuickReply.Desktop`.
- `src/QuickReply.CloudService` is the local/server-side cloud skeleton for knowledge review, published-knowledge sync, AI auth, and asynchronous candidate synthesis.
- `src/QuickReply.ReplayEval` is a console-style offline evaluation tool that reuses desktop-side models and services.
- `tests`, `scripts`, `packaging`, `docs`, and `截图工具源码` are support areas with their own local guidance files.

## Instruction layering

- Follow OpenAI Codex `AGENTS.md` guidance: keep shared rules here and put narrower, directory-specific guidance in nested `AGENTS.md` files.
- Nested files should add or tighten local rules instead of copying this whole file.
- Put specialized guidance as close to the working directory as possible.

## Working environment

- Primary environment is Windows with PowerShell.
- Prefer PowerShell-friendly commands and Windows paths.
- Assume local development happens from this repository root unless a nested guide says otherwise.

## Source of truth

- Trust the real code and scripts over older prose when they disagree.
- The build commands verified in this repository are:
  - `dotnet build 回复效率工具.sln`
  - `dotnet build src\QuickReply.Desktop\QuickReply.Desktop.csproj`
  - `dotnet build src\QuickReply.CloudService\QuickReply.CloudService.csproj`
  - `dotnet build src\QuickReply.ReplayEval\QuickReply.ReplayEval.csproj`
- The test commands verified in this repository are:
  - `dotnet test tests\QuickReply.Desktop.Tests\QuickReply.Desktop.Tests.csproj`
  - `dotnet test tests\QuickReply.CloudService.Tests\QuickReply.CloudService.Tests.csproj`
  - `dotnet test tests\QuickReply.ReplayEval.Tests\QuickReply.ReplayEval.Tests.csproj`
- Do not assume `QuickReplyTool.slnx` is the default build entry. In this environment, `dotnet build QuickReplyTool.slnx` is not the reliable default path.

## Directory boundaries

- Treat these as editable source/documentation areas:
  - `src`
  - `tests`
  - `scripts`
  - `packaging`
  - `docs`
  - `截图工具源码`
- Do not place new instructions files in generated or output directories.
- Exclude these directories from normal implementation and documentation work unless the task is explicitly about build outputs:
  - `**/bin`
  - `**/obj`
  - `dist`
  - `artifacts`
  - `**/__pycache__`
  - extracted release folders and `.zip` artifacts

## Data and runtime paths

- User data lives under `%AppData%\QuickReplyTool`.
- Important runtime files include:
  - `%AppData%\QuickReplyTool\quick-reply.db`
  - `%AppData%\QuickReplyTool\logs\conversation-logs.db`
  - `%AppData%\QuickReplyTool\logs\`
  - `%AppData%\QuickReplyTool\ScreenshotTool\lite_shot_settings.json`
- Avoid destructive changes to user data when working on repository code, scripts, or packaging.

## Validation defaults

- For desktop code changes, prefer targeted `dotnet build` first, then the smallest relevant manual flow.
- For cloud-service or shared-contract changes, prefer a targeted build/test of both the touched project and any directly affected desktop/test project.
- For test-only changes, keep the test scope aligned with the touched runtime project; do not broaden unrelated suites without a reason.
- For script or packaging changes, validate the referenced paths, parameters, and output conventions against the current scripts.
- For documentation changes, keep claims aligned with what the code and scripts actually do today.

## Cross-cutting expectations

- Keep Chinese documentation natural and concise; preserve code identifiers and commands in English.
- Reuse existing project structure and service boundaries instead of inventing parallel subsystems.
- If a change touches cloud sync, updates, issue reporting, or release packaging, check whether `docs/` also needs an update.
- AI QA knowledge-base work is cloud-first and asynchronous: candidates are reviewed server-side, only published knowledge is consumed by the desktop client.
- After changing code in a directory tree that has its own `AGENTS.md`, review that local guide and update it if responsibilities, entry points, validation commands, or operator expectations changed.
- If a code change introduces a new long-lived subsystem in a directory tree that does not yet have a meaningful local guide, add or extend the nearest appropriate `AGENTS.md` instead of leaving the documentation drift unresolved.
