# AutoAtlas Execution Effect Summary

id: `autoatlas-execution-effect-summary`  
status: `captured-active-at-snapshot`  
runtime_cost: `low-to-medium`

## Purpose

Correlate planner predictions with actual applied AutoAtlas operations after FineTuning and execution revalidation. Distinguish merge-specific additional saving from ordinary standalone AutoAtlas saving, and expose merge fallbacks.

## Source anchors

`Editor/NDMF/AutoAtlasTexturePass.cs`

Per-run counters/state:
- `appliedOperationCount`
- `appliedMergeCount`
- `appliedStandaloneCount`
- `appliedSourcePixels`
- `appliedOutputPixels`
- `appliedSourceMemoryBytes`
- `appliedOutputMemoryBytes`
- `appliedMemoryKnown`
- `executionEffectDiagnostics`
- `twoDimensionalFallbackReasons`.

Per-operation diagnostic row construction occurs after final FineTuning memory is known and before/around mesh/material replacement.

Final report anchor:
- `executionSummaryLines`
- localization key `AutoAtlasTexture:info:TemporaryExecutionSummary`.

Timing:
- `processingTimer`
- diagnostic field `processingWithDiagnostics`.

## Patch sources

Useful historical sources:
- `8d11ee353d2270dff0becba11270510b0b5ed25f` — production fallback diagnosis.
- `ef96218494f7b55c3b623459d071f168eb7a4e85` — current investigation report expansion.
- `7a82aea12fce95155770505068b75a7cd2eafa2d` — total AutoAtlas processing-time reporting; this user-facing total-time behavior is permanent and must not be removed with this template.

## Output contract

Summary lines:
- prepared group count
- production state budget
- production safe candidate count
- selected merge count
- predicted merge saving
- applied operation count
- applied merge count
- applied standalone count
- merge fallback count
- aggregate applied input/output pixel count and delta
- aggregate applied input/output texture memory and delta, or `unknown`
- processing time including active diagnostics.

Per operation:
- group IDs or `standalone`
- actual source/output pixels and delta
- actual source/output texture memory and delta, or `unknown`
- material names
- for merges:
  - predicted baseline memory
  - predicted merged memory
  - predicted saving.

## Semantic requirements

The diagnostic must preserve the distinction between:
- original source -> actually applied output: total realized effect
- standalone optimized baseline -> merged output: merge planner's incremental effect.

Do not report merge planner saving as if it were the full original-to-final saving.

For a merge, actual post-FineTuning output is authoritative for execution acceptance. Production currently revalidates strict pixel reduction, texture-memory reduction, and the absence of retained source-texture references before mesh/material mutation. Diagnostic values must observe the already-decided execution result, not change it.

## must_not_change

- FineTuning execution.
- Merge memory revalidation.
- Merge final pixel-count revalidation.
- Generated-material retained-source rejection.
- Common Atlas renderer rejection for meshes with zero submeshes before normalization.
- Rejected output cleanup.
- Standalone acceptance.
- Mesh/material mutation ordering.
- Fallback scheduling.
- User-facing total AutoAtlas processing-time log.

## validation

Useful consistency checks:
- successful merge with no late divergence should have predicted merged memory equal to actual merged memory when the memory model covers the fixture exactly.
- `mergeFallbacks=0` on a benchmark run means every selected production merge executed without falling back.
- aggregate values include only applied operations, not skipped standalone candidates.

Captured benchmark pack:
- Seiran_Test_Atlas, Koiboshi, ShuwashuwaPearl, FrillNyantic, Cornet, SailorCupid, Octopus all completed with `mergeFallbacks=0` at the captured snapshot.

## activation_recipe

1. Initialize counters before execution queue processing.
2. After each operation survives final acceptance and has authoritative FineTuning descriptors, compute actual pixels/memory.
3. Accumulate aggregate counters only for operations that will be applied.
4. Append one immutable diagnostic row per applied operation.
5. After queue completion, append the summary as one long-form diagnostic section.
6. Keep normal user-facing result blocks unchanged.

## removal_contract

Remove:
- detailed per-operation diagnostic string accumulation
- aggregate diagnostic-only counters that have no production consumer
- `TemporaryExecutionSummary` report section
- `processingWithDiagnostics` diagnostic field.

Preserve:
- production counters/state actually required for fallback or acceptance
- final FineTuning memory checks
- final merge pixel checks
- retained-source material checks
- rejected output cleanup
- normal relocation result output
- total AutoAtlas processing-time user-facing log.
