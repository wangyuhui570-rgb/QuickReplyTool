# Tests Guide

## Scope

- `QuickReply.Desktop.Tests` covers local desktop services and models.
- Former `QuickReply.CloudService.Tests` and `QuickReply.ReplayEval.Tests` projects were removed with the local-only refactor.

## Commands

- `dotnet test tests\QuickReply.Desktop.Tests\QuickReply.Desktop.Tests.csproj`

## Notes

- Prefer focused tests for the code you touch.
- Do not reintroduce cloud sync / OTA / knowledge-review test suites unless the product adds those features again.
