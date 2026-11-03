# 2026-11 Roadmap re-baseline (#10, #108)

Docs-only lane of waves run w1102. No C#, tests, asmdefs or scripts changed.

## What

- Applied the owner re-baseline recorded on #10 (2026-11-02): feature freeze until v1.0 GA, the v1.0
  gates (22c negotiated today, must ship off by default per #103, 18a experimental until #104, 14.3 experimental and opt-in,
  BouncyCastle opt-in), the `1.0.0-pre.N` release train, host-only Unity tests.
- `Development/docs/00-overview.md` rewritten against the repository:
  - config block: MIT license, LiteJson default JSON (STJ opt-in, `JsonUtilitySerializer` after
    `TurboHTTP.Complete` startup, #271), BouncyCastle as opt-in fallback, pre.N distribution;
  - module list matches `Runtime/**/*.asmdef` (16 optional modules incl. `Complete`/`Bootstrap` and
    `Transport.BouncyCastle`); the non-existent `Performance/` module is gone from the directory tree;
  - early risk spike 2 (device ALPN) kept as history and mapped to #99 (v1.0 GA);
  - the six "Review Notes TODO" blocks replaced by a resolved/tracked table;
  - phase list covers every plan in `phases/` (3C, 3D, 18-19c, 22-22c, 23-25; 22d listed without link,
    plan docs pending #160; Phase 13 has no plan); dead Phase 16 link fixed;
  - `M0`-`M5` replaced by the six GitHub milestones with the phase -> issue mapping; GC target now
    "< 500 B per request on the buffered fast path", matching CLAUDE.md and `project-status.md`.
- Every `**Milestone:**` header in `phases/phase-*.md` and `phases/*/overview.md` moved to the GitHub
  milestone names (phase 23 gained one; 3D stays a deprecated stub); `M1`-`M5` prose in phases 4, 5, 8,
  9, 10, 11, 12 and 17 reworded. Middleware names `M1`..`M3` in phase 4 tests, review documents and
  other journal entries are history and untouched.
- `Development/docs/README.md` mirrors the overview (milestone names, MIT, LiteJson, full phase list,
  workflow without `M0`-`M5`, duplicate differentiator line removed).
- `CHANGELOG.md`: added a "Release status" note; the "Phase 14 Features" block became an "Initial
  feature set" list, each item re-checked against the code (Happy Eyeballs `HappyEyeballsConnector`,
  `ProxySettings.Credentials`, `UseAdaptiveNetworking`, `IHttpPlugin`/`PluginContext`, `Runtime/Auth`
  OAuth/OIDC/PKCE); background networking is labelled experimental and opt-in; the "zero-overhead plugin
  system" claim was dropped. The Modules paragraph already matched the asmdefs and is unchanged. The top
  entry stays `[1.0.0-pre.1]` (the json check ties it to `package.json`).

## Decisions

- Phases that are shipped with no open issue use the header `v1.0 (shipped)`; the overview defines
  "v1.0" as the three v1.0-* milestones. Phases with an open gate name the milestone of that gate
  (v1.0 GA for 3C, 8, 9, 14, 15, 17, 18a, 22a, 22c) and say so.
- CLAUDE.md, AGENTS.md and `Development/docs/agents/` are claude-md hot files and were not edited;
  `rg -w 'M[0-5]'` finds nothing there, so #10 AC1 holds for them (done by #107).
- Not changed (history): `implementation-journal/*`, `review-*.md`, `phases/phase22b/phase-22b.2-review.md`.

## Deferred / follow-ups

- `Development/docs/agents/project-status.md` could carry the freeze and gate wording (hot-group file):
  noted in the PR as an open question for a claude-md lane.
- Phase 22d plan docs are still untracked (#160); #125 is the plan fix.

## Verification

- Link check (`python3 -I`) over `00-overview.md` and `README.md` in `Development/docs/`: all relative
  `.md` targets and in-page anchors exist.
- `rg -n -w 'M[0-5]' Development/docs/phases Development/docs/00-overview.md Development/docs/README.md`
  only leaves phase-04 middleware names `M1`..`M3`, phase22b review text and phase22 review ids.
- `Tools~/ci/check.sh` at the pushed head (see the PR body).
- Not run: Unity EditMode/PlayMode (nothing to filter on, docs only), IL2CPP, devices.
