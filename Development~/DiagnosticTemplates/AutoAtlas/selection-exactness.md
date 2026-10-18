# AutoAtlas Selection Exactness

id: `autoatlas-selection-exactness`  
status: `captured-active-at-snapshot`  
runtime_cost: `low`

## Purpose

Expose whether ranked greedy disjoint selection differs from the exact weighted set-packing result, without treating the production exact solver itself as temporary diagnostic code.

## Permanent production behavior

The following are production algorithm and must survive diagnostic cleanup:

`Editor/NDMF/AutoAtlasTextureMergePlanner.cs`
- `ExactSelectionGroupLimit`
- `SelectTwoDimensionalMerges(...)`
- `SelectExactByGroupMask(...)`
- bounded fallback when the group universe is too large.
- tie behavior that preserves the established greedy execution order when total predicted saving is equal.

The old `candidateCount <= 16` exact-subset path may remain as bounded fallback/support, but the current common path is exact by atomic-group mask when the distinct group universe is bounded.

## Temporary reporting anchors

- `SelectionDiagnostics`
- `SelectionSolverKind`
- report fields:
  - candidate count
  - distinct group count
  - solver
  - greedy saving
  - selected saving
  - delta.

`SelectionImprovement` may be retained if production/user-facing behavior still needs it; otherwise classify its consumers before removal.

## Patch sources

Production exact-selection upgrade:
- `6542f9b2a45c3ecefb3634b76541b3a2ae3cc521`
- `93f7bf6e1748e0a09b4691c55c375d3f2552c034`

Diagnostic presentation:
- `eec70ad8b4cf5488bc415e74f16bf038d0844bbc`
- `5e793e9a32db7ae46f3d56864459e74b4c6371c9`

Historical precursor:
- `b7b4fab9886fd9a53a52cbed505fa97a29b44123`
- `07a54544b7050879e725f38f2377ff605a857764`

Tests added for current production solver:
- commit `f6c14101f70277417ccb804e257f41c73f2f16bb`.

## Output contract

One selection-policy line must identify:
- `candidates`
- `distinctGroups`
- `solver`
- `greedySaving`
- `selectedSaving`
- `delta`
- active exact/fallback limits.

Interpretation:
- `delta = 0`: greedy total saving equals exact optimum for this candidate set.
- `delta > 0`: exact selection improved the adopted total saving.
- one or zero candidates may report `Greedy`; no exact search is necessary because there is no competing disjoint combination.

## must_not_change

- Candidate ranking/gates.
- Predicted saving semantics.
- Production exact solver and bounded fallback.
- Execution ordering when exact and greedy totals are equal.
- State-budget search itself.

## validation

Keep production tests:
- `LargeCandidateSetUsesExactGroupMaskWhenGroupUniverseIsBounded`
- `ExactGroupMaskImprovesGreedyWithMoreThanSixteenCandidates`
- `LargeGroupUniverseKeepsBoundedGreedySelection`
- existing exact/equal-total selection tests.

Captured benchmark result:
- no tested benchmark fixture had `delta > 0`.
- This validates those fixtures only; it is not a reason to remove the production exact solver.

## activation_recipe

If reporting was removed but needs to be restored:
1. Do not modify selection behavior.
2. Compute greedy total alongside the already-selected production result.
3. Record solver kind, candidate count, distinct group count, greedy total, selected total.
4. Append the compact selection-policy line.
5. Add detailed greedy-vs-selected member lists only when a positive delta needs investigation.

## removal_contract

Safe to remove:
- `SelectionDiagnostics` DTO/state if it has no non-diagnostic consumer.
- solver/greedy/selected/delta report formatting.

Must preserve:
- exact group-mask production solver.
- limits and fallback.
- production tests named above.
