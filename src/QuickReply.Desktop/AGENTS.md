# QuickReply.Desktop Guide

This guide adds desktop-app-specific rules on top of the repository root `AGENTS.md`.

## Role of this project

- `QuickReply.Desktop` is the primary WPF application.
- `QuickReply.Core` now carries part of the shared model/helper surface that does not require Windows APIs; keep desktop-only behavior in this project and move pure logic downward when practical.
- `App.xaml.cs` is the composition root that wires services, view models, windows, tray behavior, logging, sync, updates, and the screenshot-tool bridge.
- Keep app startup composition centralized there, but push runtime workflows into dedicated coordinators instead of growing more behavior directly inside `App`.
- Current desktop responsibilities also include:
  - search-overlay recommendation and AI draft presentation
  - knowledge-review operator UI that talks to `QuickReply.CloudService`
  - AI auth status/login/logout UI that probes current server first and falls back to the local cloud service

## Architectural boundaries

- `ViewModels/` owns UI-facing state, commands, and presentation flow.
- `Services/` owns persistence, system integration, sync/update flows, diagnostics, screenshot integration, and recommendation orchestration.
- `Models/` owns desktop-specific durable data models, DTOs, enums, and settings objects that still require the desktop assembly.
- Window `.xaml.cs` files should stay thin and UI-event-focused.
- Do not move business logic into XAML or code-behind when it belongs in a service or view model.
- If a helper or model no longer depends on WPF, Win32, desktop state, or screenshot/runtime integration, prefer moving it into `QuickReply.Core` instead of keeping it here.
- Recommendation/query contracts and AI prompt/provider DTOs should keep migrating downward. Let this project own desktop-only mapping from `ReplyPhrase` or other observable/view-facing types into `QuickReply.Core` contracts rather than keeping those contracts here.
- Dedicated operator windows such as knowledge review or hotkey capture should stay UI-shell-oriented; endpoint fallback, persistence, and orchestration stay in services.
- Settings-window session orchestration now belongs in `Services/SettingsWindowCoordinator`, and settings-to-selection mapping belongs in `Services/SettingsWindowSelectionMapper`. Keep `MainWindow` and `SettingsWindow` from re-growing those responsibilities inline.
- `MainViewModel` no longer owns every settings/file/theme helper inline. Keep file-library catalog work, settings snapshot copy logic, and appearance-theme persistence in dedicated services instead of re-expanding `MainViewModel`.

## Change patterns

- Prefer extending existing services and models before adding new abstraction layers.
- When a feature spans UI, storage, and external behavior, keep responsibilities split:
  - UI interaction in windows/view models
  - durable state in `SqliteAppDataStore` or related services
  - OS / network / process side effects in services
- Follow existing WPF and MVVM patterns already used in this project.

## Important integration areas

- Messaging/send behavior depends on clipboard, keyboard simulation, and foreground-window tracking.
- Search overlay and recommendation flows depend on `SearchOverlayViewModel`, recommendation services, and conversation logging.
- Search overlay recommendation/AI UI should stay explainable: when changing it, keep grounding visibility (short phrase count, published-knowledge count, AI provider/fallback status) aligned with the actual retrieval/generation behavior.
- Recommendation and knowledge-card lanes should stay source-transparent. Keep `历史高频`、`短语库匹配`、`问答卡`、`资料卡` visible when the retrieval pipeline can distinguish them; do not collapse them back into a single generic recommendation list.
- AI drafts should keep a conservative grounded-degrade path. If retrieval grounding exists but model generation is skipped or unavailable, prefer explicit “保守草稿 / provider / fallback” wording over returning a silent empty AI section.
- Search overlay should also surface phase-aware status for recognition/recommendation/results. Keep “识别中 / 已识别但未命中 / 搜索无结果但上方仍有建议” visible instead of forcing users to infer state from an empty panel.
- Search overlay keyboard behavior should stay explicit and low-risk. Keep search-text send/save shortcut policy centralized instead of reintroducing single-key send actions in code-behind.
- Send/input failures shown in the overlay should stay structured and explainable. Prefer `ReplyActionResult.FailureKind` + guidance text over string-matching UI logic.
- Text-only, file-drop, and mixed rich send flows should keep sharing the same structured clipboard/write/paste/send orchestration in `MessageComposeService`. Do not let one payload path drift back to bespoke retry or focus-handling logic.
- Sync, update, and issue-report behavior are real shipped capabilities; keep docs and implementation aligned.
- The screenshot tool is an external Python utility launched from the desktop app. Keep the integration boundary explicit.
- AI auth and knowledge review rely on `QuickReply.CloudService` endpoints and may auto-fallback to the local cloud service when the configured server does not expose those APIs.
- The knowledge-review window now also surfaces audit history and unpublish actions. Keep raw history fetching, endpoint fallback, and published-card mutation in services; keep filtering and presentation behavior lightweight in the window shell.

## Minimum verification after changes

- Build with one of:
  - `dotnet build src\QuickReply.Desktop\QuickReply.Desktop.csproj`
  - `dotnet build 回复效率工具.sln`
- If the change touches operator windows, settings orchestration, or desktop/cloud contract handling, prefer also running `dotnet test tests\QuickReply.Desktop.Tests\QuickReply.Desktop.Tests.csproj`.
- If startup or DI wiring changed, verify the app still composes from `App.xaml.cs`.
- If UI behavior changed, prefer the smallest relevant manual validation path instead of broad exploratory changes.

## Inheritance note

- `Models`, `ViewModels`, `Commands`, `Native`, and `Styles` inherit this file.
- `Services` has a narrower nested `AGENTS.md` with stricter guidance for side-effect-heavy code.
