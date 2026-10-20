# WDT Maintainer Context

> **Audit draft.** This file is being reconstructed from the current `wdt` tree, commit history, merged PRs, archived diagnostics, and validated handoff information. Treat sections explicitly marked **audited** as reliable; do not use incomplete sections as a substitute for source review.

## 0. Purpose and loading rule

This is the single normative maintainer-context document for WDT-specific development.

It exists to preserve information that is easy to lose when reading only the final source tree: feature intent, architectural boundaries, non-obvious invariants, reasons for special handling, rejected alternatives, regression-sensitive behavior, and the distinction between implementation/package/Unity/runtime validation.

For WDT work, read this file in full before changing or auditing code.

Do not store transient branch/HEAD/debug-package/release state here. Those must be verified from Git/GitHub and the active handoff.

## 1. Sources of truth

- **Current implementation:** current source tree.
- **Accepted long-lived design intent and invariants:** this document, after auditing against source/history.
- **Current work state:** Git/GitHub plus the active conversation handoff.
- **Validation claims:** only the validation level actually performed. Package publication is not Unity compilation or Unity Test Runner execution.

If these disagree, investigate. Do not "repair" code merely because a local implementation looks redundant before checking the invariant and its history.

## 2. WDT delta inventory — audited at repository-diff level

As of the audit base used to create this document, `wdt` is 45 commits ahead of upstream `master` at merge base `741b7dc3febc1d77269f267f4cf139db0f12492a`.

The WDT delta contains these major areas:

1. WDT VPM/debug/release publication workflows.
2. Shared/manual AtlasTexture correctness and shader-information fixes.
3. Automatic minimum lossless AtlasTexture sizing and property-aware output sizing/reporting.
4. Persistent Atlas bake.
5. Standalone Part Atlas Prefab extraction/atlas workflow using Matsukawa and optional Modular Avatar adaptation.
6. Part Atlas hierarchy/preview/native-inspector UX.
7. AutoAtlasTexture automatic selection and execution.
8. AutoAtlas unused-space texture merging and memory/sampling safety.
9. Shared AtlasTexture resource/material/mesh safety fixes required by the above.
10. Regression tests and archived AutoAtlas diagnostic templates.

Detailed sections below are being audited feature-by-feature.

## 3. Historical-status rule — audited

A PR, branch, debug build, or diagnostic experiment is not a specification merely because it existed.

Important examples in WDT history:

- PR #5 (general texture ST baking) was **not merged** and was superseded by the narrower independently-tiled second-normal solution in PR #11.
- PR #16 was an integration probe only; the adopted Matsukawa architecture was implemented later in PR #17.
- PR #18 Roslyn syntax CI was **not merged**.
- PR #19 was superseded by merged PR #20.
- PR #24 was an experimental/draft Auto Material Atlas investigation. Its conservative-analysis assumptions are not automatically current AutoAtlas requirements.
- PR #25 and PR #26 contain the current accepted AutoAtlas merge implementation and the corrective re-audit that removed over-conservative AutoAtlas-only semantics.

Rejected or superseded proposals must never be reintroduced from old branches/commits without re-evaluating them against the current accepted specification.

## 4. Validation terminology — audited

Keep these distinct:

- GitHub package/debug workflow success
- source/static checks
- Unity C# compilation
- Unity EditMode/NUnit/Test Runner
- real-project smoke testing
- feature-specific visual/runtime verification

A packaging workflow does not start Unity Editor and does not prove Unity compilation or automated regression-test execution unless the workflow explicitly does so.

## 5. Feature audit

### 5.1 WDT packaging/release workflow

**Status:** history audited; implementation audit pending.

The workflow evolved deliberately from automatic branch releases to:
- separate WDT package identity,
- stable SemVer visible without prerelease settings,
- explicit formal releases,
- rolling debug validation packages for work branches,
- versioned release-branch support,
- WDT/upstream version metadata.

Do not infer the final release policy from early PRs #1-#3; later PRs intentionally replaced parts of those policies.

### 5.2 Shared/manual AtlasTexture

**Status:** pending detailed source audit.

### 5.3 Persistent Atlas bake

**Status:** history audited; source audit in progress.

### 5.4 Part Atlas Prefab workflow

**Status:** history audited; source audit in progress.

### 5.5 AutoAtlasTexture

**Status:** history audited; source audit in progress.

Critical accepted direction already established by the corrective re-audit:

> AutoAtlasTexture follows normal AtlasTexture semantics unless AutoAtlas itself introduces a concrete additional failure mode.

Do not turn AutoAtlas into a separate exhaustive shader-compatibility proof system.

### 5.6 AutoAtlas texture merging

**Status:** history audited; source audit in progress.

Merging combines atomic groups that standalone AutoAtlas would keep separate. It may therefore require checks specifically against semantics newly introduced by merging, together with benefit/sampling/resource revalidation and standalone fallback.

## 6. Detailed accepted invariants

_To be filled only after matching current code to its introduction/fix history._

## 7. Regression-sensitive areas

_To be filled only after matching current code to its introduction/fix history._

## 8. Rejected / superseded designs

_To be expanded during audit. Entries record current conclusions, not a chronological diary._

## 9. Required validation matrix

_To be completed after test-code and workflow audit._

## 10. Code and test map

_To be completed after current-tree audit._
