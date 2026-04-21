# QuickReply.CloudService Guide

This project is the server-side skeleton for cloud sync, knowledge review, published-knowledge delivery, and runtime diagnostics.

## Scope

- Keep `knowledge_candidates`, review actions, and `published_knowledge_cards` on the server side.
- The desktop client may only consume published knowledge through sync endpoints.
- Do not expose unpublished review data to desktop-facing read APIs.

## API boundaries

- Desktop-facing read API:
  - `GET /api/quickreply/knowledge/published`
- Runtime/ops API:
  - `GET /healthz`
  - `GET /readyz`
  - `GET /version`
  - `GET /info`
- Operator/background job APIs:
  - `POST /api/quickreply/knowledge/jobs/import-logs`
  - `GET /api/quickreply/knowledge/jobs/runs`
- Operator review APIs:
  - `GET /api/quickreply/knowledge/review/queue`
  - `GET /api/quickreply/knowledge/review/{id}/sources`
  - `GET /api/quickreply/knowledge/review/{id}/history`
  - `POST /api/quickreply/knowledge/review/{id}/approve`
  - `POST /api/quickreply/knowledge/review/{id}/reject`
  - `POST /api/quickreply/knowledge/published/{id}/remove`
- Treat `review/*`, `jobs/*`, and `/info` as management/control-plane APIs. They should not stay anonymously exposed on a real remote server.
- Keep `readyz` focused on real runtime readiness rather than a static “ok=true”.
- Keep response shapes aligned with `docs/cloud-sync-update-server.md`.
- Remember that the desktop client may talk to this service either as a true remote server or through the local fallback bootstrapper at `http://127.0.0.1:5128`.

## Persistence rules

- Favor backward-conscious schema changes and explicit migrations for SQLite-backed local development.
- Keep candidate, source, published-card, and job-run data separate.
- Record review outcomes and publish state explicitly so later analysis can build on the same tables.
- Published-knowledge delivery uses a monotonic revision/cursor model. Keep `published_knowledge_changes`, revision fields, and response `latestRevision/nextCursor/hasMore` semantics aligned across store, API, docs, and desktop sync.
- Review queue, source browsing, and review history all expose pagination. Preserve those response shapes instead of silently falling back to unbounded arrays.
- When approving/rejecting candidates, preserve audit metadata such as reviewer identity and review-session context.
- When publishing or unpublishing cards, update both current state and revision/change-log state in the same transaction.

## Validation

- Run `dotnet build src\QuickReply.CloudService\QuickReply.CloudService.csproj` after server changes.
- Run `dotnet test tests\QuickReply.CloudService.Tests\QuickReply.CloudService.Tests.csproj` when touching API contracts, persistence, or review/publish flow.
