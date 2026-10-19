# NDMF Console Exact-Copy Long Diagnostic

id: `ndmf-console-exact-copy`  
status: `captured-active-at-snapshot`  
runtime_cost: `presentation-only`

## Purpose

Present a very long multiline diagnostic report in NDMF Console without text-geometry corruption, while guaranteeing that the visible report and the one-click clipboard text come from the same exact source string.

This template is reusable outside AutoAtlas if another NDMF diagnostic needs large preformatted output.

## Source anchors

`Editor/NDMF/AutoAtlasTexturePass.cs`
- `AutoAtlasTextureTemporaryDiagnosticError : SimpleError`
- private `_message`
- empty `TitleSubst`
- `CreateVisualElement(...)`
- horizontal + vertical `ScrollView`
- line container `auto-atlas-diagnostic-lines`
- one plain `Label` per logical line
- `enableRichText = false`
- empty-line minimum height
- copy button assigning `EditorGUIUtility.systemCopyBuffer = _message`.

`TestCode/NDMF/AutoAtlasTextureMergePlannerSafetyTest.cs`
- `DiagnosticConsoleRendersExactCopyText`.

## Patch sources

Historical evolution:
- `0a08c73c189f01be86fcd7598f9c3a1fc8fad8fb` — one-click copy.
- `8d3dc77471c4cb290a69c4f3c6f9bc94b11a429f` — restore proven copy behavior / project label.
- `89658c4c46b210dcc6d42a05aa73607b0a68f18d` — exact plain-text rendering attempt.
- `d41ae48e374a22aa5ae074fc65e96e1765895ae8` — scrollable report.
- `af4804f4ca286288a77ad91322d361e90100da0c` — line-by-line rendering; current validated geometry fix.
- `b975b798b499435e182e566daea5259780a679fb` — regression test for line-based rendering.

## Why the current shape exists

Rejected/failed presentation approaches:
- putting the entire report into `SimpleError.TitleSubst` caused broken NDMF title layout.
- one giant readonly multiline text field fixed some geometry but did not provide the desired scrolling behavior.
- one giant `Label` inside a `ScrollView` still produced corrupted text geometry for long reports.

Validated approach:
- do not use the title as the report body.
- split the report only for display layout.
- render one plain non-rich-text label per logical line.
- preserve empty logical lines with minimum line height.
- copy the original unsplit `_message` directly.

User runtime validation at the captured snapshot confirmed the NDMF Console display corruption was fixed.

## Output contract

- The report source is one exact string.
- Clipboard content equals that exact string.
- Displayed logical lines, joined with `\n`, equal that exact string.
- No rich-text interpretation.
- Both horizontal and vertical scrolling are available.
- Long lines are not wrapped.
- Empty lines remain visually represented.
- Full report is not duplicated into `TitleSubst`.

## must_not_change

While this presentation template is active:
- display and copy must not be built from separate formatted data sources.
- copy must not reconstruct text from UI elements.
- do not truncate the clipboard text.
- do not expose absolute project paths merely for diagnostic labeling.
- early/TargetNotFound report paths must still be able to expose/copy the diagnostic content when required by the active investigation.

## validation

Keep/restore `DiagnosticConsoleRendersExactCopyText` while the temporary diagnostic UI exists. It should verify:
- dedicated scroll view
- line container
- line labels
- rich text disabled
- joined labels equal exact message
- copy button exists and copies the original message
- title substitution does not contain the giant report.

Runtime validation is still required after UI changes because the original failure was UI Toolkit/NDMF rendering corruption, not merely string inequality.

## activation_recipe

1. Construct the complete report string once.
2. Pass that exact string to a dedicated `SimpleError` implementation.
3. Keep the title short/localized.
4. Create a vertical+horizontal scroll view.
5. Split the message with `StringSplitOptions.None` and render one non-wrapping plain label per line.
6. Copy `_message` directly from the button.
7. Add/restore the exact-copy regression test.

## removal_contract

If no long-form diagnostic remains:
- remove the temporary diagnostic `SimpleError` class
- remove diagnostic-only UI localization keys
- remove the exact-copy UI test if it no longer tests any shipped behavior.

If another diagnostic still needs long-form NDMF presentation:
- prefer moving/adapting this renderer as shared diagnostic infrastructure instead of reimplementing another giant-text UI.

Do not remove unrelated normal NDMF logging or the user-facing AutoAtlas result/timing output.
