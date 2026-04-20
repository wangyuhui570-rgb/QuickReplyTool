# QuickReply.CloudService Guide

This project is the server-side skeleton for cloud sync, AI QA review, and published-knowledge delivery.

## Scope

- Keep `knowledge_candidates`, review actions, and `published_knowledge_cards` on the server side.
- The desktop client may only consume published knowledge through sync endpoints.
- Do not expose candidate-generation internals or unpublished review data to desktop-facing APIs.

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
  - `POST /api/quickreply/knowledge/jobs/synthesize-drafts`
  - `GET /api/quickreply/knowledge/jobs/runs`
- AI auth/operator APIs:
  - `GET /api/quickreply/ai-auth/status`
  - `POST /api/quickreply/ai-auth/login`
  - `POST /api/quickreply/ai-auth/logout`
- Operator review helper APIs:
  - `GET /api/quickreply/knowledge/review/{id}/sources`
  - `GET /api/quickreply/knowledge/review/{id}/history`
  - `POST /api/quickreply/knowledge/published/{id}/remove`
- `synthesize-drafts` should use the configured AI provider (`codex_cli`, `openai_api`, `ollama`, or `auto`) and fall back to deterministic heuristics if AI is unavailable; never auto-publish from the synthesis step.
- Default local-dev auth should prefer the official Codex CLI login flow (`codex login`, `codex login status`, `codex logout`) and let Codex CLI own token refresh/session persistence.
- Treat `codex exec` as the official host path for “Sign in with ChatGPT” synthesis. The API-key path remains a compatible fallback, not the default.
- Do not invent a private OAuth flow or claim direct reuse of ChatGPT desktop/session quotas through undocumented HTTP auth. If compatibility behavior is needed, keep it isolated and clearly labeled as such.
- Keep `qa` candidates and `resource` candidates distinct. Do not let short keyword-only samples, greetings, or unsupported abbreviations enter the `qa` path.
- Only run AI synthesis for `qa` candidates with enough evidence; resource/tutorial cards and thin-evidence drafts should stay on deterministic review paths.
- Published-knowledge delivery now uses a monotonic revision/cursor model. Keep `published_knowledge_changes`, card/removal revision fields, and response `latestRevision/nextCursor/hasMore` semantics aligned across store, API, docs, and desktop sync.
- Review queue and source browsing now expose pagination (`limit` + cursor). Preserve those response shapes instead of silently falling back to unbounded arrays.
- Review history also exposes pagination (`limit` + cursor). Preserve action ordering and audit payload fields (`reviewer`, `review_session_id`, before/after snapshots) so the desktop review tool can reconstruct publish / reject / unpublish history.
- Draft synthesis now claims candidates with a lease before running provider work. Preserve `lease_token` / `lease_expires_at` semantics so parallel workers do not double-process the same candidate.
- Review/admin APIs:
  - `GET /api/quickreply/knowledge/review/queue`
  - `POST /api/quickreply/knowledge/review/{id}/approve`
  - `POST /api/quickreply/knowledge/review/{id}/reject`
- Treat `review/*`, `jobs/*`, and `ai-auth/*` as management/control-plane APIs. They must not stay anonymously exposed on a real remote server; local loopback bypass is acceptable for dev fallback only.
- Treat `/info` as a management/control-plane endpoint too. It may expose database/provider/auth readiness details and should stay behind the same management access rules.
- `ai-auth/login` and `ai-auth/logout` should remain loopback-only by default because they manipulate the host machine's Codex CLI login state.
- Keep `readyz` focused on real runtime readiness instead of a static “ok=true”. It should continue reporting database/provider/auth state that matches the current service behavior.
- Keep response shapes aligned with `docs/cloud-sync-update-server.md` and `docs/cloud-ai-knowledge-workflow.md`.
- Remember that the current desktop client may talk to this service either as a true remote server or through the local fallback bootstrapper at `http://127.0.0.1:5128`; keep operator flows usable in both modes.

## Persistence rules

- Favor backward-conscious schema changes and explicit migrations for SQLite-backed local development.
- Keep candidate, source, published-card, and job-run data separate; do not merge review queue and published cards into one table contract.
- Record review outcomes and publish state explicitly so future async jobs can build on the same tables.
- Keep candidate-kind (`qa` vs `resource`) semantics explicit in storage and APIs; the desktop review UI now depends on that distinction.
- When approving/rejecting candidates, preserve audit metadata such as reviewer identity and review-session context so future review history and moderation logs can be reconstructed.
- When publishing cards, update both the current published row and the revision/change-log state in the same transaction.
- When unpublishing cards, update the published-card table, removal change-log revision, candidate archive status, and review audit trail in one transaction.
- Keep runtime diagnostics backward-conscious too: `readyz`, `version`, and `info` should stay aligned with `KnowledgeCloudOptions`, Codex CLI status probing, and database initialization behavior.

## Validation

- Run `dotnet build src\QuickReply.CloudService\QuickReply.CloudService.csproj` after server changes.
- Run `dotnet test tests\QuickReply.CloudService.Tests\QuickReply.CloudService.Tests.csproj` when touching API contracts, persistence, or review/publish flow.
- If you change AI synthesis or auth behavior, also update `appsettings.json` / `appsettings.Development.json` and keep the documented provider/auth/runtime in sync.
