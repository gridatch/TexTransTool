# Unity compilation CI (WDT)

This is an isolated compilation gate prototype, **not** a full integration test of
the Matsukawa HCE engine or persistent Atlas bake.

- Workflow: `.github/workflows/UnityCompile.yml` (pull requests into `wdt` or manual run).
- Unity: **2022.3.22f1** (VRChat's supported Editor).
- Test project: `.ci/unity-project/`.
- Test package: reconstructed from the **current source commit** using the
  WDT package-name rewrite and upstream image asset overlay.
- Dependencies: `vrc-get` from the explicit VPM repositories in the workflow.
- Acceptance: EditMode tests must run and pass. Unity must first compile all
  enabled assemblies, then the smoke test verifies core WDT asmdefs are present.

## Repository Actions secrets required

Set in **Settings → Secrets and variables → Actions**:

- `UNITY_EMAIL` and `UNITY_PASSWORD`
- **Personal:** `UNITY_LICENSE` (full contents of `Unity_lic.ulf`)
- **Pro:** `UNITY_SERIAL` instead of `UNITY_LICENSE`

See [GameCI activation](https://game.ci/docs/github/activation/).
If secrets are absent, the workflow **fails**, rather than reporting a skipped
or successful compile. Never commit credentials or license files. PRs from
external forks do not have these secrets; those runs are expected to fail until
an explicit trust-boundary policy is designed.

## CI scope and release gate rollout

- Unity compile + NUnit assembly smoke only; no Atlas, Prefab, HCE Reflection
  contract, or GPU processing verification.
- A representative, pinned VRChat dependency set, not all version combinations.
- This source package is assembled independently of the existing ZIP jobs.
  Reuse common assembly logic or compare shipping sources before release gating.
- **Do not modify Debug/Release publishing to depend on this check** until
  a genuine licensed Unity run succeeds and a negative-case compilation
  failure is demonstrated. This avoids breaking existing distribution.
- This workflow does not publish anything.

## Required acceptance tests before release gating

1. Resolve dependencies and confirm Unity launches under a configured license.
2. Observe the passing EditMode smoke test in CI.
3. Add an intentional C# syntax error to a disposable branch; CI must fail.
4. Fix it; confirm green CI on the same branch.
5. Only then add fail-closed gates before VPM/Release publication.
