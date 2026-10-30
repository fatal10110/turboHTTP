---
name: turbohttp-pr-review
description: >
  Produce a first-pass review of an open pull request in fatal10110/turboHTTP — whether it meets
  the acceptance criteria of the issues it closes, RFC correctness, security, IL2CPP/AOT and
  mobile safety, thread safety and the ARM64 memory model, pooling/ownership, module boundaries,
  and tests — apply both specialist rubrics, submit every evidence-backed defect as an inline
  GitHub review comment, and end with an explicit bottom line saying whether anything must change
  before merge. Use when the user says "review PR N" for turboHTTP, or when a turbohttp-waves
  review prompt names this skill. Do not use for answering comments on your own PR
  (turbohttp-pr-review-comments).
---

# turboHTTP PR review

The PR body's claims are claims, not evidence. The linked issues' acceptance criteria are the
contract; the code is the evidence; the RFC is the oracle. Every review ends with a **bottom line**
that says unambiguously whether any change is required — including when the only items are nits.

## Priorities

Protocol correctness, security, and IL2CPP/mobile safety lead the review, then: acceptance criteria
met → should-this-exist design → correctness (concurrency, ownership, cancellation) → tests →
reuse/style. A turbohttp-waves prompt overrides this file on test scope, worktrees and GitHub
tooling.

## 1. Gather

```bash
gh pr view <N> -R fatal10110/turboHTTP \
  --json number,title,body,author,baseRefName,headRefName,headRefOid,files,additions,deletions,closingIssuesReferences,reviews,comments
gh pr diff <N> -R fatal10110/turboHTTP > <scratchpad>/pr<N>.diff
gh api repos/fatal10110/turboHTTP/pulls/<N>/comments --paginate      # existing inline threads
```

- Read the **whole** diff (page it). Never write a finding from a truncated view.
- Existing reviews/threads: do not repost what is raised or resolved. On a re-review, first spend a
  pass on what earlier reviews never questioned, then diff since the last reviewed head.
- Code-scanning threads (CodeQL alerts posted by `github-advanced-security`) are not "raised" by
  anyone who will act on them: triage every unresolved one. A real defect becomes a finding of
  yours; noise gets a one-line reason and is resolved
  (`python3 'Tools~/agent-tools/wave_mail.py' threads --pr <N>` lists them, `resolve-thread --id
  <id> --reply '<text>'` answers one). Wait for the head's `analyze (csharp)` check first.
- Linked issues: every `Closes/Fixes/Resolves #X`, cross-checked with `closingIssuesReferences`.
  For each, `'Tools~/agent-tools/issue_slice.py' <X>` and copy its **Acceptance criteria** into
  your checklist before reading the diff. Read its entry in
  `Development/docs/review-2026-10-full-library-review.md` and the phase doc it names.
- Check out the head without touching the author's branch:

  ```bash
  git -C <repo> fetch origin pull/<N>/head:pr-<N>-review
  git -C <repo> worktree add ../turboHTTP-worktrees/pr-<N>-review pr-<N>-review
  ```

## 2. Verify the PR's claims

- Every "X is now bounded / validated / never Y" claim: grep the whole affected assembly, not only
  the diffed files (e.g. a header-validation fix must cover headers, trailers and pseudo-headers on
  both HTTP/1.1 and HTTP/2 paths if the issue says so).
- Map each acceptance checkbox to the hunk or test that satisfies it. No hunk → already satisfied on
  `main` (prove it there) or **unmet**.
- A PR that describes its scope more narrowly than the issue requires has a gap, and the gap is a
  finding (unless the PR says `Part of #X` and the remainder is tracked).

## 3. Should this exist?

New public API, a new type/interface with one implementation, a new asmdef reference, a new
dependency, reflection, or an option that silently does nothing without another module: each needs
a reason in the issue or the PR. A new cross-module asmdef reference outside the documented
exceptions, or Unity engine types in Transport/JSON/WebSocket cores, is a `[blocker]`.

## 4. Review the diff

Work each pass against the code; write out the concrete byte sequence, interleaving or call order
before concluding either way. "Looks intentional" is not a clearance.

- **Protocol** — open the RFC section yourself. Connection vs stream error, which frames/headers
  are allowed in which state, size limits, required ordering (HPACK table order = wire order;
  stream ids increase on the wire; END_STREAM/RST on every early exit; HTTP/1.1 framing precedence
  and connection reuse only when the message was fully sent and fully read).
- **Security** — certificate/hostname validation never weakened; no fallback that bypasses
  validation; credentials never cross hosts, hops or logs (redirect, proxy, Auth/Cookie interceptor
  ordering); every peer-controlled length bounded before allocation.
- **Concurrency** — list every thread/continuation that touches changed state (read loop, writers
  under `_writeLock`, cancellation callbacks, dispose, timers, the Unity main thread). Name what
  serializes each access. Check publication on ARM64 (`Volatile`/`Interlocked`/locks, not plain
  fields), check-then-act races, lost wake-ups, and `await` while holding a lock.
- **Ownership** — every rented buffer/pooled object returned exactly once on success, exception,
  cancellation and dispose; nothing used after return; no slice of a reusable buffer escaping the
  lock that protects it.
- **Cancellation** — cancelling mid-operation leaves the connection either consistent or failed
  (never half-written); tokens observed where the operation is safe to abandon.
- **IL2CPP/AOT and platform** — no new reflection/`MakeGenericMethod`/`Expression.Compile`/emit;
  link.xml/[Preserve] for anything reached only by reflection; no API missing on .NET Standard 2.1 /
  Unity 2021.3; mobile networking (ATS, cleartext, IPv6, suspension).
- **Every caller** — for each changed method, list all call sites and re-derive its invariants at
  each.
- **Tests** — every new branch has a deterministic regression test that fails on the old code;
  hostile-path fixes are tested in the hostile direction; no sleeps as synchronization, no external
  network outside `[Category("ExternalNetwork")]`. New files have `.meta` files.
- **Journal** — the PR adds its implementation-journal file (CLAUDE.md requirement).
- **Over-engineering** — when the diff adds a type, interface, option or more than ~200 non-test
  lines, look for the smaller change; post survivors as `[should-fix]`.

Run the checks yourself in the review worktree — they are evidence, not the author's word:

```bash
cd <review-root> && './Tools~/ci/check.sh'
cd <review-root> && './Tools~/unity/run-tests.sh' --platform EditMode|PlayMode --filter '<the PR tests>'
```

(Unity runs need `UNITY_PATH`; exit 3 means CI only — record it.) Record exactly what ran.

## 5. Specialist rubrics

Apply `.claude/agents/unity-infrastructure-architect.md` and
`.claude/agents/unity-network-architect.md` to the diff — as subagents when you can spawn them,
otherwise read both files and work their checklists yourself. Their findings go through §6 like any
other.

## 6. Gate every finding before posting

Post a finding only if you can state: **where** (path + new-side line on the current head),
**failure scenario** (concrete input/interleaving → wrong bytes, state, leak or exposure),
**evidence** (RFC section, lock, call sites checked, test output), and **fix** (one or two lines).
Drop anything speculative, unrelated to the diff, or already raised. A confirmed protocol break,
race, leak or security hole is never demoted to style.

| Tag | Meaning | Blocks merge? |
|-----|---------|---------------|
| `[blocker]` | protocol violation, security hole, data loss/corruption, race, unmet acceptance criterion, forbidden module dependency, artifact that should not exist | yes |
| `[must-fix]` | real bug, missing regression test for a new branch, failing local gate, missing .meta/journal | yes |
| `[should-fix]` | correctness-adjacent or maintainability issue worth fixing in this PR | no, but action expected |
| `[nit]` | small style/naming/cleanup | no, but action expected unless marked optional |
| `[note]` | pre-existing gap or follow-up candidate; say where it is tracked | no |

`[nit, optional]` marks something you genuinely do not need done.

## 7. Submit the review

Do not modify, commit, push, merge or close anything on the PR.

1. Confirm `headRefOid` has not moved; if it has, re-fetch and recheck anchors.
2. Submit one review (event `COMMENT`) with every gated finding inline (tag first, scenario,
   evidence, fix). Nothing actionable → summary-only review.
3. Review body:

   ```markdown
   ## Bottom line
   **Verdict:** <Blocked | Changes required | Approve after small fixes | Approve as-is>
   **Action required before merge:** <YES | NO>
   <If YES: numbered list — `[tag] path:line — what to do`, every should-fix and non-optional nit.>
   <Optional items: list or "none".>  <Follow-ups to track: list with target issue, or "none".>

   ## Protocol, security, concurrency
   RFC sections checked; threads touching changed state and what serializes them; hostile inputs
   considered and the bound that stops each.
   Checks run: <check.sh result; Unity filters run or "CI only">.

   ## Acceptance criteria (#<issue>)
   - [x]/[ ] <criterion> — <evidence hunk/test, or "unmet: …", or "already on main: …">

   ## Specialist rubrics
   Infrastructure: <clean | items>; Network: <clean | items>

   ## Other findings
   <one line each, path:line, severity>
   ```

   Verdict rules are mechanical: any `[blocker]` or unmet criterion → **Blocked**; any
   `[must-fix]` → **Changes required**; only `[should-fix]`/non-optional `[nit]` → **Approve after
   small fixes**; otherwise **Approve as-is** (action NO).
4. Remove the review worktree and local branch.

## 8. Final chat response

Repeat the bottom line — never just "review submitted":

```
PR #<N> — review submitted (<url>).
Verdict: <verdict>
Action required: <YES — N items | NO>
1. [tag] path:line — what to do
Optional: <items or "none">   Follow-ups: <items or "none">
Checks: <check.sh pass; EditMode --filter X pass | Unity CI only>
```
