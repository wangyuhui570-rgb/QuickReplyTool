# Tests Guide

This directory contains xUnit test projects for the desktop app, cloud service, and replay/evaluation tool.

## Scope

- `QuickReply.Desktop.Tests`
  - covers desktop services, settings, recommendation flows, packaging helpers, and local integration seams
- `QuickReply.CloudService.Tests`
  - covers cloud-service API contracts, import/synthesis jobs, auth bridges, and review/publish behavior
- `QuickReply.ReplayEval.Tests`
  - covers replay sample loading, reporting, and log-health analysis

## Change rules

- Keep tests aligned with the actual public/internal behavior of the runtime project they exercise.
- When runtime behavior changes intentionally, update the matching tests in the same turn instead of leaving stale expectations behind.
- Prefer focused test doubles and fixture reuse over duplicating large setup logic across suites.
- Do not silently weaken assertions for high-risk paths such as send flow, sync/update, AI auth, knowledge review, or synthesis fallback order.

## Validation

- Prefer the smallest relevant suite first:
  - `dotnet test tests\QuickReply.Desktop.Tests\QuickReply.Desktop.Tests.csproj`
  - `dotnet test tests\QuickReply.CloudService.Tests\QuickReply.CloudService.Tests.csproj`
  - `dotnet test tests\QuickReply.ReplayEval.Tests\QuickReply.ReplayEval.Tests.csproj`
- If a change touches shared contracts between desktop and cloud projects, run both affected suites.
- Keep test names and failure messages descriptive enough for operators to understand which workflow regressed.
