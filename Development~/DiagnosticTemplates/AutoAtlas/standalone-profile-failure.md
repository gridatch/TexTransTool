# AutoAtlas Standalone Profile Failure

id: `autoatlas-standalone-profile-failure`  
status: `captured-active-at-snapshot`  
runtime_cost: `low`

## Purpose

Explain why a prepared atomic group has `standalone=(none)`, `baselineKnown=False`, and `profiles=[]` instead of collapsing all causes into one opaque result.

## Source anchors

`Editor/NDMF/AutoAtlasTextureMergePlanner.cs`
- `PreparedGroup.StandaloneAnalysisDiagnostics`
- `AnalyzeAtlas(...)`
- `AtlasTexture.PrepareAtlasContext(...)`
- `AtlasTexture.TryAnalyzePreparedAtlasAtSize(...)`
- `AutoAtlas2DMergeIslandRelocator.IsNfdhComparerFailure(...)`.

`Editor/NDMF/AutoAtlasTexturePass.cs`
- prepared-group report formatting
- conditional `analysis=[...]` output for groups with no standalone metrics.

## Patch sources

- `6542f9b2a45c3ecefb3634b76541b3a2ae3cc521` — collect profile failure causes.
- `eec70ad8b4cf5488bc415e74f16bf038d0844bbc` — expose them in the report.

## Output contract

For a group with no standalone lossless profile, report a compact ordered trace such as:

```
analysis=[
  prepare=ok,
  256=lossless-relocation-failed,
  512=lossless-relocation-failed,
  1024=lossless-relocation-failed,
  2048=lossless-relocation-failed,
  4096=lossless-relocation-failed
]
```

Recognized states:
- `prepare=failed`
- `prepare=ok`
- `<size>=success(<layout>,usedHeight=<n>)`
- `<size>=lossless-relocation-failed`
- `<size>=nfdh-comparer-failure`.

The trace is diagnostic. It must not make a failed profile acceptable.

## must_not_change

- AutoAtlas remains lossless-only for these profile probes.
- Failure at one candidate size must not silently enable scaling or texel-density reduction.
- A group with no valid standalone profile remains ineligible for merge planning that requires standalone/baseline cost.
- Existing fail-closed behavior remains intact.
- NFDH comparer failures are reported/rejected, not converted into success.

## validation

Known captured fixtures:
- Seiran `g8: 1_Blue, 2_Gray, 3_Dark Blue, 4_Light Blue`: prepare succeeds; all 256–4096 lossless relocations fail.
- ShuwashuwaPearl `g11: Lace 1`: same classification.

These observations establish that the condition is not unique to one fixture. They do not prove that no theoretical packing exists; they prove only that the current lossless relocation path did not find one.

## activation_recipe

1. Add a per-prepared-group diagnostic trace container.
2. Clear it before profile analysis.
3. Record prepare success/failure.
4. For every AutoAtlas size candidate, record success, ordinary lossless relocation failure, or known NFDH comparer failure.
5. Emit the trace only when standalone metrics are absent, unless a broader profile investigation explicitly requires successful traces too.

## removal_contract

Remove:
- `StandaloneAnalysisDiagnostics`
- trace writes inside `AnalyzeAtlas(...)`
- `analysis=[...]` report formatting.

Preserve:
- profile analysis itself
- lossless-only behavior
- NFDH failure safety behavior
- standalone baseline semantics
- any independent production fix discovered while investigating a failed profile.
