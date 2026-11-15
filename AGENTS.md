# Repository Agent Notes — WDT

## Mandatory startup

Before investigating, designing, reviewing, implementing, or auditing WDT work on `wdt` or a WDT work branch:

1. Read this file and `Development~/WDT_DEVELOPMENT_POLICY.md` **in full**.
2. Verify the current Git branch/HEAD, applicable PRs, and the actual relevant source. Use `validation-state.json` on the `vpm` branch and the current GitHub Actions workflows for debug/release state; do not rely on an old handoff or a branch name alone.
3. Verify the relevant upstream/vendor code and installed-version API where compatibility is at issue. Treat source code as implementation truth; historical designs and unmerged PRs are evidence, not automatically accepted specifications.

If a work branch predates these instructions, read the current `wdt` copies. When sources and documented intent disagree, investigate and report the discrepancy before changing behavior.

## Required workflow and context maintenance

- Follow the policy's **Repository branches and VPM channels**, **Development, validation and integration workflow**, and **Maintainer-context upkeep** sections. In particular, `feat/**` and `fix/**` pushes publish a *rolling debug VPM build*, not a stable release; publishing success does not establish Unity compilation or runtime validation.
- **Proactively preserve accepted, durable context as part of the change itself**: rationale that is not obvious from code, responsibility boundaries, important invariants, rejected/superseded alternatives, and concrete validation limits. Update the existing policy in the same work branch when those facts change; do not leave them only in chat or a handoff.
- Record temporary experiments, work-in-progress state, unresolved questions, debug build identifiers and validation evidence in the relevant PR/handoff instead of turning the policy into a chronological diary. Verify live Git/VPM state at every new session.
- Before handing off or requesting a merge, check whether the policy needs an update, make any needed update, and state clearly what was implemented, verified, left unverified, and still needs a decision. If no durable context changed, do not add filler.
- `AGENTS.md` is the **single entry point**; `Development~/WDT_DEVELOPMENT_POLICY.md` is the **single persistent source for WDT development rules and accepted cross-feature constraints**. Do not create competing copies of these rules.

## Audit scope

Audits verify architecture, correctness, state transitions, and existing behavioral contracts; they are not permission for additional AutoAtlas-only conservative gates. Ordinary AtlasTexture acceptance is the baseline unless an automation-specific real failure justifies a deviation. Avoid unrelated refactoring, features, tests, UI, and speculative guards.
