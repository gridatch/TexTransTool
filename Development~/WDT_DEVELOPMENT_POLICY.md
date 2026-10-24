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

## Handoff requirement

For future WDT development or audit work, read this file together with the current code before deciding that a new safety restriction is required.

This policy is intentionally persistent across individual feature branches and conversations.
