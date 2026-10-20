# WDT Maintainer Context

> This is the single normative maintainer-context document for WDT-specific development.
> Read this file **in full** before investigating, designing, implementing, refactoring, or auditing WDT behavior.

## 0. What this document is for

The source tree tells us **what the current implementation does**. This document preserves the parts that are easy to lose when reading only the final code:

- feature intent and responsibility boundaries;
- accepted invariants;
- reasons for non-obvious special handling;
- implementation history that materially constrains future changes;
- rejected or superseded designs that must not be revived accidentally;
- regression-sensitive behavior;
- what each validation level does and does not prove.

This file is versioned with the implementation. It must not be used as a substitute for checking the current Git branch, HEAD, PR, release, or working tree.

Do not add chronological debugging diaries here. Preserve only conclusions that remain relevant to maintainers.

## 1. Sources of truth and conflict handling

Use these sources for different questions:

1. **Current implementation:** the current source tree.
2. **Accepted long-lived design intent/invariants:** this document.
3. **Current work position:** Git/GitHub plus the active conversation handoff.
4. **Validation status:** the exact validation evidence recorded for the change being evaluated.

If they appear inconsistent, investigate the discrepancy. Do not silently choose one source and do not "simplify" apparently redundant code before checking why it exists.

In particular:

- a merged PR body may describe an earlier state later changed by follow-up commits;
- an unmerged PR is evidence of investigation, not accepted specification;
- a debug package build is not Unity compilation;
- the existence of NUnit/EditMode tests is not evidence that they were executed;
- an old audit branch is not a source of truth merely because it contains apparently stricter checks.

## 1.1 Critical invariants — establish these before interpreting implementation details

These are deliberately repeated here even though later sections explain them in detail.

- **AutoAtlasTexture is normal AtlasTexture + automatic selection + lossless automatic optimization.** It is not a separate shader-compatibility proof system.
- AutoAtlas-only restrictions require a concrete failure introduced by AutoAtlas itself. Shared AtlasTexture defects belong in shared AtlasTexture.
- Texture merging may be stricter than standalone AutoAtlas only where merging atomic groups introduces new semantics/costs; failed merged execution falls back to standalone atomic groups.
- AutoAtlas renderer eligibility follows normal AtlasTexture's default targeting semantics, including `IncludeDisabledRenderer=false`.
- Lossy texture resizing is opt-in. The old default 512px Resize was removed when property-aware lossless output sizing became shared Atlas behavior.
- Independently tiled lilToon `_Bump2ndMap` preservation is a shared normal-Atlas rule that predates AutoAtlas.
- The user-facing total AutoAtlas processing-time log is permanent production behavior, not temporary diagnostic instrumentation.
- Persistent Bake intentionally updates one stable named output with manifest/GUID preservation and rollback. Part Atlas intentionally creates new versioned standalone generations instead of overwriting an older generation.
- PR #24 and old rejected-audit branches are investigation history, not current AutoAtlas specification.
- Archived `Development~/DiagnosticTemplates/AutoAtlas/` files are reconstruction references. Only behavior explicitly classified as permanent remains normative.
- GitHub package/debug workflow success does not prove Unity compilation, Unity Test Runner/NUnit execution, or visual/runtime correctness.
- WDT source must remain compatible with the Unity project's C# 9 language level unless the project/toolchain requirement is deliberately changed. Do not introduce C# 10-only syntax such as parameterless struct constructors merely because the local compiler accepts it.

## 2. WDT extension map

Relative to upstream `master`, WDT adds or changes these major areas:

1. WDT VPM/debug/stable release workflows.
2. Shared/manual AtlasTexture correctness and shader-information fixes.
3. Automatic minimum lossless AtlasTexture sizing.
4. Property-aware lossless atlas output resolution.
5. Atlas resolution-route and texture-memory reporting.
6. Persistent Atlas bake.
7. Standalone Part Atlas Prefab extraction/atlas workflow.
8. Part Atlas hierarchy, 3D preview, and native Inspector UX.
9. AutoAtlasTexture automatic material selection and lossless execution.
10. Optional AutoAtlas texture merging into unused atlas space.
11. Shared Atlas material/mesh/resource safety required by AutoAtlas.
12. Regression tests and archived AutoAtlas diagnostic templates.

The important architectural distinction is:

- **normal AtlasTexture** is the shared atlas implementation;
- **AutoAtlasTexture** reuses normal AtlasTexture semantics and adds automatic selection/lossless acceptance;
- **texture merging** is a second-stage AutoAtlas optimization that may combine atomic groups that standalone AutoAtlas would keep separate;
- **Persistent Bake** persists normal AtlasTexture output into project Assets and rewires the existing hierarchy;
- **Part Atlas** is a standalone Prefab-extraction workflow that reuses the persistent Atlas core after Matsukawa extraction.

Do not collapse these into one feature.

---

# 3. Shared / normal AtlasTexture

## 3.1 Shader texture-property enumeration

### Accepted behavior

Atlas material grouping must query only texture properties that the material/shader actually exposes.

Current shader information may describe properties not present on a particular shader variant. `MaterialGroupingContext` therefore intersects shader-information usage with the material's actual texture-property names and only reads supported non-null textures.

### Why this exists

PR #4 added the first guard after Unity errors from unavailable shader texture properties.
PR #10 finalized the behavior by enumerating actual exposed texture properties rather than trusting the metadata property list.

This is a **shared AtlasTexture correctness rule**, not an AutoAtlas-only compatibility gate.

Do not replace it with a broad "unknown shader = reject AutoAtlas" policy.

## 3.2 Shader UV usage metadata

WDT retains UV-transform information in shader usage metadata. `MaterialUVUsageProvider` resolves texture `_ST` through `GetTextureScale` / `GetTextureOffset`, and `MaterialInformationTranslator` carries both UV-channel and transform information.

The registry falls back to `_MainTex` on UV0 for an unregistered shader. Registered shader information is therefore an improvement over the fallback, not a proof system that AutoAtlas is allowed to run.

### lilToon selector corrections

The current shared lilToon information includes concrete selector fixes retained by PR #26:

- `_Main3rdTex` uses `_Main3rdTex_UVMode`;
- `_GlitterColorTex` uses its own `_GlitterColorTex_UVMode`;
- `_OutlineVectorTex` uses `_OutlineVectorUVMode`, not `_AudioLinkMask_UVMode`.

These are corrections to common shader information used by normal AtlasTexture. They are not AutoAtlas-only safety checks.

### lilToon AudioLink metadata sync

Commit `f1fec67f` also synchronized concrete AudioLink UV-use behavior from later Avatar Optimizer fixes, including the relevant AudioLink feature gates, mask UV selectors/transform, and local-map non-mesh usage.

This is shared shader metadata maintenance. It must not be confused with the later rejected attempt to make "known complete shader semantics" an AutoAtlas permission gate.

### VRChat Toon Standard

PR #12 added Toon Standard / Toon Standard Outline texture-usage mappings based on Avatar Optimizer's shader information because fallback `_MainTex`-only behavior was insufficient.

The mapping covers the shader's relevant normal/specular/matcap/emission/occlusion/detail/hue/color/AudioLink/outline texture usages according to their selector properties.

This support belongs to shared AtlasTexture behavior.

Commit `993b7293` later moved the Toon Standard WDT extension into its own source file to isolate WDT-specific changes. That move was source organization, not a change in the accepted Toon Standard behavior.

## 3.3 Independently tiled lilToon second normal

### Invariant

An independently tiled `_Bump2ndMap` must not be forced through normal atlas UV0 transformation.

When `_Bump2ndMap` uses the atlas target UV channel with a non-identity UV transform:

1. exclude that property from atlas texture generation for the affected material;
2. find a **completely unused** UV channel among supported spare channels;
3. copy the original UV0 into that spare channel;
4. switch `_Bump2ndMap_UVMode` to the preserved channel;
5. preserve the original second-normal texture/tiling on generated materials;
6. abort atlasing if no safe spare UV exists.

### History

PR #5 experimented with general texture-ST baking and was **not merged**.
PR #11 deliberately replaced that general approach with the narrower second-normal preservation strategy after Unity validation.

Do not revive PR #5's general ST-baking design merely because the shared code still tracks UV transforms.

This behavior predates AutoAtlas and must not be reclassified as an AutoAtlas-specific problem.

## 3.4 Automatic minimum lossless atlas size

PR #14 introduced `AtlasSetting.AutoAtlasTextureSize`.

When enabled, normal AtlasTexture tries real island relocation at:

`256 -> 512 -> 1024 -> 2048 -> 4096`

and accepts the first candidate where island placement requires no priority or overall downscale.

This is based on the **actual packing result**, not source texture dimensions.

For ordinary/manual AtlasTexture, if even 4096 is not lossless, 4096 remains the fallback with a warning and normal scaling behavior.

For callers that set `AtlasExecutionOptions.RequireLossless`, failure to pack losslessly is a normal failure result and no atlas mutation is applied.

New `AtlasSetting` instances default automatic minimum lossless sizing to enabled. Existing serialized values remain whatever Unity serialized.

Do not add 8192 to this production search without a separate accepted design change. The old experimental PR #24's 8192 idea was not adopted.

## 3.5 Property-aware lossless output resolution

Commit `68166b5c` changed output sizing from a uniform atlas texture size to a per-property resolution plan.

### Invariant

Automatic lossless sizing means not only "islands fit without UV downscale", but also that each generated texture property retains the texel density required by its source texture relative to the group's primary texture.

`AtlasTextureResolutionPlan`:

- computes an output size independently for each generated texture property;
- uses the largest density requirement across contributing groups;
- may reduce a property's dimensions below the layout dimensions when source density permits;
- will not shrink below 4 pixels in a dimension;
- will not shrink below the layout when primary-texture density is unknown;
- can grow a property size if required to preserve density.

When automatic sizing is disabled, legacy uniform layout-sized outputs are retained.

### Why the old default Resize disappeared

Before this change, AutoAtlas initially removed the regular default 512px `Resize` FineTuning as a special case.
Commit `68166b5c` removed that lossy default `Resize` from **normal AtlasTexture settings themselves** and replaced the need for it with the property-aware resolution plan.

Therefore current behavior is:

- lossy resizing is opt-in;
- AutoAtlas no longer needs a special "remove default Resize" step;
- reintroducing a default 512px lossy Resize would conflict with the accepted lossless design.

This history is regression-sensitive because old AutoAtlas commits still contain the now-obsolete special-case removal.

## 3.6 Reusable Atlas execution boundary

`AtlasTexture.DoAtlasTexture` is intentionally reusable by normal AtlasTexture, AutoAtlas, and Persistent Bake.

`AtlasExecutionOptions` provides controlled seams including:

- `RequireLossless`;
- progress-log suppression;
- optional forced automatic atlas width;
- optional island-relocator factory;
- optional final candidate acceptance callback.

It also returns execution metrics including final layout and per-property sizes.

These seams were expanded for AutoAtlas merging but are shared Atlas infrastructure. Do not duplicate the atlas implementation in AutoAtlas or Part Atlas.

## 3.7 FineTuning provenance

`TexFineTuning` tracks source-property provenance.

This is necessary because FineTuning can alter the set of final outputs:

- `ReferenceCopy` may create a target output even when that target was not in the atlased UV property set;
- `MergeTexture` combines source-property provenance into the merged holder and aliases.

`AtlasResolutionRouteReport` uses this provenance to report actual input-resolution composition to final output resolution.

This provenance is also relevant to AutoAtlas merge semantics. Do not infer final output properties only from pre-FineTuning `MaterialGroupingContext`.

## 3.8 Equivalent final-output sharing

PR #30 introduces shared AtlasTexture deduplication for texture properties that would otherwise materialize byte-equivalent physical atlas textures.

This is **not** "same source Texture means always share". Automatic ReferenceCopy is added only when equivalence is proven across the selected materials:

- the exact same source Texture reference is used by both properties;
- generated atlas resolution is identical;
- UV channel and statically known UV transform are identical;
- descriptor-only FineTuning is equivalent, including ColorSpace, MipMap settings/algorithm, and Compression settings;
- neither property is already an explicit ReferenceCopy target or a MergeTexture participant;
- pixel-changing/output-removing FineTuning such as Resize, DiscardAlphaChannel, and Remove is not present;
- unknown/future tuning-data types fail closed.

The canonical output is chosen deterministically, preferring an existing ReferenceCopy source and then `_MainTex`.

AutoAtlas merge cost projection must model the same sharing rule; otherwise standalone baseline memory would be overestimated and merge benefit could be overstated.

Validated reproduction:
- `Seiran_Test_Atlas`: redundant 2048² `_OutlineTex` output was removed; `4.1 MiB -> 6.8 MiB` became `4.1 MiB -> 4.1 MiB`.
- `Seiran_SailorCupid` / `Mat_Skirt_White_Blue_custom`: `2.0 MiB -> 1.0 MiB`, matching the 50% output-area reduction.
- User smoke/visual validation reported no behavioral or visual issue before the final conservative UV-equivalence guard was added. Final Unity compilation/smoke validation of that guard remains pending until PR #30 is re-tested.

## 3.9 Resolution and memory reporting

Normal AtlasTexture reports:

- final resolution routes, aggregated and sorted;
- missing source slots as `0x0` inputs when applicable;
- source -> final target texture memory when both sides can be calculated.

Memory calculation uses actual block dimensions/block sizes and mip counts where available.

Unknown/unsupported memory-layout information is not silently guessed for safety decisions.

## 3.10 Empty/effective submesh handling

`AtlasSubMeshIndexIDSetContext.BuildAtlasSubSet` and mesh resolution intentionally follow Unity's effective material/submesh behavior.

Important cases:

- mesh with no submeshes: no atlas subset;
- target material on an empty effective submesh: no generated atlas geometry required;
- material slots beyond the submesh count follow the effective last-submesh behavior;
- an all-null atlas subset is not registered.

This was hardened during AutoAtlas merge work because domain-wide material replacement paired with a missing UV rewrite can corrupt unrelated renderer output.

Do not revert this as "AutoAtlas-only defensive code"; it is a shared Atlas correctness fix.

## 3.11 Borrowed runtime texture ownership

`UnityDiskUtil.Dispose` must not destroy a runtime texture that was borrowed from upstream and returned as the same reference.

PR #25 isolated this into the shared resource-safety commit because destroying borrowed runtime inputs is independent of texture merging.

Do not change ownership cleanup based only on whether an input is non-persistent; reference identity matters.

## 3.12 NDMF preview `WithData` comparer compatibility

`PreviewIslandSelector` and `TexTransDomainFilter` are part of the WDT diff because PR #15 changed their `RenderGroup.WithData` calls to pass explicit equality comparers.

This is NDMF preview/API compatibility support that happened in the Persistent Bake changeset; it is **not** part of Persistent Bake's product semantics. Do not use those file changes as evidence that Persistent Bake owns or changes ordinary NDMF preview targeting.

---

# 4. Persistent Atlas Bake

## 4.1 Purpose

Persistent Bake is an Editor-only path launched from the normal `AtlasTexture` Inspector.

It applies normal AtlasTexture processing to the existing hierarchy and persists generated assets under:

`Assets/TexTransToolGenerated/AtlasTexture/<BakeName>/`

It does **not** replace the GameObjects, Renderers, or surrounding components. It changes the relevant Mesh/Material references.

Normal NDMF AtlasTexture processing is unaffected by bake-only settings.

## 4.2 Component-independent bake core

`PersistentAtlasBakeSettings` snapshots the settings needed by the bake core.

This separation was introduced so two callers can share the exact same persistent atlas implementation:

- normal `AtlasTexture` Inspector bake;
- Part Atlas's extracted temporary hierarchy.

Part Atlas therefore does not need an `AtlasTexture` component.

## 4.3 Target resolution

Normal Persistent Bake obtains candidates through normal AtlasTexture target resolution, then applies `BakeExcludedRenderers`.

The bake exclusion list is bake-only. It must not silently alter ordinary NDMF AtlasTexture targeting.

## 4.4 Manifest-managed output and GUID preservation

Each named bake has `BakeManifest.asset`.

Manifest roles identify generated textures, materials, and meshes semantically.

On same-name rebake:

- existing managed assets with the same role are updated in place;
- their path/GUID identity is preserved;
- new roles create new assets;
- stale managed roles are deleted;
- the output folder is not blindly deleted and recreated;
- overwrite aborts if the existing output has no compatible manifest;
- manifest paths outside the managed output folder are never treated as deletable managed assets.

The reason is to keep external Unity references stable without doing a project-wide reverse-reference scan.

## 4.5 Safety validation

Before applying persistent results, the bake validates at least:

- target renderers are inside the supplied domain;
- AnimationClip object-reference bindings that directly reference targeted meshes/materials/textures;
- every required renderer resolves to a generated atlas mesh;
- blendshape count/order/name/frame count/frame weights remain compatible;
- skinned bindpose count remains compatible.

Renderer state is captured before reference application and restored if application throws.
Asset output is guarded by an output transaction.

The Persistent Bake hardening commit `8a3c5071` made the destructive boundary explicit:

- reject incompatible manifest versions;
- reject manifest-managed paths outside the bake output root;
- include source textures in direct AnimationClip object-reference validation;
- resolve animation binding paths against the exact renderer paths;
- back up an existing same-name bake output before mutation;
- restore the previous output and renderer references when the transaction fails.

This rollback model remains appropriate here because Persistent Bake intentionally updates one stable named output.

These checks are intentional fail-closed boundaries for a destructive/persistent workflow.

## 4.6 Texture importer fidelity

Persisted textures retain descriptor-driven mip/filter/aniso/wrap/compression behavior.

Normal-map output is inferred from:

- FineTuning descriptor role;
- source importer normal-map role;
- shader normal-texture metadata when descriptor role alone is insufficient.

Normal-map outputs are imported as `TextureImporterType.NormalMap` with appropriate linear handling before generated materials are finalized.

The importer `maxTextureSize` may be clamped up to 8192 to represent a persisted texture asset. This must **not** be confused with the rejected proposal to search 8192-sized AutoAtlas layouts.

## 4.7 History

PR #15 replaced older "Export Atlas Assets" / "existing part replacement" concepts with this named manifest-managed bake workflow.

PR #17 refactored the bake core to accept component-independent settings so Part Atlas could reuse it.

Later WDT changes hardened safety, canonical texture names, normal-map import, and shared target/mesh resolution without changing the core responsibility.

---

# 5. Part Atlas Prefab workflow

## 5.1 Purpose and architecture

Part Atlas is a **standalone Prefab extraction tool**, not an AtlasTexture component workflow.

Current `wdt` menu entry:

`Tools/TexTransTool/WDT/Prefab抽出・アトラス化...`

Merged UI cleanup from PR #28 changed the display path to:

`Tools/TexTransTool/Prefab抽出・アトラス化 [WDT]`

The `[WDT]` marker is intentionally a suffix so menu sorting follows the original feature name rather than the provenance marker.

Validation: the suffix menu placement and Part Atlas UI hardening from PR #28 were confirmed in Unity by the user on 2026-10-20; compilation, display, and basic operation were reported working. PR #28 was then merged into `wdt` as merge commit `01fc1cda6289a192702307ce27b6ab3b696ad4fe`.

Architecture:

1. user selects a Project Prefab Asset;
2. user selects one or more extraction target GameObjects;
3. user selects retained Renderers;
4. optional Atlas target Materials are selected;
5. TTT performs source-side preflight;
6. TTT stages an isolated copy in a temporary additive Scene;
7. Matsukawa Hair/Costume Extractor performs dependency analysis/extraction;
8. TTT verifies trim/reference results;
9. TTT optionally restores Modular Avatar attachment metadata;
10. TTT optionally runs the shared persistent Atlas bake core;
11. superseded HCE trim meshes are removed only when no longer referenced;
12. Matsukawa saves the final Prefab/report.

The source Prefab Asset is never the destructive working object.

## 5.2 Why Matsukawa is an external dependency

PR #16 was only an integration probe.

PR #17 adopted architecture option B:

- TTT owns UI/orchestration;
- Matsukawa Hair/Costume Extractor Ver1.1.0 remains separately distributed;
- TTT calls Matsukawa through reflection;
- Matsukawa is not bundled or modified.

`MatsukawaAdapter` deliberately requires only the public APIs actually used by the workflow and checks their types/signatures strictly.

Do not reimplement Matsukawa's extraction/dependency engine inside TTT unless a new architecture decision explicitly replaces this boundary.

## 5.3 Extraction scope

Prefab Root itself is not an extraction target.

Multiple target paths are supported. Parent/child overlap is normalized so selecting a parent subsumes selected descendants.

Execution uses a temporary staging container so Matsukawa can analyze multiple detached targets while the complete source Prefab remains available for dependency analysis.

Renderer identity is captured before staging, so temporary hierarchy paths do not alter the user's retained-renderer semantics.

Nested Prefab instances are completely unpacked only in the isolated working copy. Preview and execution share this staging assumption.

## 5.4 Extraction can run without Atlas

No Atlas target material is required.

If no Atlas materials are selected:

- Atlas-specific preflight is skipped;
- Persistent Bake is skipped;
- the extracted Prefab is saved directly.

If one or more Atlas materials are selected, Atlas target validation remains strict.

This distinction is intentional; Part Atlas is an extraction workflow with optional atlasing, not an "Atlas must always run" tool.

## 5.5 Avatar and Modular Avatar adaptation

When the source is detected as an Avatar:

- avatar-root Animator / AvatarDescriptor / Pipeline components are stripped from output;
- references owned by those components are not treated as surviving-reference failures when stripping is enabled;
- if extracted content depends on the avatar's Humanoid armature, output can be configured with MA Merge Armature + MA Outfit Root;
- when a selected target was attached under a Humanoid bone, a missing MA Bone Proxy can be reconstructed;
- existing authored Bone Proxy settings are preserved rather than overwritten.

If required MA capabilities are unavailable, the workflow aborts instead of silently producing unattached output.

### Compatibility risk

Unlike Matsukawa integration, `ModularAvatarAdapter` currently reflects several Modular Avatar internal/non-public implementation details, including fields such as `mergeTarget`, `prefix`, `suffix`, `LockMode`, `armatureRoot` and non-public `ResetArmatureLock()`.

This is known compatibility fragility. It is a candidate for a separate compatibility audit; do not confuse that future audit with AutoAtlas work.

## 5.6 Reference safety and HCE-result validation

The pipeline checks references both before and after extraction.

Important behavior:

- generic serialized references to objects/components HCE will remove cause abort unless an explicitly modeled safe case applies;
- SkinnedMeshRenderer bone references are not naively rejected because HCE owns bone trimming/rewrite semantics;
- Modular Avatar cached `targetObject` may be cleared only when a non-empty path reference provides the durable reference;
- HCE trim result bone counts and persistent generated-mesh paths are verified;
- final output is checked for direct Scene references outside the standalone Prefab;
- temporary duplicate-name trim meshes must be consumed, otherwise abort.

### Known fail-closed boundary requiring separate review

If `SerializedObject(component)` cannot be created, or a component exceeds the `SerializedPropertyScanLimit` of 10,000 during generic reference scanning, the current implementation records an issue and aborts.

This is **current behavior**, but its false-positive cost has not been fully re-audited.

A later fix (`7a8a2254`) explicitly modeled components HCE removes and reduced prior false positives, proving that generic "cannot prove safe = reject" logic can be over-conservative here.

Therefore:
- do not silently relax the current checks;
- also do not cite them as an unquestionable project-wide design principle;
- reevaluate them only as a scoped Part Atlas safety task.

## 5.7 Output generation and failure cleanup

Current standalone Part Atlas does **not** overwrite an existing same-name generation.

Each execution creates:

`<LogicalName>_extracted_<yyyyMMdd_HHmmss>`

with `_01`, `_02`, ... collision suffixes as needed.

The timestamped generation name is used for the generated folder/root/Prefab. Logical output name is still used for Atlas child asset naming inside that generation.

On failure, only the newly created generation folder is deleted.

### Superseded design

Early PR #17 implementation used same-name overwrite, folder backup outside Assets, rollback, and Trim Mesh GUID restoration.

PR #17's later accepted follow-up **removed** that overwrite model in favor of versioned standalone generations.

Do not restore backup/overwrite machinery based on the early part of PR #17's description.

Normal Persistent Bake still uses same-name manifest-managed updates; that is a different workflow.

## 5.8 UI ownership and settings parity

The window owns a transient `PartAtlasPrefabSettings : ScriptableObject`.

It deliberately:

- is not an AtlasTexture component;
- never participates in NDMF;
- shares normal AtlasSetting drawing through `AtlasSettingsEditorGUI`;
- supports only hierarchy-independent `SetFromMaterial` island-size priority entries;
- does not expose normal AtlasTexture's `IncludeDisabledRenderer`, because Part Atlas renderer retention is explicit.

The visible workflow is organized as:

- 入力
- 抽出
- アトラス化
- 実行

## 5.9 Hierarchy, 3D preview, and Inspector

### Read-only hierarchy model

The Prefab hierarchy is a tool-owned TreeView, not Unity global Selection.

It supports:

- extraction-target checkboxes;
- keep/delete/protected analysis colors;
- navigation from renderer/protected-object rows;
- search that preserves ancestor context and original depth;
- horizontal scrolling for arbitrarily deep search paths;
- selection restore/reveal after clearing search;
- no rename/edit operation.

PR #23 added the depth-preserving search behavior. Its PR body records static review; do not automatically promote that PR's planned Unity validation to completed evidence.

### 3D preview

PR #20 added a read-only extraction-target preview.

`PartAtlasPrefabPreview`:

- does not run source GameObjects, Animator, Matsukawa, or Atlas processing;
- snapshots SkinnedMeshRenderer geometry with `BakeMesh`;
- borrows static MeshRenderer meshes;
- uses existing materials;
- renders only the selected/retained extraction meshes;
- provides 360-degree horizontal orbit;
- uses yaw-independent orthographic fitting so orbit does not change apparent zoom;
- owns/destroys only its temporary skinned snapshots and preview resources.

This was user-validated in Unity on the PR's debug build.

### Native Inspector

PR #21 added a third pane hosting actual Unity/custom component Editors through `InspectorElement(Editor)`.

The Inspector works on a separately loaded disposable Prefab contents copy.

This protects the source Prefab hierarchy from edits made through the Inspector copy, including duplicate-name mapping through sibling-index identity.

However, arbitrary third-party `CustomEditor` code can still mutate external referenced assets. Do **not** describe this as a universal sandbox or truly read-only enforcement.

---

# 6. AutoAtlasTexture

## 6.1 Core product invariant

> **AutoAtlasTexture = normal AtlasTexture + automatic selection + lossless automatic optimization.**

AutoAtlas is not a second shader-compatibility system.

If normal AtlasTexture can process an input, AutoAtlas must not reject it merely because AutoAtlas cannot exhaustively prove future/unknown shader semantics.

An AutoAtlas-specific exclusion is justified only by a concrete failure introduced by AutoAtlas's own automatic behavior.

Explicit `ExcludedRenderers` / `ExcludedMaterials` exist for real asset-specific exceptions.

This invariant is the main corrective conclusion of PR #26 and the rejected-audit incident.

## 6.2 History boundary: PR #24 is not the current design

PR #24, `WIP: Auto Material Atlas — conservative analysis and NDMF reporting`, was **never merged**.

It explored:
- strict same-shader/non-texture-state compatibility;
- broad animation/reference exclusions;
- possible 8192 lossless search;
- effective-property/shader-semantic proof ideas;
- analysis-only candidate reporting.

The experiments were useful diagnostics but did not become the current AutoAtlas specification.

Current AutoAtlas was introduced separately by commit `29a83b28` and subsequently evolved through property-aware sizing, PR #25 merging, and PR #26 corrective re-audit.

Do not cherry-pick or recreate PR #24's conservative conditions simply because they look safer.

## 6.3 Component and execution placement

`AutoAtlasTexture` is a stable component added from the same TTT Hierarchy/GameObject creation path as AtlasTexture. Merged UI cleanup from PR #28 displays it there as `TTT AutoAtlasTexture [WDT]`; `[WDT]` is a suffix so the original feature name continues to determine menu sorting, while `AddComponentMenu` / serialized component identity remain unchanged.

It has:

- island padding;
- general excluded renderers/materials;
- texture-merging enable switch;
- optional visibility-animation merge exclusion;
- texture-merging-only excluded renderers/materials.

The component has no ordinary per-renderer preview button because it is a build-time, domain-wide operation.

Only one active AutoAtlasTexture configuration is supported per avatar; multiple active configurations cause a warning and no AutoAtlas execution.

AutoAtlas is intentionally not in the ordinary component-phase list.

In NDMF Optimizing it runs after explicitly configured normal AtlasTexture processing, then before TTT session end. The TTT optimizing plugin is ordered before Avatar Optimizer and the configured mesh-simplification/inventory plugins.

## 6.4 Renderer selection must match normal AtlasTexture defaults

This is a critical invariant restored by PR #26.

AutoAtlas candidate renderer eligibility follows normal AtlasTexture's default targeting semantics, including default `IncludeDisabledRenderer=false`.

Do not broaden AutoAtlas to disabled/inactive renderers merely because a domain-wide material reference exists.

General `ExcludedRenderers` are then removed from that normal-default population.

Target renderers are resolved through the shared Atlas filters and must possess the required UV0 channel.

AAO negotiation uses the same AutoAtlas-allowed renderer population when exactly one Auto configuration exists.

## 6.5 Automatic material/group selection

AutoAtlas builds `MaterialGroupingContext` from materials actually used by the allowed renderer population.

It separately obtains `domain.GetAllMaterials()` for safety checks so materials referenced only through animation/domain state are not missed.

Materials explicitly selected by an active normal AtlasTexture are excluded from AutoAtlas to avoid double-processing.

AutoAtlas groups materials by connected source textures. These texture-connected sets are the **atomic standalone groups** used as the baseline for optional texture merging.

## 6.6 Active untransformed material consumers

`HasActiveUntransformedMaterialConsumer()` is a real correctness guard and must remain.

Reason:

- generated material replacement is domain-wide;
- if an active renderer shares the target material but cannot receive the matching atlas mesh/UV rewrite, it would receive an atlased material on unchanged UVs;
- that causes visible corruption outside the intended target.

Disabled/inactive consumers are intentionally ignored here to preserve normal AtlasTexture default targeting semantics.

This is not the rejected "include all disabled renderers" strategy.

## 6.7 Source texture exclusivity

`ReferencesAreExclusive()` is also intentional.

A source texture scheduled to disappear from the standalone AutoAtlas group must not remain referenced by another material/property consumer outside the equivalent transformed use.

The check uses `GetAllMaterials()` plus shader-aware texture references, including references not discoverable merely from current renderer materials.

This protects both correctness assumptions and the memory-saving model.

## 6.8 Standalone AutoAtlas acceptance

Standalone AutoAtlas executes normal AtlasTexture with lossless placement required.

After FineTuning, standalone output is accepted only when final generated output pixels are positive and smaller than the group's original source-texture pixel total.

The post-FineTuning check is authoritative because FineTuning can remove/merge/create property outputs.

No standalone AutoAtlas shader-semantic proof is performed beyond normal AtlasTexture behavior.

## 6.9 Required generated mesh resolution

Before domain mutation, AutoAtlas verifies that every target renderer with drawable target-material geometry can resolve a generated atlas mesh.

A missing mesh result is harmless only where no drawable target geometry actually needs the UV rewrite.

This guard exists because material replacement is domain-wide and `ReplaceMesh` itself can otherwise skip a renderer silently.

## 6.10 Total processing-time log

The user-facing total AutoAtlas processing time is a required production feature.

It is emitted both for successful summaries and target-not-found cases.

It was removed during a rejected audit and explicitly restored. Do not treat it as temporary diagnostic output.

The production Japanese headers include:

- `自動アトラス再配置結果`
- `自動アトラス総処理時間`

---

# 7. AutoAtlas texture merging

User-facing Japanese terminology is **テクスチャ統合**. Do not introduce the Japanese UI/name `追加統合`.

Internally this is a second-stage optimization: it combines atomic groups that standalone AutoAtlas would process independently.

Because merging changes group composition, it may require stricter checks than standalone AutoAtlas **only for semantics newly introduced by that merge**.

## 7.1 Baseline

The baseline is the cost/result of running each texture-connected atomic group independently with standalone AutoAtlas behavior.

Savings already achieved by standalone AutoAtlas are not counted as texture-merging benefit.

Groups excluded only from texture merging remain eligible for standalone AutoAtlas.

## 7.2 Planning safety gates

A merge candidate is adoptable only when all current conditions pass:

1. atomic-group mapping remains executable;
2. merge-only ReferenceCopy semantics remain equivalent;
3. merged pixel count is strictly lower than standalone baseline;
4. merged full-resident texture memory is strictly lower than baseline;
5. per-group sampling cost does not regress.

A larger maximum texture dimension is **not by itself** a rejection if the actual pixel/memory/sampling conditions improve.

Streaming-mipmap prediction is not an adoption gate.

## 7.3 ReferenceCopy rule: merge-only semantics, not general shader proof

`PreservesReferenceCopySemantics()` returns true for a standalone group.

For merged groups it rejects only a concrete new rewrite:

- a group does not generate the ReferenceCopy source property when standalone;
- the merged set introduces that source property;
- the material has the ReferenceCopy target shader texture property;
- an actual `Texture2D` or `RenderTexture` is assigned to that target.

That case can cause FineTuning to create/apply a target output only because unrelated groups were merged.

Do **not** extend this check into:
- identity-UV proof;
- target texture equality proof;
- known shader-usage allowlists;
- raw-UV 0..1 proof;
- unknown-semantics rejection.

Those were part of the over-conservative audit that PR #26 removed.

## 7.4 2D packing

Production planning uses 2D rectangle packing with `ShadowPackingStatesPerHost = 1024`.

Each atomic group retains an independently valid rectangle/profile.

Execution uses `AutoAtlas2DMergeIslandRelocator`:

- validates region completeness, bounds, overlap, and material/island group mapping;
- scales each atomic group into local region coordinates;
- performs local NFDH;
- preserves the atomic group's physical pixel padding;
- allows ordinary outer-atlas-edge padding clipping;
- protects internal region boundaries from padding overlap;
- may translate the complete group without changing island size, rotation, or relative placement;
- fails the merge if the planned geometry cannot be reproduced.

## 7.5 NFDH comparer failure

TexTransCore's NFDH sort can violate comparer consistency when impossible/extreme island sizes cause its rounded difference comparator to overflow.

Two protections coexist:

- shared `IslandRelocationManager.RelocateSafely()` recognizes only candidates that are physically impossible for the current NFDH target and returns ordinary relocation failure before invoking the broken sort;
- the 2D merge relocator catches the specific NFDH inconsistent-comparer exception and fails the merge.

This is **not** a material exclusion for UVs outside 0..1.

For AutoAtlas, a failed size/merge can continue to a larger size or standalone fallback as appropriate.

## 7.6 Planner state budget and exact selection

The 2D search retains a bounded production state budget of 1024 per host.

Archived diagnostics established:

- 1024 is not candidate-complete in every fixture;
- tested 2048/4096 diagnostic searches found more candidates in some projects;
- no tested fixture changed the final selected plan above the production 1024 budget;
- higher budgets imposed substantial diagnostic runtime cost.

Therefore 1024 is an intentional production tradeoff, not an accidentally low constant.

Candidate selection is exact where bounded:

- exact weighted set packing by group-usage mask for bounded group universes;
- exact candidate-subset path for a small candidate set;
- bounded greedy fallback for large universes.

Do not replace this with an unbounded exact search.

## 7.7 Execution-time revalidation and fallback

A merge plan is only an optimization proposal.

Before mutation, execution revalidates:

- renderer set still matches the planned set;
- no active untransformed consumer appeared;
- actual lossless atlas layout fits planned rectangle/property-resolution envelopes;
- every required generated mesh resolves;
- post-FineTuning pixels still strictly beat standalone baseline;
- post-FineTuning full resident memory is known and still strictly beats baseline;
- generated atlas materials do not retain source textures that the cost model assumed removable.

If a merged execution fails any of these checks, its original atomic groups are queued for standalone AutoAtlas.

Unrelated work remains untouched.

## 7.8 Optional visibility-animation exclusion

`ExcludeVisibilityAnimatedItemsFromTextureMerging` is optional and default false.

When enabled, it detects actual changes to:

- Renderer `m_Enabled`;
- GameObject `m_IsActive` on the renderer or ancestors.

It checks all NDMF-remapped path aliases and ignores curves that remain at the current boolean state.

This affects **texture merging only**, not standalone AutoAtlas.

Do not broaden this into general material-property animation exclusion.

---

# 8. Rejected or superseded AutoAtlas safety designs

The following must not be reintroduced as "safer auditing" without a new, concrete failure case and design decision:

- AutoAtlas-only full shader-semantic proof interfaces as permission gates;
- lilToon current-version gates;
- lilToon official-shader GUID allowlists as AutoAtlas permission gates;
- rejecting unknown shader semantics while normal AtlasTexture would proceed;
- broad raw-UV semantic proof;
- VertexID-dependent AutoAtlas rejection;
- general material-property animation rejection;
- target UV outside 0..1 as a blanket material rejection;
- analysis of whether a shader might read the spare UV used to preserve `_Bump2ndMap` as an AutoAtlas-only rejection;
- general identity-UV-matrix proof infrastructure;
- streaming mipmap prediction/blockers;
- broad Modular Avatar material-reference exclusions;
- 8192 production atlas search;
- UV rotation;
- introducing a custom shader to make automatic merging possible;
- expanded visibility heuristics such as scale/blendshape/transparency;
- broadening AutoAtlas to include disabled renderers;
- unbounded exact merge search.

The guiding rule is not "never add safety checks". It is:

> Add an AutoAtlas-only restriction only for a concrete breakage introduced by AutoAtlas itself. Fix shared AtlasTexture bugs in shared AtlasTexture.

---

# 9. NDMF and Avatar Optimizer integration

Normal TTT Optimizing processing runs before AutoAtlas.

AutoAtlas then runs before TTT session end and before Avatar Optimizer according to plugin ordering.

`NegotiateAAOPass` handles skinned renderers requiring UV handling before later UV modification:

- existing normal AtlasTexture targets;
- AutoAtlas allowed targets when exactly one active Auto configuration exists.

AAO mesh-removal information can be converted into UV-island-removal information before atlasing.

Do not move AutoAtlas casually across these phases. Its material candidates intentionally see earlier TTT results while still preceding AAO optimization.

---

# 10. WDT packaging and release workflow

## 10.1 Package identity

WDT VPM packages use:

`net.gridatch.tex-trans-tool`

Source namespaces/assemblies remain upstream-compatible.

This separate package ID was introduced by PR #2 to avoid repository/package identity collision with official `net.rs64.tex-trans-tool`.

## 10.2 Why the workflow changed repeatedly

Early PR #1 published branch builds directly.
PR #3 moved WDT to stable-version semantics so VCC did not require prerelease visibility.
PR #6 stopped formal release publication on every work-in-progress commit.
PR #7 briefly used temporary Actions artifacts.
PR #8 replaced that with a rolling VPM validation channel.
PR #9 simplified that into rolling debug build metadata.
PR #13 added release-branch triggering.
PR #22 established the final WDT/upstream metadata format.

Do not infer current release policy from an early PR.

## 10.3 Current stable version format

Formal WDT versions use:

`X.Y.Z+wdt.ttt.A.B.C`

where `A.B.C` must match the upstream `package.json.version`.

Stable tags/ZIP/VPM versions use the same formal version.

Release branches use:

`release/X.Y.Z+wdt.ttt.A.B.C`

and must point exactly at the current `wdt` HEAD when the release workflow evaluates them.

Manual workflow dispatch from `wdt` is also supported.

A stable VPM version is immutable.

Stable publishing is retry-safe: if the GitHub Release tag already exists it may be reused only when it resolves to the same source commit. An existing package asset is reused for its checksum, a missing asset may be uploaded, and a tag pointing at a different commit is an error.

Last documented successful stable publication (verify current Git/VPM state before relying on this): on 2026-10-20, `0.1.19+wdt.ttt.1.0.2` was published from `wdt`/release commit `01fc1cda6289a192702307ce27b6ab3b696ad4fe`; the release branch and `wdt` matched exactly, the GitHub Release/VPM entry were created successfully, and the rolling debug entry was removed from VPM state.

## 10.4 Current rolling debug channel

Pushes to `fix/**` and `feat/**` create/update a rolling debug VPM build.

For metadata-formatted WDT bases, debug versions append:

`.debug.<serial>.<change>.<sha>`

to the existing metadata rather than introducing a second `+`.

Only the current rolling debug version/ZIP is retained in the VPM repository.

The debug workflow does not create a formal stable release.

Debug and stable VPM publication share the same workflow concurrency group so they do not race while mutating the `vpm` branch.

## 10.5 What workflows do not validate

Current package/release workflows package files and update VPM/GitHub release state.

They do **not** launch Unity Editor.

Therefore workflow success does not establish:
- Unity C# compilation;
- Unity Test Runner/NUnit success;
- scene/build behavior;
- graphical correctness.

---

# 11. Diagnostics and production logs

## 11.1 Archived AutoAtlas diagnostic templates

`Development~/DiagnosticTemplates/AutoAtlas/` is an archive for reapplying temporary investigation diagnostics.

It is not the normative specification.

Its README explicitly classifies which behavior was permanent and which diagnostic code was removed after investigation.

Permanent conclusions currently preserved in production include:

- normal/default renderer targeting parity;
- production 1024-state 2D search;
- PixelCount / FullResidentMemory / Sampling merge gates;
- physical pixel-padding preservation;
- post-FineTuning memory revalidation;
- standalone post-FineTuning acceptance;
- bounded exact selection;
- optional visibility-animation merge exclusion;
- total AutoAtlas processing-time log;
- shared empty-submesh and ownership fixes.

Temporary long-form candidate/search diagnostics are not production requirements.

## 11.2 NDMF result logs

Normal AtlasTexture and AutoAtlas result logs intentionally report concrete before/after resolution routes rather than a percentage-only "optimization score".

AutoAtlas reports original material names through NDMF object identity where possible so build-stage material suffixes do not obscure user-facing diagnostics.

The total AutoAtlas processing-time line is production-required.

Long diagnostic reports, when temporarily restored, must keep displayed text and copied text identical and include enough project context for support; see the archived diagnostic template for the exact-copy presentation rules.

---

# 12. Validation model and current test assets

## 12.1 Validation levels

Never collapse these terms:

### Package build
Verifies package assembly/publication mechanics only.

### Static/source review
Verifies code/diff properties without Unity execution.

### Unity C# compilation
Verifies scripts compile in the target Unity/project dependency environment.

### Unity EditMode / NUnit / Test Runner
Executes test methods.

### Real-project smoke test
Executes the integrated feature on representative avatar projects and can reveal dependency/build/runtime behavior absent from unit tests.

### Visual regression check
Confirms rendered appearance, which compilation/tests may not establish.

State exactly which level was actually performed.

## 12.2 Automated Unity CI status

WDT contains substantial NUnit/EditMode test code, but the current GitHub package workflows do not run Unity Test Runner.

PR #26 records future CI work and security constraints:

- do not place the primary Unity account credentials in repository secrets;
- consider a dedicated CI Unity account or appropriately isolated authenticated self-hosted runner;
- start with manually authorized credentialed execution;
- do not expose credentials to fork PR code / `pull_request_target`;
- publish `TestResults.xml`.

This CI work is a follow-up, not evidence that current tests have run automatically.

## 12.3 Important regression-test coverage present in source

`AutoAtlasTextureMergePlannerSafetyTest` covers, among other cases:

- lilToon UV selector corrections;
- NFDH impossible-island preflight;
- unknown memory-layout fail-closed behavior;
- source mip-count memory accounting;
- retained-source detection;
- temporary mesh/material cleanup;
- ReferenceCopy merge-only semantics and non-revalidation of normal Atlas semantics;
- effective/empty submesh behavior;
- strict pixel and resident-memory benefit;
- sampling-cost regression;
- acceptance of larger max dimension when other costs improve;
- normal/default renderer-target parity;
- active untransformed material consumers;
- atomic-group mapping;
- per-property density behavior;
- merge-only renderer/material exclusions;
- 2D region padding/bounds/translation/rotation;
- exact vs bounded-greedy candidate selection;
- merged-execution fallback to atomic groups;
- visibility animation and remapped path aliases.

`AutoAtlasTextureAtlasSettingDefaultsAssumption` covers:

- texture-merging default and preservation of serialized disabled state;
- accepted normal Atlas defaults inherited by AutoAtlas;
- property-resolution density behavior;
- 4px minimum;
- unknown-primary fallback;
- legacy uniform sizing when automatic sizing is disabled.

Additional tests cover:
- resolution-route aggregation/sorting/missing source slots;
- borrowed runtime texture ownership.

Test existence is a regression contract, not proof of execution.

---

# 13. Known review boundaries / follow-up candidates

These are real concerns but must not be mixed into unrelated AutoAtlas audits.

## 13.1 Part Atlas generic reference scanning

Current fail-closed behavior on uninspectable SerializedObjects / scan-limit overflow may be overly conservative.

Review only as a scoped Part Atlas task.

## 13.2 Modular Avatar private implementation dependency

The reflection adapter depends partly on MA non-public fields/methods.

Review as a scoped compatibility task.

## 13.3 Unity automated test CI

Existing tests should eventually be executed in a credential-safe Unity CI environment.

This is infrastructure work, not a reason to claim current code is untested or to block unrelated already-manually-validated behavior retroactively.

---

# 14. Implementation / file map

This map exists so a maintainer auditing one feature knows where cross-cutting behavior lives.

## WDT publication

- `.github/workflows/BuildWorkPackage.yml`
- `.github/workflows/PublishVPM.yml`

## Shared domain/resource/log infrastructure

- `Editor/Domain/NotWorkDomain.cs`
- `Editor/Domain/TextureManager.cs`
- `Editor/ErrorReporting/TTTLog.cs`
- `Runtime/IDomain.cs`
- `Runtime/TextureCompressionData.cs`
- `Runtime/Utils/MaterialUtility.cs`

## Shared AtlasTexture core

- `Runtime/TextureAtlas/AtlasSetting.cs`
- `Runtime/TextureAtlas/AtlasSetting.AutoSize.cs`
- `Runtime/TextureAtlas/AtlasTexture.cs`
- `Runtime/TextureAtlas/AtlasTexture.BakeSettings.cs`
- `Runtime/TextureAtlas/AtlasTexture.ExecutionOptions.cs`
- `Runtime/TextureAtlas/AtlasTexture.Memory.cs`
- `Runtime/TextureAtlas/AtlasTextureResolutionPlan.cs`
- `Runtime/TextureAtlas/AtlasResolutionRouteReport.cs`
- `Runtime/TextureAtlas/AtlasContext.cs`
- `Runtime/TextureAtlas/AtlasSubMeshIndexIDSetContext.cs`
- `Runtime/TextureAtlas/IslandRelocationManager.cs`
- `Runtime/TextureAtlas/MaterialGroupingContext.cs`
- `Runtime/TextureAtlas/TextureFineTuning/TexFineTuning.cs`
- `Runtime/TextureAtlas/TextureFineTuning/MergeTexture.cs`
- `Runtime/TextureAtlas/TextureFineTuning/ReferenceCopy.cs`

## Shader information

- `Runtime/TextureAtlas/ShaderTextureUsageInformation/ITTShaderTextureUsageInformation.cs`
- `Runtime/TextureAtlas/ShaderTextureUsageInformation/MaterialInformationTranslator.cs`
- `Runtime/TextureAtlas/ShaderTextureUsageInformation/TTShaderTextureUsageInformationRegistry.cs`
- `Runtime/TextureAtlas/AAOCode/ShaderInformationFromAAO.cs`
- `Runtime/TextureAtlas/AAOCode/ShaderInformation.Liltoon.cs`
- `Runtime/TextureAtlas/AAOCode/ShaderInformation.VRCSDK.cs`
- `Runtime/TextureAtlas/AAOCode/ShaderInformation.VRCSDKToonStandard.cs`

## Atlas Inspector / Persistent Bake

- `Editor/Inspector/TextureAtlas/AtlasTextureEditor.cs`
- `Editor/Inspector/TextureAtlas/AtlasSettingsEditorGUI.cs`
- `Editor/Inspector/TextureAtlas/PersistentAtlasBakeSettings.cs`
- `Editor/Inspector/TextureAtlas/AtlasTextureBakeTargetResolver.cs`
- `Editor/Inspector/TextureAtlas/AtlasTextureBakeManifest.cs`
- `Editor/Inspector/TextureAtlas/AtlasTextureBaker.cs`

## Part Atlas

- `Editor/Inspector/TextureAtlas/PartAtlasPrefabWindow.cs`
- `Editor/Inspector/TextureAtlas/PartAtlasPrefabPipeline.cs`
- `Editor/Inspector/TextureAtlas/MatsukawaAdapter.cs`
- `Editor/Inspector/TextureAtlas/ModularAvatarAdapter.cs`
- `Editor/Inspector/TextureAtlas/PartAtlasPrefabHierarchyView.cs`
- `Editor/Inspector/TextureAtlas/PartAtlasPrefabPreview.cs`
- `Editor/Inspector/TextureAtlas/PartAtlasPrefabReadOnlyInspector.cs`
- `Editor/Inspector/TextureAtlas/HierarchyIdentityUtility.cs`
- `Editor/Inspector/CommonDrawer/TargetObjectSelector.cs`
- `Editor/Inspector/TextureAtlas/IslandSizePriorityTunerEditor/SetFromMaterialDrawer.cs`

## AutoAtlas / NDMF integration

- `Runtime/TextureAtlas/AutoAtlasTexture.cs`
- `Editor/Inspector/TextureAtlas/AutoAtlasTextureEditor.cs`
- `Editor/NDMF/AutoAtlasTexturePass.cs`
- `Editor/NDMF/AutoAtlasTextureMergePlanner.cs`
- `Editor/NDMF/AutoAtlasMergeIslandRelocator.cs`
- `Editor/NDMF/NDMFPlugin.cs`
- `Editor/NDMF/NegotiateAAOPass.cs`
- `Editor/OtherMenuItem/NewGameObjectAndAddTTTComponent.cs`

## Preview/support changes touched by WDT

- `Editor/NDMF/PreviewUtility/PreviewIslandSelector.cs`
- `Editor/NDMF/TexTransDomainFilter.cs`
- `Editor/Localize/ja_JP.po`
- `Editor/Localize/en_US.po`

## Test accessibility / tests

- `Runtime/AssemblyInfo.cs`
- `Runtime/InternalsVisible.cs`
- `Editor/NDMF/InternalsVisible.cs`
- `TestCode/NDMF/net.rs64.tex-trans-tool.ndmf-test-code.asmdef`
- `TestCode/NDMF/AutoAtlasTextureMergePlannerSafetyTest.cs`
- `TestCode/AtlasTexture/AutoAtlasTextureAtlasSettingDefaultsAssumption.cs`
- `TestCode/AtlasTexture/AtlasResolutionRouteReportTest.cs`
- `TestCode/AtlasTexture/UnityDiskUtilOwnershipTest.cs`

## Archived diagnostics

- `Development~/DiagnosticTemplates/README.md`
- `Development~/DiagnosticTemplates/AutoAtlas/README.md`
- `Development~/DiagnosticTemplates/AutoAtlas/state-budget-comparison.md`
- `Development~/DiagnosticTemplates/AutoAtlas/selection-exactness.md`
- `Development~/DiagnosticTemplates/AutoAtlas/standalone-profile-failure.md`
- `Development~/DiagnosticTemplates/AutoAtlas/execution-effect-summary.md`
- `Development~/DiagnosticTemplates/AutoAtlas/ndmf-console-exact-copy.md`

Unity `.meta` files accompany the new scripts/assets and are not separate semantic features.

---

# 15. Accepted WDT history map

This is not a debugging diary. It maps every substantive commit in the current WDT lineage to the reason it exists, so a maintainer can inspect the accepted change instead of guessing from an old work branch.

## Publication / versioning lineage

- `e8e52fdc` — initial WDT branch VPM publishing. Later policy was deliberately replaced.
- `37f6a9ad` — separate WDT package identity `net.gridatch.tex-trans-tool`.
- `01274304` — move WDT builds to stable-visible SemVer rather than prerelease-only visibility. Later syntax evolved.
- `42e93972` — stop treating every work commit as a formal release; make stable releases explicit.
- `168e3021` — temporary work-branch artifact path; later replaced.
- `61db0744` — rolling VPM validation channel replaced temporary artifacts.
- `76d217da` — rolling debug serial/metadata model.
- `6777c3bb` — support versioned release branches that must match `wdt` HEAD.
- `da2ea06e` / PR #22 — establish current `X.Y.Z+wdt.ttt.A.B.C` stable/debug metadata contract.
- `5429fbe9` — make stable release publication retry-safe without allowing a tag to silently move to another commit.
- `f1fec67f` — serialize debug/stable VPM mutation with one concurrency group.

## Shared Atlas / shader lineage

- `ebe9132a` / PR #4 — first unavailable shader texture-property guard.
- `da12c3b9` / PR #10 — finalize guard by intersecting with actually exposed texture properties.
- PR #5 — **unmerged** general texture-ST baking experiment.
- `81afe13d` / PR #11 — accepted narrow independently tiled `_Bump2ndMap` preservation; supersedes PR #5.
- `afd54418` / PR #12 — VRChat Toon Standard texture-usage metadata.
- `80b5d7f3` / PR #14 — automatic minimum lossless 256..4096 atlas-size search.
- `993b7293` — isolate WDT extension fields/files and introduce shared hierarchy identity; source organization for Toon Standard / auto-size / bake fields.
- `f1fec67f` — sync later AAO lilToon AudioLink metadata fixes.
- `68166b5c` — property-aware lossless output sizes; remove the old default lossy 512px Resize.
- `d0b11ab9` — final resolution-route provenance/reporting through FineTuning.
- `2085a588` — block/mip-aware texture-memory estimation interfaces and implementation.
- `8ffa1d7d` — reusable Atlas execution/planning seams used by AutoAtlas.
- `69e9dcdb` — shared material/mesh hardening, including effective/empty submesh handling.
- `c6faee4b` — preserve borrowed runtime texture ownership.
- `d530ac7b` — user-facing normal Atlas texture-memory before/after reporting.
- Equivalent-output sharing validation (PR #30): `Seiran_Test_Atlas` removed one redundant 2048² `_OutlineTex` output (`4.1 MiB -> 6.8 MiB` became `4.1 MiB -> 4.1 MiB`), and `Seiran_SailorCupid` / `Mat_Skirt_White_Blue_custom` changed to `2.0 MiB -> 1.0 MiB`, matching its 50% resolution-area reduction.
- `19d8632d` / PR #26 — concrete lilToon selector corrections.
- `0df70359` / PR #26 — safe preflight for physically impossible NFDH candidates.

## Persistent Bake lineage

- `c559092c` / PR #15 — named manifest-managed Persistent Bake; same-role in-place asset update and normal Atlas pipeline reuse.
  - This changeset also contains NDMF `RenderGroup.WithData` equality-comparer compatibility adjustments in preview support files; those are not bake product semantics.
- `5d8c7abe` — remove duplicate bake-side target/mesh resolution logic and reuse normal AtlasTexture targeting/mesh resolution.
- `8a3c5071` — harden destructive bake boundary: manifest/path validation, texture animation refs, output transaction/rollback, renderer-state restoration, more stable generated-asset identities.
- `e91f575c` / PR #17 — make the persistent bake core component-independent for Part Atlas reuse; normal bake keeps Undo enabled while standalone Part uses Undo-disabled temporary processing.

## Part Atlas lineage

- PR #16 — **unmerged probe** proving the Matsukawa reflection/integration boundary; not product implementation.
- `e91f575c` / PR #17 — accepted Matsukawa-backed standalone Part Atlas workflow and the large set of later follow-ups recorded in that PR.
  - The final PR state replaced its early same-output backup/overwrite design with versioned standalone generations.
  - It also established shared Atlas settings drawing, multi-target staging, optional Atlas, normal-map persistence, canonical texture names, extraction scope rules, and the narrowed Matsukawa adapter contract.
- `c6344161` / PR #20 — isolated 3D extraction preview with owned skinned snapshots and stable orthographic 360° orbit.
- `6ba09495` / PR #21 — native Unity/custom Inspectors in a disposable Prefab copy; explicitly not a universal sandbox against external asset side effects.
- `8bfa1dc4` / PR #23 — preserve original hierarchy depth/context during search and reveal selection on clear.
- `22090788` — harden Part hierarchy/MA reflection contracts and Inspector integration.
- `417d6611` — fix protected-object override UI/behavior rather than treating the analyzed protected list as the only state.
- `7a8a2254` — model HCE's actual stripped Renderers/dropped Components (including paired MeshFilter and PipelineSaver cases) so reference validation does not reject references to Components extraction itself removes.
- `0f82bcee` / PR #26 — resolve nullable handling in shared Atlas editor / Part pipeline without suppressing actual optional state.

## AutoAtlas lineage

- PR #24 — **unmerged analysis-only experiment**. Its broad conservative conditions and 8192/shader-proof exploration are not current specification.
- `29a83b28` — introduce the current AutoAtlasTexture lineage: automatic texture-connected material selection, lossless normal-Atlas execution, explicit exclusions, manual-Atlas exclusion, AAO negotiation, build-time NDMF reporting.
- `68166b5c` — eliminate AutoAtlas's old special-case default-Resize removal because lossy Resize was removed from normal defaults and replaced with property-aware sizing.
- PR #25 reconstructed a very large development branch into responsibility commits without changing the validated tree:
  - `2085a588` — memory estimation;
  - `8ffa1d7d` — reusable execution hooks;
  - `69e9dcdb` — shared material/mesh safety;
  - `c6faee4b` — resource ownership;
  - `a3fecae1` — production unused-space texture merging;
  - `d530ac7b` — normal Atlas memory report;
  - `b147ecc6` — regression tests / internals accessibility;
  - `dd25d5c4` — archived diagnostic templates.
- `be320895` / PR #26 — model `NotWorkDomain`'s TexTrans engine as genuinely optional and throw only when an engine-required API is misused.
- `bca0092a` / PR #26 — corrective re-audit: remove broad AutoAtlas-only safety gates and realign standalone AutoAtlas with normal Atlas semantics; retain merge-only semantic checks and fallback.
- `19d8632d` / PR #26 — shared lilToon selector correctness, not an Auto-only permission layer.
- `0df70359` / PR #26 — shared NFDH impossible-candidate handling, not a UV/material exclusion.

## Non-adopted CI proposals

- PR #18 — unmerged Roslyn syntax-only CI experiment; it could not establish Unity compilation.
- Existing Unity NUnit/EditMode tests remain valuable regression contracts, but a licensed/authenticated Unity CI executor has not yet been adopted.

Old backup branches preserve forensic evidence only. Never blind-cherry-pick them into current WDT.

---

# 16. Maintenance rules for future audits

Before changing WDT behavior:

1. read this file in full;
2. identify whether the code is shared Atlas, standalone AutoAtlas, texture-merging-only, Persistent Bake, or Part Atlas;
3. inspect current code and relevant tests;
4. use the historical decision index when a special case looks removable or unnecessarily strict;
5. distinguish a concrete failure mode from speculative "more safety";
6. do not add AutoAtlas-only restrictions for behavior normal AtlasTexture already accepts unless AutoAtlas itself introduces the failure;
7. do not remove production logs because they look diagnostic without checking this document and archived diagnostic classification;
8. do not describe package workflow success as Unity compilation/test success;
9. keep user-facing Japanese terminology `テクスチャ統合`; do not introduce `追加統合` as a UI/product term;
10. when a new design decision changes one of these invariants, update this file in the **same feature change**, not later as a handoff cleanup.
11. maintenance of this document is an implementation responsibility: update, correct, reorganize, and prune it as needed without requesting separate user approval for documentation-only maintenance, provided the changes faithfully reflect accepted implementation/design state and do not change product behavior.

