# QuickReply.ReplayEval Guide

This project is an offline evaluation tool layered on top of desktop-side code.

## Role

- `QuickReply.ReplayEval` is a non-UI .NET project for replaying historical conversation data and writing evaluation artifacts.
- It references `..\QuickReply.Core\QuickReply.Core.csproj` and keeps replay-only query/retrieval logic inside this project so the tool can run without the desktop host.

## Data dependencies

- The default input database is `%AppData%\QuickReplyTool\logs\conversation-logs.db`.
- Output artifacts are written under `artifacts\replay-eval\...` unless overridden by arguments.
- Keep this tool safe for offline analysis; avoid turning it into a second runtime app.

## Change rules

- Preserve the console/offline nature of the project.
- Prefer adding evaluation-specific logic here and keeping reusable business logic in the desktop project when both need it.
- Avoid UI, tray, or interactive workflow assumptions in this project.
- Current scope also includes log-health inspection for send flow, recommendation flow, and operation-event funnels; keep those summaries source-driven and deterministic.

## Validation

- Build with:
  - `dotnet build src\QuickReply.ReplayEval\QuickReply.ReplayEval.csproj`
  - or `dotnet build 回复效率工具.sln`
- For replay/log-health behavior changes, prefer also running `dotnet test tests\QuickReply.ReplayEval.Tests\QuickReply.ReplayEval.Tests.csproj`.
- If you change argument parsing, output files, or replay metrics, keep summaries and artifact paths explicit and deterministic.
