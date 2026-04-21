# QuickReply.Core Guide

This project is the cross-platform core layer for QuickReplyTool.

## Scope

- Put pure models, value objects, enums, and deterministic business logic here.
- Code in this project must stay buildable on plain `net8.0`.
- This is the preferred layer for contributors working on macOS or Linux.
- Recommendation/query request DTOs and other UI-agnostic helper logic belong here once they no longer depend on desktop-only observable/UI types.

## Keep out of this project

- WPF, WinForms, XAML, `System.Windows.*`
- Win32 hooks, foreground window tracking, hotkeys, clipboard integration
- Python screenshot runtime integration
- Local file paths or OS-specific launcher assumptions unless explicitly abstracted

## Boundary rule

- If a type can be expressed without Windows-only APIs, prefer moving it here instead of keeping it in `QuickReply.Desktop`.
- `QuickReply.Desktop` should reference this project for shared logic; shared logic must not reference `QuickReply.Desktop` back.
- During the migration phase, some moved files intentionally keep `QuickReply.Desktop.*` namespaces to reduce churn while still enforcing a real project boundary.

## Validation

- Changes here should compile with the whole solution and must not introduce `-windows` target framework dependencies.
