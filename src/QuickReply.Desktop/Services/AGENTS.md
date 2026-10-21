<!-- Local-only desktop services. Cloud sync / OTA / knowledge-review clients removed. -->
<!-- Local-only: CloudService and ReplayEval projects removed. -->
# QuickReply.Desktop Services Guide

This directory contains side-effect-heavy code. Follow the root and desktop guides first, then these stricter rules.

## Service-layer responsibilities

- Keep OS integration, persistence, process control, logging, sync, update, and search-overlay orchestration inside services.
- Prefer reusing an existing service and model pair instead of duplicating logic in a new helper.
- Keep service interfaces and data contracts stable unless a real boundary change is required.
- If a helper becomes deterministic and UI-agnostic, consider moving it to `src/QuickReply.Core`.

## High-risk areas

- Clipboard and send flow:
  - `ClipboardService`
  - `KeyboardInputSimulator`
  - `MessageComposeService`
  - `ForegroundWindowTracker`
- Logging and overlay flow:
  - `ConversationLoggingService`
  - manual search / recommendation helpers
- Operator review and fallback service flow:
  - `KnowledgeReviewClientService`
  - `KnowledgeReviewQueueGrouper`
  - `LocalCloudServiceBootstrapper`
- Remote side effects:
  - `KnowledgeSyncService`
  - `UpdateCheckService`
  - `IssueReportService`
  - `SelfUpdateService`
- Process and external-tool orchestration:
  - `ScreenshotToolService`
  - startup / tray / single-instance related services

## Safety rules

- Treat `%AppData%\QuickReplyTool` data and `%AppData%\QuickReplyTool\logs` as user/runtime state, not disposable fixtures.
- Be careful with code that changes clipboard contents, focus, keyboard input, process lifetime, remote HTTP calls, or updater behavior.
- Keep side effects out of UI code-behind.

## Persistence and contracts

- `SqliteAppDataStore` is the main application data boundary.
- Keep schema, settings keys, and persisted value formats backward-conscious unless the task explicitly changes them.
- Sensitive tokens must not remain as plaintext values in SQLite.
- `ReplyActionResult` is a structured desktop action contract. Preserve `FailureKind` and `UserGuidance` when plumbing send/copy/input failures through services and UI.
- For sync/update/report services, preserve request shapes and semantics unless the change is intentional and documented.
- Treat the cloud/report/review service address as built-in by default. If a legacy/custom value is invalid, fall back to the built-in default and migrate it back on settings load.
- When the built-in remote service does not expose issue-report endpoints, treat 404/405 as a supported downgrade and keep local issue packages.
- For local cloud-service fallback, preserve the current behavior of probing the configured server first and only then falling back to `http://127.0.0.1:5128`.
- Installed/portable builds bundle a `LocalCloudService` runtime. Keep the bootstrapper preferring the packaged service directory before falling back to source-tree discovery.
- For remote cloud/report endpoints, prefer HTTPS by default; plain HTTP should stay limited to explicit localhost/dev flows.
- Keep desktop runtime workflows such as startup sync/update checks in dedicated coordinators. `App.xaml.cs` should remain the composition root.
- Keep search-overlay shortcut detection and failure-feedback formatting in dedicated helpers.
- Keep `MessageComposeService` on a unified structured send pipeline for text-only, file-drop, and mixed rich content.
- Published-knowledge sync tracks a monotonic revision cursor. Keep `LastPublishedKnowledgeRevision`, response paging (`nextCursor` / `hasMore`), and local cache updates aligned.

## Validation expectations

- Always run `dotnet build src\QuickReply.Desktop\QuickReply.Desktop.csproj` after service-layer changes.
- When changing local cloud fallback, knowledge review, or desktop/cloud contracts, also run `dotnet test tests\QuickReply.Desktop.Tests\QuickReply.Desktop.Tests.csproj`.
- If a change touches sync, update, issue reporting, logging, or screenshot-tool integration, also sanity-check the matching docs under `docs/`.
