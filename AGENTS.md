# Repository Agent Notes

## Mandatory startup for WDT work

Before analyzing, auditing, reviewing, or modifying WDT-specific code on `wdt` or any WDT feature branch:

1. Read this file.
2. Read `Development~/WDT_DEVELOPMENT_POLICY.md`.
3. Re-read the current implementation relevant to the task. The code is the implementation source of truth; HANDOFF/chat summaries are context only.

Do not skip the policy because the current task appears small or familiar.

If a WDT feature branch was created before these files were added and does not contain them, read the copies from `wdt` before proceeding.

The policy especially prohibits treating audits as an opportunity to add AutoAtlasTexture-only conservative guards for theoretical corner cases that ordinary AtlasTexture already accepts. Any additional AutoAtlas-specific safety restriction must have a concrete automation-specific failure mode and justified real-world value.

## Context ownership

- `AGENTS.md` is the single entry point for persistent WDT development context.
- `Development~/WDT_DEVELOPMENT_POLICY.md` contains the persistent development/audit principles.
- Do not create competing copies of these principles in additional repository documents. If the policy changes, update the policy file and keep this entry point accurate.
