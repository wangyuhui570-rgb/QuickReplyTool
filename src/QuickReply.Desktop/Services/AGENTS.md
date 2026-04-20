# QuickReply.Desktop Services Guide

This directory contains side-effect-heavy code. Follow the root and desktop guides first, then these stricter rules.

## Service-layer responsibilities

- Keep OS integration, persistence, process control, logging, sync, update, and recommendation orchestration inside services.
- Prefer reusing an existing service and model pair instead of duplicating logic in a new helper.
- Keep service interfaces and data contracts stable unless a change truly requires a boundary change.

## High-risk areas

- Clipboard and send flow:
  - `ClipboardService`
  - `KeyboardInputSimulator`
  - `MessageComposeService`
  - `ForegroundWindowTracker` / external workbench helpers
- Logging and recommendation flow:
  - `ConversationLoggingService`
  - retrieval / recommendation services
- AI auth / operator review flow:
  - `AiAuthClientService`
  - `KnowledgeReviewClientService`
  - `KnowledgeReviewQueueGrouper`
  - `LocalCloudServiceBootstrapper`
- Desktop UI orchestration boundaries:
  - `DesktopRuntimeCoordinator`
  - `SettingsWindowCoordinator`
  - `SettingsWindowSelectionMapper`
- Search-overlay interaction helpers:
  - `SearchOverlayShortcutPolicy`
  - `SearchOverlayActionFeedbackFormatter`
  - `SearchOverlayPanelStatusBuilder`
- Main-view-model support services:
  - `AppSettingsSnapshotService`
  - `FileLibraryCatalogService`
  - `AppearanceThemeCatalogService`
- Remote side effects:
  - `KnowledgeSyncService`
  - `UpdateCheckService`
  - `IssueReportService`
  - `SelfUpdateService`
- Process and external-tool orchestration:
  - `ScreenshotToolService`
  - startup / tray / single-instance related services

## Safety rules

- Treat `%AppData%\QuickReplyTool` data and `%AppData%\QuickReplyTool\logs` as user/runtime state, not disposable repo fixtures.
- Be careful with code that changes clipboard contents, focus, keyboard input, process lifetime, remote HTTP calls, or updater behavior.
- When adding side effects, make the trigger conditions explicit and preserve current fallback behavior where possible.
- Do not move these side effects into UI code-behind.

## Persistence and contracts

- `SqliteAppDataStore` is the main application data boundary.
- Keep schema, settings keys, and persisted value formats backward-conscious unless the task explicitly changes them.
- API tokens and AI keys must not remain as plaintext values in SQLite. Persist only metadata/state in `app_settings`; sensitive material should live in the desktop secret store / OS user scope.
- `ReplyActionResult` is now a structured desktop action contract. Preserve `FailureKind` and `UserGuidance` when plumbing send/copy/input failures through services and UI.
- For sync/update/report services, preserve current request shapes and semantics unless the change is intentional and documented.
- For AI QA knowledge work, keep candidate generation and review on the server side; desktop services may only sync and consume published knowledge cards.
- For AI auth, prefer the official Codex CLI flow exposed by `QuickReply.CloudService`; desktop services should not invent a separate OAuth/token store.
- Treat the cloud/report/review/AI service address as built-in by default. An empty configured value should resolve to the internal remote service endpoint, not to “未配置服务器”; if a legacy/custom value is invalid at runtime, fall back to the built-in default and migrate it back to that default on settings load.
- When the built-in remote service does not expose issue-report endpoints, treat 404/405 as a supported downgrade: keep local issue packages, skip startup refresh warnings, and avoid surfacing that case as a blocking configuration failure.
- For local cloud-service fallback, preserve the current behavior of probing the configured server first and only then falling back to `http://127.0.0.1:5128`.
- Installed/portable builds now bundle a `LocalCloudService` runtime. Keep the bootstrapper preferring the packaged service directory before falling back to source-tree `dotnet run` discovery.
- For remote cloud/report/AI endpoints, prefer HTTPS by default; plain HTTP should be limited to explicit localhost/dev flows.
- Keep desktop runtime workflows such as startup sync/update checks in dedicated coordinators. `App.xaml.cs` should remain the composition root, not the long-term home for every background workflow.
- Keep settings session orchestration, preview coordination, and settings-selection mapping in dedicated services. Do not push those cross-window behaviors back into `MainWindow` or `SettingsWindow`.
- Keep file-library catalog mutation, appearance-theme catalog persistence, and full `AppSettings` snapshot copy logic in services. `MainViewModel` may coordinate UI state around them, but should not re-grow those long persistence/mapping blocks inline.
- Keep search-overlay shortcut detection and failure-feedback formatting in dedicated helpers. Do not fall back to single-key send/save gestures or UI-side string matching for send failures.
- Keep search-overlay phase/status copy in dedicated builders. OCR/recognition, recommendation availability, AI skip/failure, and empty result states should stay visible and explainable instead of collapsing into a silent empty panel.
- Keep search-overlay layout mode explicit: non-empty manual search text should switch the main pane to search results, while an empty search box should give the remaining space to the scrollable recognition/recommendation pane.
- Keep `SearchFocusHotkeyMode.Tab` restricted to supported chat/workbench windows only. The allow-list should stay focused on WeChat, WeCom, QianNiu/Taobao, Pinduoduo, Douyin workbench, and JD workbench; generic editors, browsers, and unrelated apps must not consume Tab globally.
- When recognition succeeds, keep the actual recognized customer message visibly surfaced in the recommendation pane; do not reduce it to only a generic “已识别客户消息” status line.
- Keep OCR/recognition filtering conservative around window chrome. Title-bar/button words such as “最小化 / 恢复 / 关闭” should be treated as UI noise, and low-confidence weak-control text should degrade to “未可靠识别到客户消息” instead of being surfaced as customer content.
- Keep grounded AI downgrade behavior in services, not in the window layer. When a provider is skipped or fails but retrieval grounding exists, prefer conservative grounded drafts over silently dropping the AI lane.
- Keep `MessageComposeService` on a unified structured send pipeline for text-only, file-drop, and mixed rich content. Clipboard snapshot/write, paste stabilization, send-hotkey waiting, retry, and guidance should stay shared helpers instead of payload-specific branches.
- Keep published-knowledge `candidate_kind` (`qa` vs `resource`) intact through desktop persistence and sync so the client can distinguish knowledge-card types.
- Recommendation and AI tracking should preserve structured knowledge-card metadata in `recommendation_events` so later analysis can distinguish phrase hits, published knowledge-card hits, and AI grounded-fallback behavior.
- Published-knowledge sync now tracks a monotonic revision cursor in addition to timestamps. When changing sync code, keep `LastPublishedKnowledgeRevision`, response paging (`nextCursor` / `hasMore`), and local cache updates in sync.
- Review queue/source clients now consume paged control-plane responses instead of raw arrays. Preserve the accumulation loop and fallback behavior when changing review APIs.
- Review history and published-remove flows also consume paged / structured control-plane responses. Keep the desktop review client aligned with `/review/{id}/history` and `/published/{id}/remove`.
- Knowledge-review grouping is now intentionally conservative: same candidate kind + same platform + same product + same normalized question/answer. Do not widen that grouping silently without revisiting the review UI and docs.
- Published-knowledge sync should make removal effects visible to the desktop side as first-class behavior: keep `removedCardIds` deletion, revision persistence, catalog reload, and any user-facing completion messaging aligned.

## Validation expectations

- Always run `dotnet build src\QuickReply.Desktop\QuickReply.Desktop.csproj` after service-layer changes.
- When changing AI auth, local cloud fallback, knowledge review, or desktop/cloud contract code, also run `dotnet test tests\QuickReply.Desktop.Tests\QuickReply.Desktop.Tests.csproj`.
- If a change touches sync, update, issue reporting, logging, or screenshot-tool integration, also sanity-check the matching docs under `docs/`.
- Prefer focused validation:
  - send-flow changes: target compose path only
  - logging/recommendation changes: target search/recommendation path only
  - sync/update/report changes: confirm endpoint/path and payload assumptions in code and docs
