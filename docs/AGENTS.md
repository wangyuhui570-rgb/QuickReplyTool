<!-- Local-only docs. Cloud sync / OTA server docs removed. -->
# Documentation Guide

This directory contains Chinese-first product, ops, and release documentation.

## Writing rules

- Prefer clear Chinese prose; keep commands, file paths, endpoint paths, and code identifiers in their original form.
- Document current behavior, not aspirational behavior.
- When behavior is partial, conditional, or manual, say so explicitly.

## When docs must be updated

- Update the relevant document when implementation changes any of these areas:
  - cloud sync
  - local cloud-service fallback or operator review flow
  - update checks
  - ota release workflow
  - issue reporting
  - patch release workflow
  - installer or portable packaging behavior
- Keep examples and endpoint descriptions aligned with the current code under `src/QuickReply.Desktop` and `scripts/`.
- If a change affects knowledge review or published-knowledge sync, update `cloud-sync-update-server.md`.

## Accuracy rules

- Do not claim a feature is automatic, silent, scheduled, or production-ready unless the code actually does that today.
- If README or older version notes differ from the current code, prefer the code and correct the docs.
- Preserve pragmatic guidance for operators and maintainers; avoid turning docs into vague marketing copy.
