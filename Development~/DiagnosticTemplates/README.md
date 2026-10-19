# Diagnostic Template Registry

format_version: 1

## Purpose

Store temporary investigation instrumentation in a form that can be reconstructed against a future repository state without keeping the instrumentation enabled in production.

This directory is intentionally under `Development~`. Unity ignores package folders whose names end in `~`, so these records do not require Unity `.meta` files and do not become package assets.

## Source-of-truth rules

- Current repository source is implementation truth.
- A template records diagnostic intent, source anchors, output contract, validation, and historical patch sources.
- Never blindly cherry-pick or replay an old diagnostic commit. Inspect current source first and reapply the smallest equivalent instrumentation.
- Historical commit SHAs are patch sources, not authority over current production behavior.
- If a template conflicts with current source, preserve current production semantics and adapt the diagnostic.
- Diagnostic removal must remove only items explicitly classified as temporary. Production fixes or algorithms discovered during an investigation stay unless separately reviewed.

## Restore protocol

1. Read the target template and its parent index.
2. Inspect the current branch and base branch.
3. Resolve every listed source anchor against current code.
4. Check the listed historical commits only to reconstruct the old code delta or rationale.
5. Classify each old hunk as:
   - `temporary-diagnostic`
   - `diagnostic-presentation`
   - `permanent-production`
   - `permanent-test`
   - `independent-fix`
6. Reimplement only the temporary diagnostic behavior needed for the current investigation.
7. Verify every `must_not_change` invariant.
8. Add/restore the listed validation tests when applicable.
9. Record the new activation commit/HEAD in the template or parent index if the diagnostic meaning changed.
10. When the investigation ends, remove temporary instrumentation using the template's removal contract, not by reverting a commit range wholesale.

## Template contract

Each template should contain:

- `id`
- `status`
- `purpose`
- `runtime_cost`
- `source_anchors`
- `patch_sources`
- `output_contract`
- `must_not_change`
- `validation`
- `activation_recipe`
- `removal_contract`

Optional sections may record known fixtures, historical findings, and dependencies on other diagnostic templates.
