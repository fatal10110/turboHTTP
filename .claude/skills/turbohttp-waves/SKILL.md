---
name: turbohttp-waves
description: >
  Plan and run a multi-PR wave of fatal10110/turboHTTP issues on the Workflow tool: group open
  issues into lanes (one PR each) and waves, then fan out. Each PR gets a Sonnet lane brief, an
  implementation side that implements and fixes every review round, CI failure and merge conflict
  (handing over to a fresh agent at context checkpoints), and one long-lived review agent that
  reviews the first push in full (turbohttp-pr-review plus both specialist rubrics), re-reviews
  only each new delta, and requests the merge. A merger workflow runs the cheap-model CI/merge
  agent; a helper workflow runs the cheap helper agents (cs-scout, rfc-researcher, runner) that
  workflow agents cannot spawn themselves. Use when the user says "run the waves", "fan out
  wave 1", "ship these issues in waves", "implement -> review -> merge loop", or asks to execute a
  grouping/waves plan for turboHTTP. Merging is authorized by the user's invocation of this skill.
  Do not use for a single issue (use turbohttp-issue) or for reviewing one PR (use
  turbohttp-pr-review).
---

# turbohttp-waves

Runs a waves plan through `waves.js` (next to this file), launched as several Workflows over one
plan. Ported from the acis-waves orchestrator (fatal10110/acis_public); the protocol is the same,
the repository specifics are turboHTTP's.

- **track** (one Workflow per track): works the track's lanes wave by wave. Per lane (one PR):
  0. **Lane brief.** A Sonnet `wave-brief` agent gathers the issue text, the code anchors (current
     file:line of everything the issue cites, callers, owning asmdef), the RFC sections and phase
     docs, the tests to copy and the exact gate commands into `lanes/<slug>/brief.md`, which goes
     into the implementation agent's prompt, so the expensive agent starts coding instead of
     exploring (`brief: false` turns it off).
  1. **Implementation agent** (session model, or the lane's `implModel`). It implements (regression
     test first), runs the local gate, adds the lane's implementation-journal file and opens the PR;
     the implementation side then fixes every review round, CI failure and merge conflict. It hands
     over at checkpoints (right after the first push, and whenever its context has grown long): it
     returns `checkpoint` and a fresh agent continues from the worklog, because cost is
     turns × context.
  2. **Review agent.** It starts at the first push and stays alive until the PR is merged or given
     up. Round 1 is a full `turbohttp-pr-review` with both specialist rubrics
     (`.claude/agents/unity-infrastructure-architect.md`, `.claude/agents/unity-network-architect.md`)
     applied by the review agent itself, since workflow agents cannot spawn subagents. Every later
     round is a delta re-review. When the PR is clean, it asks the merger to merge and routes the
     result back.

  The two agents talk directly through the lane mailbox. Before the first push, a Haiku watcher
  holds the second slot. The script replaces either agent if it dies; a replacement rebuilds its
  context once, from the worklog or the review files. At the end of each wave, every track meets
  at a file barrier, then the next wave starts.
- **merger** (one Workflow): one Haiku CI/merge agent per merge request, one at a time across all
  tracks. It checks that the head is still the one the reviewer approved, updates the branch when
  behind, waits for the aggregate `ci` check, reruns known flakes, and squash-merges. It never edits
  code or runs tests. A real CI failure or a conflict goes back to the review agent, which sends it
  to the implementation agent. A Unity license/activation failure is answered `retry`, not
  `ci_failed`.
- **helper** (one Workflow): serves helper requests from every lane. Workflow agents cannot spawn
  subagents, so an agent runs `wave_mail.py ask` and this broker starts the helper:
  - `cs-scout` (`wave-scout`, Haiku): read-only C# locator;
  - `rfc-researcher` (`wave-researcher`, Sonnet; at most `researcherPerLane` per lane): one batched
    protocol/platform question answered from RFC text, the phase docs and the code;
  - `runner` (`wave-runner`, Haiku): runs the local gate or `--filter`ed Unity tests and returns a
    compressed result.
  The helper agent types are read-only, so a Haiku deliver agent writes each answer back.

The loop per lane:

1. The implementation agent implements and pushes.
2. The review agent reviews. If there are findings, they go to the implementation agent, which
   fixes, pushes, and gets a delta re-review from the same review agent. This repeats until a
   review is clean, at most `maxRounds` rounds.
3. The review agent asks the merger to merge. A real CI failure goes to the implementation agent,
   then gets a delta review. A conflict goes to the implementation agent and is re-reviewed only if
   the resolution was semantic. Then merge again.

All coordination goes through `Tools~/agent-tools/wave_mail.py` under
`<repo-parent>/turboHTTP-worktrees/.waves/<run>/` (mailboxes `box/impl`, `box/review`; lane state;
helper and merge request queues and answers; reviews, CI notes and barrier markers). It survives
session restarts. Lane worktrees are `turboHTTP-worktrees/wf-<slug>` on branch `wf/<slug>` from
`origin/main`; review worktrees are `turboHTTP-worktrees/review-<slug>` (detached).

## Slim agent types

Every workflow agent runs as a role-specific type from `.claude/agents/wave-*.md`
(`wave-coord`, `wave-deliver`, `wave-merge`, `wave-impl`, `wave-review`, `wave-runner`,
`wave-scout`, `wave-researcher`, `wave-brief`). Each lists only the tools its role uses, so no
agent loads the session's skill listing or unrelated MCP tools. Implementation and review agents
read their skill files with Read instead of the Skill tool. A role that needs a new tool needs it
added to its agent file. A session loads `.claude/agents` at start: a type added later is missing
until a restart, and the script then falls back to the default subagent for that role.

## Lane tools (token savers, built into every prompt)

A long-lived agent's every tool call re-sends its whole context, so cost tracks the number of
calls, not their size. The prompts point every agent at (all under `Tools~/agent-tools/`, always
called by absolute path, quoted because of the `~`):

- `look.py <spec>...` — ranges, C# declarations by name (`cs:PATH:Type.Member`, doc comments and
  attributes included, bodies by brace matching), bounded rg; many lookups in one call.
- `issue_slice.py <n>` — an issue's body, newest comments, referenced issues and the repository
  paths it names; cached under `.waves/.cache` so both lane agents fetch it once.
- `issue_search.py <terms>` / `pr_search.py <terms>` — search cached issues / closed PRs (cache
  built on first use or with `--sync`), for follow-up dedupe.
- `wave_mail.py worktree --lane S [--review] [--remove]` — creates, moves or removes lane worktrees
  with absolute paths.
- `wave_mail.py threads --pr N [--head SHA]` and `resolve-thread --id ID [--reply TEXT]
  [--keep-open] [--dismiss REASON]` — the PR's unresolved review threads, CodeQL alerts included
  (with `--head`, after that head's code scanning finished and settled: re-run while
  `code_scanning` is `pending` or `settling`), and reply, resolve and alert dismissal in one call. The reviewer triages every thread before
  each verdict; the merge agent answers `threads_open` instead of merging while any is open.
- `wave_mail.py pr-state --pr N` and `ci-wait --head SHA` — compact PR state (with `behind_by`)
  and an in-process CI wait for the merge agent.
- STOP reaches agents through commands they run anyway: every `wave_mail.py` output of a stopping
  run carries `"stop": true`, `look.py` prints `### STOP`, and the gate command in every prompt runs
  `wave_mail.py stopcheck <worktree>` first, which prints `### STOP` and exits 3 inside a lane or
  review worktree of a stopping run.

## Test policy (built into every prompt)

- Every agent runs the fast local gate `'./Tools~/ci/check.sh'` from the worktree root before every
  push (the reviewer before every verdict): repo invariants (.meta files, asmdef dependency rules,
  JSON/seed validity), `dotnet format` on changed C# files, and a shadow `dotnet build` of every
  runtime asmdef with analyzers. Missing Unity managed DLLs: `'./Tools~/ci/fetch-unity-dlls.sh'`
  once (docker). `--full` only when the lane note asks.
- Unity tests: no agent runs the full Unity suite. PR CI does not run them either (no Unity
  license, #188); the full suites run in a host editor at Step 5 (testing.md, "Periodic host-editor
  runs"). Locally only `--filter`ed runs of the affected tests, and only when Unity is installed:
  `'./Tools~/unity/run-tests.sh' --platform EditMode|PlayMode --filter '<regex>'`. Exit 3 /
  `UNITY_UNAVAILABLE`: agents say "Unity unavailable locally".
- Every behavior change carries a deterministic regression test that fails on the old code.
- The PR body carries a "Local gates" section and the line "Unity tests: host editor (periodic)". The reviewer
  reproduces the gate; a missing regression test or a failing gate is a [must-fix].
- The merge agent never runs tests. The `runner` helper refuses an unfiltered Unity run.

## Concurrency budget (read before choosing tracks)

The Workflow tool caps concurrent agents per workflow at `min(16, CPUs - 2)`. The dev box has 8
CPUs / 16 GB, so the cap is **6** and the script default is `slots: 6`. A lane needs 2 slots: the
implementation agent plus the review agent, or plus the watcher before the first push. Merges and
helpers run in their own Workflows with their own caps. So:

- One track workflow runs `lanesPerTrack = floor(slots / 2)` lanes at a time: **3 here**.
- Parallel PRs come from lanes within a track and from several track workflows launched at once.
- On this box: **2 tracks + merger + helper** (4 Workflows, up to 6 lanes in flight). The local
  gate's shadow build is CPU-heavy: with more than ~4 lanes building at once, pass `slots: 4`.
  On a smaller box pass `slots` = `min(16, CPUs - 2)`.

## Step 1: build the plan

Start from the live open issues in scope (milestones `v1.0-alpha: Release blockers`,
`v1.0-beta: Protocol & correctness hardening`, ...; the `[Tracking]` issue of each milestone lists
them). Load only the issues the user put in scope. Rules:

- **Lane** = one PR. Combine issues that touch the same function or file for the same reason, or
  would conflict with each other (e.g. the HPACK encoder/decoder issues; the Http2Connection write
  path issues). Keep a lane to one reviewable change.
- **Wave** = lanes that can start together. A dependency on another lane goes to an earlier wave,
  or to the same wave on the **same track** (it then waits for that lane to merge).
- **Track**: lanes that share a hot file (`hot` group) in the same wave go on one track; the
  validator refuses a hot group split across tracks. Typical hot groups: `http2-conn`
  (Runtime/Transport/Http2/Http2Connection*.cs), `raw-socket-transport`
  (Runtime/Transport/RawSocketTransport.cs), `http1-parser`, `tls`, `websocket`, `cache`,
  `uhttpclient` (Runtime/Core/UHttpClient.cs), `asmdef` (any asmdef change). Balance the tracks by
  expected size.
- Leave out issues that need an owner decision (license, package metadata, distribution format,
  roadmap re-baseline), need device validation the agents cannot do, belong to another session's
  uncommitted work, or are trackers. Put them in `exclusions` and list them for the user.
- A lane may be audit-only (close already-fixed issues, file follow-ups): say so in `note`; the
  implementation agent returns `no_change` with no PR.
- CLAUDE.md and the guides in Development/docs/agents/ are shared: lanes do not edit them unless the note says so (then give
  those lanes a `claude-md` hot group).

Plan file (`turboHTTP-worktrees/.waves/<run>/plan.json`). Shape, also in `plan.example.json`:

```json
{
  "tracks": ["A", "B"],
  "exclusions": "tracking issues #134-#139; owner-decision issues #3 #4 #5 #6 #10",
  "env": "optional extra environment rule lines for every agent",
  "waves": [
    {"name": "1", "lanes": [
      {"slug": "h2-hpack", "track": "A", "issues": [11, 12, 27],
       "milestone": "v1.0-beta: Protocol & correctness hardening",
       "note": "one PR: HPACK state integrity", "hot": ["http2-conn"], "deps": []}
    ]}
  ]
}
```

`slug` is the branch `wf/<slug>` and worktree `turboHTTP-worktrees/wf-<slug>`; it must be unique
across the run. Optional per lane: `implModel` / `reviewModel` (a model alias such as `sonnet`) and
`implEffort` / `reviewEffort` (low|medium|high|xhigh|max). Default: every implementation and review
agent on the session model; use a cheaper `implModel` only for mechanical lanes (docs, metadata,
test tagging), never for transport, TLS, HTTP/2, WebSocket or pooling lanes.

Validate the plan:

```bash
python3 '<repo>/Tools~/agent-tools/wave_mail.py' validate <plan.json>
```

Show the user the plan (waves, tracks, lanes, exclusions) before launching unless they already
approved a grouping in this conversation.

## Step 2: preflight

- `gh auth status` works (the merge agent and `pr-state`/`ci-wait` use `gh api`), and the GitHub
  MCP tools are connected (the lane agents use them).
- The local gate runs on a clean `origin/main` worktree: `cd <repo> && './Tools~/ci/check.sh'`
  (fetch the Unity DLLs once with `'./Tools~/ci/fetch-unity-dlls.sh'` if it asks). A red gate on
  main makes every lane fail; fix that first.
- Main's CI is green (`gh run list -R fatal10110/turboHTTP -b main -L 3`); the ruleset requires
  the `ci` check, and a red main turns every merge into a CI round.
- No leftover `wf-<slug>` worktree or `wf/<slug>` branch from another run uses a planned slug
  (`git -C <repo> worktree list`). If one does, rename the lane or keep it on purpose: a fresh lane
  reuses and inspects an existing worktree.
- No open PR already covers a lane (search open PRs for the issue numbers).
- `<tool>` is the absolute path of `Tools~/agent-tools/wave_mail.py` in the primary checkout.
- Pick `run`: a short id such as `w1101`. Reusing a run id resumes it (Step 4).
- Cost check: each PR uses one implementation agent and one review agent on the session model,
  plus a Sonnet brief and Haiku merge, watcher and deliver agents. Say so if the user has not seen it.

## Step 3: launch

Launch one Workflow per track, one merger and one helper broker, all in the same turn, with the
same `run`, `plan` and `tool`. Pass `scriptPath` as the absolute path of `waves.js`. If the tool
refuses a path outside the session directory, Read the file once and pass its text as `script`;
then reuse the persisted path that result returns.

```text
Workflow({scriptPath: "<skill dir>/waves.js", args: {run, epoch, role: "merger", plan, tool}})
Workflow({scriptPath: "<skill dir>/waves.js", args: {run, epoch, role: "helper", plan, tool}})
Workflow({scriptPath: "<skill dir>/waves.js", args: {run, epoch, role: "track", track: "A", plan, tool}})
Workflow({scriptPath: "<skill dir>/waves.js", args: {run, epoch, role: "track", track: "B", plan, tool}})
```

`plan` is the parsed plan object (a JSON value, not a string). `epoch` names this launch: `"1"`
for the first, and a new value for every relaunch of the whole run. Optional args:

| Arg | Default | Meaning |
| --- | --- | --- |
| `repo` | from `tool` | the primary checkout (the parent of `Tools~/agent-tools/wave_mail.py`) |
| `worktrees` | `<repo-parent>/turboHTTP-worktrees` | where lane and review worktrees live |
| `slots` | 6 | the per-workflow agent cap; sets `lanesPerTrack` = floor(slots/2) |
| `lanesPerTrack` | from `slots` | lanes running at once in one track |
| `maxRounds` | 8 | review rounds per PR before the lane ends `unresolved` |
| `reviewCiFixes` | true | a CI fix gets a delta review before the next merge attempt |
| `waitCalls` | 6 | recv calls (~9.5 min each) per first-push watcher agent |
| `takeCalls` | 6 | take polls (~9.5 min each) per broker poller agent before it returns |
| `maxIdleMin` | 240 | minutes without a mailbox message before a lane ends `stalled` |
| `restarts` | 2 | replacements per lane for the review agent, and separately for the implementation side, when an agent dies or exits early (checkpoints do not count) |
| `researcherPerLane` | 1 | rfc-researcher requests per lane (impl and review combined) |
| `agentTypes` | `{coord: 'wave-coord', deliver: 'wave-deliver', merge: 'wave-merge', impl: 'wave-impl', review: 'wave-review', runner: 'wave-runner', scout: 'wave-scout', researcher: 'wave-researcher', brief: 'wave-brief'}` | agent type per role; `null` falls back to the default workflow subagent |
| `models` | `{merge, coord, deliver, scout, runner: "haiku", brief, researcher: "sonnet"}` | model overrides per role (`impl`, `review` included, for every lane; a lane's `implModel`/`reviewModel` wins) |
| `retry` | [] | slugs to run again even though saved state says they finished; a retried lane gets a fresh `maxRounds` budget |
| `brief` | true | a `wave-brief` agent writes `lanes/<slug>/brief.md` before a fresh lane's implementation agent starts; it is put in that agent's prompt |
| `maxCheckpoints` | 6 | hand-overs per lane; checkpoints do not use `restarts`; once spent, the next agent's prompt says none are left |

## Step 4: monitor, intervene, resume

- Progress: `python3 '<tool>' status --run <run>` prints one line per lane:
  `slug|status|stage|pr|round`. Stages are `impl`, `review`, `fix`, `merge`, `ci-fix` and
  `conflict`. Each workflow's result lists every lane's status, PR, rounds, history and follow-ups.
- Each lane directory has the implementation agent's `worklog.md`, `brief.md`, `reviews/r<N>.md`,
  `ci/c<N>.md` and its mailboxes `box/impl` and `box/review`.
- Terminal lane statuses: `merged`, `no_change`, `blocked`; `unresolved` (findings left after
  `maxRounds`; the PR stays open); `stalled` (idle for `maxIdleMin`); `merge_blocked` (the
  permission system or the ruleset denied the merge: merge it by hand, then
  `'<tool>' state --run R --lane S --set status=merged` so later deps pass); `failed`, `skipped`
  (a dep did not merge), `stopped`.
- Drain (finish what is under way, start nothing new): `'<tool>' stop --run <run> --drain`.
- Stop everything: `'<tool>' stop --run <run>` writes `STOP`. Every waiting agent returns, the
  implementation and review agents update their worklog/notes and return `stopped`, the merger and
  helper end, and the tracks stop at their next wait. A merge agent already waiting on CI finishes
  its merge first.
- **Resume** (after a restart, a stop or a usage limit): delete `<run dir>/STOP` if present, then
  launch the same four Workflows again with the same args and a **new `epoch`**. Never use
  `resumeFromRunId`: the concurrent lanes break its cache. Each track skips finished lanes and
  starts replacement agents with a RESUME note for active ones. Put slugs in `retry` to run
  finished lanes again.
- A merger or helper workflow that died can be relaunched alone with the same `epoch`; its first
  poll re-serves the requests the dead instance took but never answered (older than 4 h, or from
  ended lanes: answered "dropped"/`retry`). Relaunch a broker only after it has died.
- A track that crashed never reaches the barrier, so the other tracks wait. Relaunch only that
  track with the **same** `epoch`, or stop the run.

## Step 5: wrap up

When all workflows have returned:

- Report per lane: status, PR, merge SHA, rounds, follow-ups filed. Report every lane that did not
  merge, with the reason and what is left (open PR, worktree kept).
- Check that each "Closes" issue is closed, that the milestone's `[Tracking]` issue checkboxes are
  ticked for merged lanes (tick them), and that no `wf-<slug>` worktree remains for a merged lane.
- If phase status changed, update `Development/docs/agents/project-status.md` in one follow-up
  commit/PR (lanes never edit them).
- Run the full Unity suites in the host editor on the new `origin/main` (testing.md, "Periodic
  host-editor runs"): PR CI skips Unity tests without a license, so this is the first time the
  wave's Unity tests actually run. File or fix every failure before reporting the run done.

## Editing waves.js

The script is plain JavaScript run by the Workflow tool. It has no filesystem access and no
`Date.now()`/`Math.random()`. After any change, run the offline simulation and the tool tests:

```bash
node .claude/skills/turbohttp-waves/waves_sim.mjs
python3 -m unittest discover -s 'Tools~/agent-tools' -p 'test_*.py'
```

The simulation runs the script with fake agents that talk through an in-memory mailbox, under a
2-slot cap, and checks: one implementation and one review agent per PR across review, CI and
conflict rounds; `maxRounds` → `unresolved` with dependents skipped; resume from saved state;
replacement of dying agents; retry with a fresh round budget; serial merges and answers for a dead
merge agent; the rfc-researcher limit and helper agent types/models; turboHTTP gate/skill/rubric
paths in the prompts; the example plan on both tracks; slim agent types and the fallback when a
type is missing; STOP mid-wave; lane brief, checkpoints and per-lane model/effort; the checkpoint
budget; STOP seen by a brief agent; and an implementation agent exiting right after its first push.
