# AutoAtlas Diagnostic Templates

snapshot_branch: `feat/auto-atlas-merge-planner`  
snapshot_base: `wdt`  
snapshot_head_before_template_registry: `1405baf8aa52d4a33f88dfb4898e4113a11d20a8`

## Scope

These templates cover temporary diagnostics used to investigate AutoAtlas texture merging, search retention, candidate selection, standalone lossless-profile failures, execution effects, and NDMF Console presentation.

The snapshot head above contains the active diagnostic pack. The pack was subsequently removed after the templates were captured.

current_status: `archived-reapply-available`  
cleanup_implementation_head: `53c45e78637b9f7496c3eb0333e8a1ef6819a3a8`
post_cleanup_audit_head_before_registry_update: `cc26cebbb1d74328bf343a502067477c30efe78b`

Cleanup removed the temporary comparison searches, watched-path/rejection reporting, standalone failure strings, execution-effect summary, long-form NDMF diagnostic UI, diagnostic-only tests, and temporary localization keys. Permanent production behavior and independent fixes listed below were retained.

## Template index

- `state-budget-comparison.md` — compare production 1024-state search with 512/2048/4096 planning-only searches and trace retention losses.
- `selection-exactness.md` — expose greedy vs exact disjoint candidate selection without confusing the production exact solver with temporary logging.
- `standalone-profile-failure.md` — classify why a prepared atomic group has no lossless standalone profile.
- `execution-effect-summary.md` — report predicted vs actual merge/standalone effects and fallback counts.
- `ndmf-console-exact-copy.md` — render long diagnostics reliably and guarantee display/copy source identity.

## Current classification

### permanent-production — do not remove with diagnostic cleanup

- AutoAtlas renderer-target population aligned with regular AtlasTexture semantics.
- Production 2D shadow packing search with `ShadowPackingStatesPerHost = 1024`.
- Candidate gates: PixelCount, FullResidentMemory, Sampling.
- Physical-pixel padding preservation for 2D merge profiles/execution.
- Final FineTuning memory revalidation before mutation.
- Final merge pixel-count revalidation before mutation.
- Unknown texture-memory formats fail closed instead of assuming 4 B/px.
- Generated AutoAtlas materials are checked for retained source-texture references before mutation.
- AutoAtlas skips atomic groups whose materials are referenced by NDMF additional-material providers (Animator object curves or Modular Avatar material setters), because those references are rewritten domain-wide and their future renderer/slot UV compatibility cannot be proven automatically.
- Atlas renderer eligibility rejects meshes with zero submeshes before mesh normalization; such meshes have no drawable geometry and material-slot expansion would otherwise be invalid.
- Rejected generated meshes/materials/render textures are destroyed before fallback/skip.
- Standalone post-FineTuning acceptance behavior.
- Exact disjoint selection by group mask for bounded group universes:
  - `ExactSelectionGroupLimit`
  - `SelectExactByGroupMask(...)`
  - fallback behavior for larger universes.
- Visibility-animation merge exclusion behavior.
- Total AutoAtlas processing-time user-facing log. It was explicitly restored after a rejected audit and must remain unless separately changed.
- Common AtlasTexture empty-submesh fix:
  - `AtlasSubMeshIndexIDSetContext.BuildAtlasSubSet(...)`
  - all-null output subsets are not registered
  - mesh resolution uses the same effective-submesh rule.
- Existing common fixes such as `AtlasSubMeshIndexIDSetContext.SubPartEqual` correction and TextureManager ownership handling.

### permanent-tests — keep with production behavior

- Exact group-mask selection tests.
- Large group-universe greedy fallback test, using an input where greedy and exact results differ.
- Empty/ mixed effective-submesh tests.
- Unknown memory-format fail-closed test.
- Final strict pixel-reduction test.
- Rejected Mesh/Material cleanup tests.
- Retained source-texture detection test.
- Other safety tests covering production fallback and renderer targeting.

### temporary-diagnostic

Planner anchors:
- `DiagnosticStateBudgets`
- `DiagnosticGeneratedStateLimit`
- `SearchBudgetComparison`
- `SearchBudgetProbe`
- `WatchedPathDiagnostic`
- `RejectedCandidateDiagnostic`
- retention rank / watched-path state
- comparison-only 512/2048/4096 searches
- `PreparedGroup.StandaloneAnalysisDiagnostics`
- `SelectionDiagnostics` only as diagnostic reporting state; the exact selection algorithm itself is permanent.

Pass/report anchors:
- group preparation report
- safe-candidate top-N report
- state-budget difference reports
- watched-path report
- rejected 4096-width candidate report
- detailed execution-effect summary
- `AutoAtlasTextureTemporaryDiagnosticError`

### diagnostic-presentation

- scrollable line-by-line NDMF diagnostic UI
- one-click exact-copy button
- project-folder label in report
- exact display/copy source identity.

These are removable if no long-form diagnostic report remains, but while present they must obey `ndmf-console-exact-copy.md`.

## Validation matrix captured at this snapshot

All listed projects completed with `mergeFallbacks=0`.

| Project | 1024 safe candidates | 2048 | 4096 | production merge saving | final plan changes above 1024 |
| --- | ---: | ---: | ---: | ---: | --- |
| Seiran_Test_Atlas | 141 | 188 | 188 | 5,592,520 B | none |
| Koiboshi | 2 | 2 | 2 | 117,432 B | none |
| ShuwashuwaPearl | 2 | 2 | 2 | 196,960 B | none |
| FrillNyantic | 6 | 6 | 6 | 393,280 B | none |
| Cornet | 14 | 14 | 14 | 144 B | none |
| SailorCupid | 6 | 26 | 31 | 120 B | none |
| Octopus | 1 | 1 | 1 | 24 B | none |

Observed conclusion at this snapshot:
- 1024 is not candidate-complete in every fixture.
- No tested fixture improved its final selected plan at 2048 or 4096.
- 2048/4096 comparison searches add substantial diagnostic runtime cost.
- Current evidence supports retaining production budget 1024.

## Cleanup boundary

When removing the current investigation pack:

1. Remove comparison searches and their state/result DTOs.
2. Remove watched-path / rejected-candidate / candidate-diff formatting.
3. Remove standalone failure-detail strings if no active investigation needs them.
4. Remove detailed execution-effect summary if no active investigation needs it.
5. Remove temporary NDMF diagnostic UI only after all long-form reports are gone.
6. Preserve production exact selection, production state budget, execution safety, common fixes, and total processing-time log.
7. Run the safety tests after cleanup.
