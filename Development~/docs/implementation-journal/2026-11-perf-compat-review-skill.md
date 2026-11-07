# 2026-11 — Performance and compatibility review skill

## What

Added the `turbohttp-perf-compat-review` skill and made it a mandatory pass of every PR review:
the standalone `turbohttp-pr-review` (section 5) and the long-lived turbohttp-waves review agent
(round 1 in full, delta rounds where the delta reaches).

## Files

- `.claude/skills/turbohttp-perf-compat-review/SKILL.md` — new: hot/warm/cold scoping; performance
  checklist (allocations, async cost, copies, locks, growth, pools, logging, timers) with
  allocation-gate evidence; compatibility checklist (Unity 2021.3 / .NET Standard 2.1 / C# 9,
  IL2CPP/AOT and stripping, ARM64/32-bit, iOS/Android, WebGL, public API and default-behavior
  breaks, wire interop); output section for the review body.
- `.claude/skills/turbohttp-pr-review/SKILL.md` — section 5 runs the pass; review body gains a
  "Performance and compatibility" section.
- `.claude/skills/turbohttp-waves/waves.js` — reviewer prompt loads the skill once and applies it
  every round; cost rules list it.
- `.claude/skills/turbohttp-waves/waves_sim.mjs` — asserts the reviewer prompt names the skill.
- `.claude/skills/turbohttp-waves/SKILL.md`, `.claude/agents/wave-review.md`, `CLAUDE.md` §8 —
  mention the skill.

## Decisions

- A real performance gain (or compatibility fix) that needs extra work — a refactor or a custom
  implementation in place of an allocating BCL/Unity API — is never dropped for effort: it is a
  finding when it fits the PR's scope, otherwise a follow-up issue filed or amended in the owning
  milestone before the review is submitted, listed as `[note]` with its number.
- The pass feeds `turbohttp-pr-review` section 6 (same tags and evidence bar) instead of producing
  its own verdict, so the bottom line stays mechanical.

## Verification

- `node .claude/skills/turbohttp-waves/waves_sim.mjs` — pass.
- `'./Tools~/ci/check.sh'` and `--full` — pass.
- Not run: Unity EditMode/PlayMode (no runtime code changed), IL2CPP, device. The skill has not yet
  been exercised by a live waves run.
