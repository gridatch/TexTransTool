# WDT Development / Audit Policy

This document records persistent development and audit principles for the WDT-specific extensions in this repository.

## Source of truth

- The current repository code is the implementation source of truth.
- HANDOFF notes, chat summaries, and historical diagnostics are context only. Re-read the relevant current code before changing behavior.
- Avoid reimplementing existing AtlasTexture/TTT processing when the existing pipeline can be reused.

## Audit purpose

An audit is **not** an exercise in making AutoAtlasTexture progressively more conservative.

The primary purpose of an audit is to verify that:

- the implementation is structurally correct and understandable;
- responsibilities are separated appropriately;
- conditions and state transitions have no important omissions;
- planner, execution, fallback, reporting, and replacement behavior remain internally consistent;
- the design continues to follow the existing AtlasTexture architecture;
- tests reflect intended semantics instead of accidentally freezing an incorrect design;
- there is no unnecessary duplication, hidden coupling, or accidental behavior change.

## Avoid AutoAtlas-only over-safety

A recurring failure mode in this project has been adding AutoAtlasTexture-only rejection, exclusion, or fallback rules for corner cases that ordinary AtlasTexture already accepts and that are extremely unlikely to matter in real use.

Do **not** add such restrictions merely because a theoretical edge case can be imagined.

Before adding an AutoAtlas-specific safety condition, verify all of the following:

1. There is a concrete failure mode specific to automatic selection/merging, rather than a condition ordinary AtlasTexture already tolerates.
2. The failure is plausible enough in real use to justify permanent complexity and lost optimization opportunities.
3. The condition cannot be handled naturally by the existing shared AtlasTexture pipeline.
4. The new restriction does not unnecessarily reduce AutoAtlas adoption for normal inputs.
5. The implementation and maintenance cost is justified by the actual risk.

“Safer” by itself is not sufficient justification.

The default principle is:

> If ordinary AtlasTexture permits a case, AutoAtlasTexture should also permit it unless automation introduces a specific, demonstrated additional risk.

## Safety, cost, and correctness are separate concerns

Do not collapse unrelated concerns into one conservative gate.

Examples:

- whether an active untransformed consumer makes a transformation unsafe;
- whether an external active consumer means an original source texture must remain resident;
- whether a generated target material unexpectedly retains an original source;
- whether an Atlas candidate is actually cheaper than the best non-Atlas result.

These are different questions and should remain represented by different responsibilities and conditions.

A reference merely existing in a material, Animator, Modular Avatar component, or other metadata is not automatically evidence that it should be charged as an AutoAtlas operation cost.

## Audit changes should be minimal

- Do not add guards for hypothetical cases without a concrete need.
- Do not perform unrelated cleanup or broad refactoring during an audit.
- Do not change established user-facing behavior unless the audit identifies a real defect.
- Prefer simplifying an incorrect condition over layering another condition on top of it.
- If a proposed audit fix makes the implementation substantially more complex or reduces normal-case optimization, re-evaluate whether the requirement is valid before implementing it.

## Validation standard

Packaging success is not Unity validation.

When behavior changes:

- distinguish GitHub workflow/package generation from Unity compilation and runtime validation;
- compare against representative historical project results when available;
- confirm both transformation logs and visible output for relevant real projects;
- treat newly discovered optimization opportunities separately from regressions.


## Repository branches and VPM channels

- `master` is for upstream TexTransTool integration, not WDT feature development. `wdt` is the reviewed WDT integration branch. Implement WDT features/fixes on focused `feat/**` or `fix/**` branches based on current `wdt`, normally with a Draft PR. Documentation-only work can use `docs/**`.
- `.github/workflows/BuildWorkPackage.yml` (`Publish Debug Package`) runs on pushes to `feat/**` and `fix/**`. It packages that commit and **replaces the single rolling debug VPM entry**, incrementing `lastDebugSerial`. Its ZIP and metadata are published on `vpm`; it does not create a formal stable release. Another feature-branch push may replace the current debug candidate, so verify the active version/commit before testing in Unity.
- The live debug state is `validation-state.json` on the `vpm` branch (`currentDebugBranch`, `currentDebugCommit`, `currentDebugVersion`, `currentDebugZip`); the package index is `vpm.json` on that branch. The VCC repository URL is `https://raw.githubusercontent.com/gridatch/TexTransTool/vpm/vpm.json`. Choose the intended package version **per Unity project** through VCC; avoid a global Local registration that substitutes for the official package.
- WDT VPM package ID is `net.gridatch.tex-trans-tool`, distinct from upstream `net.rs64.tex-trans-tool`. Current formal versions use `X.Y.Z+wdt.ttt.A.B.C` (upstream `package.json.version` must equal `A.B.C`). Current debug versions append `.debug.<serial>.<change>.<sha>` to that formal base's metadata. Do not reuse a version for different contents.
- `.github/workflows/PublishVPM.yml` publishes stable packages from `release/X.Y.Z+wdt.ttt.A.B.C` **only when that branch points to current `wdt` HEAD**, or by manual dispatch on `wdt`. It creates/reuses the matching GitHub Release, updates the VPM index and clears the rolling debug registration. Stable VPM versions are immutable. It shares `wdt-vpm-publish` concurrency with the debug workflow.
- Check the workflows themselves before using them; these descriptions are a map of the current process, **not** permission to publish. Only perform a formal release after an explicit request.

## Development, validation and integration workflow

1. **Establish the baseline:** read `AGENTS.md`, this policy, live code, current `wdt` HEAD, related branches/PRs and applicable vendor APIs. Reconcile stale plans with the source. A request to continue investigation or design is not unconditional authorization to implement.
2. **Implement in a focused work branch:** keep requested changes within their actual responsibility. Avoid unrelated cleanup and product instrumentation. Create/update its PR and explain accepted decisions and scope; code and durable context documentation should evolve together.
3. **Offer a debug candidate for Unity validation when appropriate:** pushing a `feat/**` or `fix/**` branch automatically updates the rolling VPM package. Record its exact branch, commit and debug version from the resulting workflow/`vpm` state, not from the intended version alone. Let the user validate the installed version in the real project; an Actions ZIP is not a Unity test.
4. **Record evidence and resolve findings:** distinguish source inspection, package creation, Unity compilation, EditMode tests, real-project smoke tests and visual/NDMF checks. If a surprising failure contradicts the design assumptions, investigate and present findings rather than adding speculative fallback logic. Do not claim a test ran when it did not.
5. **Review before integration:** after the requested validation, inspect the full branch diff **and its commits**. Organize provisional changes into coherent feature/responsibility units, combining related follow-up fixes with their feature and separating independent work. If history is curated, verify the final tree still matches the validated tree; do not rewrite already merged `wdt` history. Merge into `wdt` only with the appropriate approval; merging does not itself authorize a release.
6. **Release separately on request:** use the defined `release/**` or manual `wdt` workflow, check VPM/Release results and package identity, and report what Unity verification was actually performed. Do not publish stable packages automatically on ordinary feature completion.

## Maintainer-context upkeep

This policy preserves **accepted, durable cross-session intent**, not a session transcript. Update it proactively when work establishes or changes a non-obvious behavior or boundary that a future maintainer could misread by inspecting code alone. At minimum, check at these points: when a design choice is accepted, when an implementation changes a documented invariant, before handoff/PR integration, and after a release-policy change.

- Record the **reason and behavioral contract**, where it lives in the current code, relevant dependencies/version assumptions, known regressions, and any important rejected or superseded alternative. Keep the entry concise and accurate; do not turn a feature-specific workaround into a universal prohibition.
- Update the existing relevant section rather than appending redundant instructions or making new competing policy documents. For extensive feature-specific details, link the canonical design/PR; the policy should state only the durable conclusion needed to interpret future changes.
- Do not elevate a proposal, unmerged PR, old diagnostic, or unverified hypothesis into an accepted rule. If evidence conflicts, investigate it and mark unresolved status in the work PR/handoff until resolved.
- Temporary investigations, exact debug versions, per-branch TODOs and verification logs belong in PR descriptions or handoffs. They must identify the code revision and separate verified from unverified. A new agent must recheck current Git and VPM state rather than assuming those notes are still current.
- Before concluding work, inspect whether this policy remains consistent with the new implementation. Include any necessary documentation adjustment in the same work branch. If no persistent design or process knowledge changed, no documentation update is necessary.

## Current architectural responsibility boundaries

- Normal AtlasTexture provides the shared atlas implementation; AutoAtlasTexture adds automatic selection and lossless optimization **using shared Atlas semantics**. AutoAtlas-only rejection/fallback conditions need a concrete automation-specific reason, as described above.
- Part Atlas Prefab extraction orchestrates a disposable working copy, the external HairCostumeExtractor API adapter, Modular Avatar attachment, the shared AutoAtlas/persistent asset path and saved Prefab/report. Unpacking only a disposable copy is not itself a violation of non-destructive editing; the input Prefab connection and references must remain intact.
### Part Atlas / HairCostumeExtractor compatibility

The Part Atlas workflow targets HairCostumeExtractor (HCE) 1.4.7 and later public APIs; **1.4.7 and 1.4.10 are the specifically inspected contracts**, not a guarantee for untested future versions. The HCE version change accepted in PR #40 uses these boundaries:

- Use Reflection against the required HCE public API without a compile-time HCE dependency. `stripAvatarComponents` is no longer an option; `keepAvatar` is optional because it is absent in 1.4.7, and must be `false` when present. Root AvatarDescriptor/Pipeline components **and the root Animator** are removed by HCE parts extraction even for sources without an AvatarDescriptor. The source-is-avatar check remains independently necessary for Humanoid/Modular Avatar attachment.
- Set HCE `copyMaterials=false` and `makeComparisonImage=false`: retain source references for unchanged Material/Texture/Mesh while continuing to persist HCE-generated Trim Mesh and WDT AutoAtlas output. The generated folder is not promised to be independently portable to another Unity project.
- Preserve source-hierarchy animation ObjectReference capture and WDT reference validation, HCE Trim result validation, temporary Scene/Prefab Unpack **only on disposable working copies**, Modular Avatar attachment, and owned-output cleanup. Use HCE's unique Trim Mesh file paths rather than duplicating its name collision workaround; still remove superseded unreferenced generated Trim Mesh.
- The optional HCE `diagnosis` describes the **extraction stage**, not the eventual AutoAtlas Prefab. Record it only in the detailed text report, distinguishing unavailable diagnostics and skipped unreadable comparisons from a full clean comparison. Do not add a new diagnostic-based stop condition or result-window UI.
- `HeadPart` and `BodyPart` are presentation categories, displayed as `角・耳` and `しっぽ・翼` with existing Hair/Costume colors. Renderer eligibility and initial selection do not change. Do not bundle unrelated result-window style changes into the HCE compatibility change.

- Persistent Atlas Bake updates a stable named output and manages existing references/GUIDs; Part Atlas instead creates distinct timestamped standalone output generations. Do not infer one feature's overwrite/rollback contract from the other.
- Relevant entry points include `Editor/Inspector/TextureAtlas/` (inspector, Part Atlas, AutoAtlas and persistence), `Runtime/TextureAtlas/` (shared atlas core), and `Development~/DiagnosticTemplates/` (archived diagnostic reconstruction, not always-on production behavior). Re-read live paths and contracts for the task.

## Handoff requirement

For future WDT development or audit work, read this file together with the current code before deciding that a new safety restriction is required.

This policy is intentionally persistent across individual feature branches and conversations.
