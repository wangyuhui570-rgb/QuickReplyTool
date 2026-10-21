<!-- Local-only: CloudService and ReplayEval projects removed. -->
# QuickReply.Desktop Guide

This guide adds desktop-app-specific rules on top of the repository root `AGENTS.md`.

## Role of this project

- `QuickReply.Desktop` is the primary WPF application.
- `QuickReply.Core` carries shared models and deterministic helpers that do not require Windows APIs.
- `App.xaml.cs` is the composition root for services, view models, windows, tray behavior, logging, updates, and screenshot-tool integration.
- Keep startup composition centralized in `App.xaml.cs`, but push durable workflows into dedicated services or coordinators.

## Architectural boundaries

- `ViewModels/` owns UI-facing state, commands, and presentation flow.
- `Services/` owns persistence, system integration, diagnostics, update checks, local cloud fallback, and message delivery orchestration.
- `Models/` owns desktop-specific durable data models, DTOs, enums, and settings objects.
- Window `.xaml.cs` files should stay thin and event-focused.
- If a helper no longer depends on WPF, Win32, or other desktop-only state, prefer moving it into `QuickReply.Core`.
- Keep settings-window session flow in dedicated services instead of re-growing large mapping blocks inside `MainWindow` or `SettingsWindow`.

## Important integration areas

- Messaging/send behavior depends on clipboard, keyboard simulation, and foreground-window tracking.
- Search overlay behavior depends on `SearchOverlayViewModel`, manual search services, and clear status text.
- This app is local-only: no cloud sync, remote knowledge review, or OTA client.
- The screenshot tool is an external Python utility launched from the desktop app. Keep that boundary explicit.

## Minimum verification after changes

- Build with one of:
  - `dotnet build src\QuickReply.Desktop\QuickReply.Desktop.csproj`
  - `dotnet build 回复效率工具.sln`
- If the change touches settings orchestration, knowledge review, or desktop/cloud contracts, also run `dotnet test tests\QuickReply.Desktop.Tests\QuickReply.Desktop.Tests.csproj`.
- If startup or DI wiring changed, verify the app still composes from `App.xaml.cs`.

## Inheritance note

- `Models`, `ViewModels`, `Commands`, `Native`, and `Styles` inherit this file.
- `Services` has a narrower nested `AGENTS.md` with stricter guidance for side-effect-heavy code.
