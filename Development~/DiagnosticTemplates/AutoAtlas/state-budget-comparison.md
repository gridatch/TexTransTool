# AutoAtlas State Budget Comparison

id: `autoatlas-state-budget-comparison`  
status: `captured-active-at-snapshot`  
runtime_cost: `very-high`

## Purpose

Compare production 2D packing search retention against alternate state budgets without changing the production plan. Diagnose whether a production-selected candidate is lost during generation, retention, candidate gating, or final selection.

Default comparison set at the captured snapshot:
- 512
- production 1024
- 2048
- 4096

## Source anchors

`Editor/NDMF/AutoAtlasTextureMergePlanner.cs`
- `ShadowPackingStatesPerHost` — production budget; permanent production behavior.
- `DiagnosticStateBudgets` — temporary.
- `DiagnosticGeneratedStateLimit` — temporary safety cap for widened probes.
- `SearchBudgetComparison`
- `SearchBudgetProbe`
- `WatchedPathDiagnostic`
- `WatchedPathLossStage`
- `EvaluateShadow2DPacking(...)`
- retention-rank calculations and watched-path ancestry tracking.

`Editor/NDMF/AutoAtlasTexturePass.cs`
- formatting sections for:
  - production 1024 search
  - comparison probes
  - candidate differences
  - watched path
  - rejected 4096-width candidates.

## Patch sources

Use these commits as historical delta sources only; adapt against current source rather than cherry-picking blindly.

- `6f536b3b2ab34187672e903cefa4f837997956dd` — initial 512 vs widened search comparison.
- `890a3bacb2ff28a06ff38211f55ffe7299b09129` — bound widened probes.
- `7b47013cfc3ac5a145b9dd450f95b4f20a3b0e77` — preparation / rejection tracing.
- `38654ebf24f311a61e730319ad4414a194638a3a` — production budget raised to 1024; this production change is not temporary.
- `ef96218494f7b55c3b623459d071f168eb7a4e85` — explain selection and retention effects in the report.

Older related diagnostic lineage, useful only when reconstructing retention research:
- `98470ffb76293b2147e1c8191cf689c4a461782e`
- `2489e64461a38a93de169cf2d7417c3c0a8bffb`
- `b0982afc8dec7b8d27c7efa61f6ba98ebbd38b47`

## Output contract

For production and each comparison budget report:
- state budget
- search milliseconds
- safe candidate count
- selected merge count
- predicted selected saving
- maximum states before retention
- cumulative pruned state count.

For each comparison budget:
- whether final adopted plan equals production
- comparison-only/new-or-better safe candidates
- production-only/production-better safe candidates
- watched path for every production-selected merge:
  - target group IDs
  - target layout
  - loss stage
  - whether exact candidate was found
  - whether selected
  - first loss expansion group/prefix when retention drops it
  - retention rank by scoring quota when available.

Comparison searches are planning-only. They must never mutate meshes, materials, textures, or production selection.

## must_not_change

- Production application uses only the production 1024-state result.
- `ShadowPackingStatesPerHost = 1024` is not changed by activating this diagnostic.
- Candidate geometry/cost/safety evaluation must be identical between production and comparison probes.
- No extra `DoAtlasTexture` execution for comparison candidates.
- Diagnostic generated-state cap may abort only a comparison probe, never production.
- Total AutoAtlas processing-time log remains present.

## validation

A valid run must make it possible to answer:
1. Did the comparison budget reproduce the exact production candidate?
2. If not, at which stage was it first lost?
3. Did extra candidates actually improve the final selected plan?

Captured multi-project result:
- Seiran: 1024=141 candidates; 2048/4096=188; final plan unchanged.
- SailorCupid: 1024=6; 2048=26; 4096=31; final plan unchanged.
- Other benchmark fixtures: no final plan change across tested budgets.

## activation_recipe

1. Resolve current production packing entry point and retention function.
2. Run production once and retain its safe candidates/selections as the watch targets.
3. Run alternate budgets through the same planning path with mutation disabled.
4. Instrument retention ancestry only for production-selected group/layout/saving targets.
5. Build `SearchBudgetComparison`.
6. Append report sections through the long-form diagnostic report.
7. Use `ndmf-console-exact-copy` presentation when the report is shown in NDMF Console.

## removal_contract

Remove:
- alternate 512/2048/4096 search execution
- `DiagnosticStateBudgets`
- `DiagnosticGeneratedStateLimit` if unused elsewhere
- comparison DTOs
- watched-path state/ranks
- candidate-diff/rejection report sections used only by this investigation.

Preserve:
- production 1024 search
- production candidate generation/retention behavior
- exact selection production algorithm
- candidate safety gates
- all unrelated fixes discovered during the investigation.

Do not revert the historical diagnostic commit range wholesale.
