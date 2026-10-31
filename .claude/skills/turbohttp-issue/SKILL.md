---
name: turbohttp-issue
description: >
  Implement one fatal10110/turboHTTP GitHub issue end to end: read the issue, the phase docs and
  RFC sections it names and the 2026-10 review, work in a linked worktree, write a failing
  regression test first, fix, run the local gates, apply both specialist rubrics, add the
  implementation-journal entry, and open a PR that closes the issue. Use when the user says
  "implement #N", "fix issue N", "pick up <issue>", or when a turbohttp-waves lane prompt names
  this skill. Do not use for reviewing a PR (turbohttp-pr-review), for answering review comments
  (turbohttp-pr-review-comments), or for multi-issue fan-out (turbohttp-waves).
---

# turboHTTP issue implementation

Repository rules live in `CLAUDE.md` and the scoped guides it lists (module rules, design constraints, known
risk areas, journal and review requirements); nothing here repeats them — read the sections your
change touches. A turbohttp-waves prompt overrides this file on test scope, worktree commands and
GitHub tooling.

## Authority

Protocol correctness (the RFCs), security, and IL2CPP/mobile safety outrank everything else:

- **Protocol**: the RFC is the oracle (RFC 9110 semantics, 9111 caching, 9112 HTTP/1.1, 9113 HTTP/2,
  7541 HPACK, 6455/7692 WebSocket, 6265 cookies, 6125/5280 TLS names and paths). Issue text and
  existing code shapes are leads, not authority. Where the RFC allows choices, follow the phase
  doc, else the conservative behavior, and record the choice under "Decisions" in the PR.
- **Security**: no change may weaken certificate validation, leak `Authorization`,
  `Proxy-Authorization`, cookies or tokens across hosts/hops/logs, or accept unbounded input
  (header bytes, frame/message sizes, decompression ratio, line lengths).
- **Platform**: IL2CPP/AOT first (no new reflection, `Expression.Compile`, `MakeGenericMethod`,
  `Reflection.Emit`; stripping-safe), ARM64 memory model (shared state via `Volatile`/`Interlocked`
  or locks), iOS/Android networking (ATS, cleartext policy, IPv6/NAT64, suspension).
- **Ownership**: pooled buffers, pooled requests/streams and response bodies have one owner and
  are returned exactly once on every path (success, exception, cancellation, dispose).

Every change answers three questions in the PR body, from evidence:
1. **Wire/behavior**: what the peer or the caller observes differently (or the RFC sections read to
   confirm nothing changes).
2. **Concurrency**: which threads/continuations touch the changed state and what serializes them
   (lock, `_writeLock`, Interlocked, single read loop).
3. **Abuse/limits**: what a hostile peer or a misbehaving caller can do on the changed path
   (oversize, flood, reorder, cancel mid-operation) and the bound that stops it.

## Work loop

1. **Scope.** Read the issue live (`'Tools~/agent-tools/issue_slice.py' <n>`, or `gh issue view <n>
   -R fatal10110/turboHTTP --comments`): Summary, Location, Problem, Spec reference, Suggested fix,
   Acceptance criteria. Copy the acceptance criteria into your worklog — they are the contract.
   Read the matching entry in `Development/docs/review-2026-10-full-library-review.md` and the phase
   doc the issue's Context names. Verify the defect still exists on `origin/main` at the cited
   file:line (line numbers drift; find the current ones). Already fixed → comment with evidence
   (main SHA, file:line, PR), close it, stop.
2. **Gaps first.** List every part of the acceptance criteria you will not deliver and why. Each
   one becomes a follow-up issue (dedupe with `'Tools~/agent-tools/issue_search.py' <terms>`; same
   template; milestone that owns it) **before** you edit. A gap found later pauses work until it is
   tracked.
3. **Research.** Read the RFC section(s) yourself (https://www.rfc-editor.org/rfc/rfcNNNN.txt) —
   never act on a citation you have not read. Read the newest implementation-journal entries that
   touch these files (`rg -l '<FileName>' Development/docs/implementation-journal | tail -5`) so
   you do not undo an earlier decision. Map every caller of each method you will change.
4. **Worktree.** Never write in the primary checkout:

   ```bash
   git -C <repo> fetch -q origin main
   git -C <repo> worktree add ../turboHTTP-worktrees/issue-<n>-<slug> -b wf/issue-<n>-<slug> origin/main
   ```

   (turbohttp-waves lanes use `wave_mail.py worktree` instead.) All paths below are relative to the
   worktree root.
5. **Test first.** Write the regression test that fails on the old code: NUnit under
   `Tests/Runtime/<Area>/` (or `Tests/Editor/`), in a new or small file. Use the existing fixtures
   (`MockTransport`, `RecordReplayTransport`, `TestHelpers`, `AssertAsync`, loopback servers used by
   neighbouring tests). Deterministic only: no wall-clock sleeps as synchronization, no external
   network. Concurrency, cancellation and protocol fixes drive the hostile interleaving or byte
   sequence the issue describes (cancel mid-frame, flood CONTINUATION, lower the window, close
   mid-headers) and assert the bound holds. New files and folders need a `.meta`:
   `python3 'Tools~/ci/gen_meta.py' <path>...` (CI fails on a missing or orphan `.meta`).
6. **Implement.** The smallest complete fix in the owning assembly. No new asmdef references
   outside the documented exceptions (`Development/docs/agents/architecture.md`), no Unity engine types in Transport/JSON/WebSocket
   cores, no unsafe code outside Transport, no new public API unless the issue asks for it (then
   XML-document it). Keep hot paths allocation-free where they already are (pooled buffers,
   `ValueTask`, no LINQ/closures in per-frame code). Never edit
   `Runtime/Transport/BouncyCastle/Lib/` (vendored).
7. **Gates.**

   ```bash
   cd <worktree> && './Tools~/ci/check.sh'            # repo invariants, dotnet format, shadow build + analyzers
   cd <worktree> && './Tools~/unity/run-tests.sh' --platform EditMode --filter '<YourTests>'
   cd <worktree> && './Tools~/unity/run-tests.sh' --platform PlayMode --filter '<YourTests>'
   ```

   The Unity runs need `UNITY_PATH`; exit 3 (`UNITY_UNAVAILABLE`) means they run on CI only — say
   so. Never run the whole Unity suite locally; PR CI does. `check.sh` asking for Unity DLLs:
   `'./Tools~/ci/fetch-unity-dlls.sh'` once.
8. **Specialist rubrics.** Apply `.claude/agents/unity-infrastructure-architect.md` and
   `.claude/agents/unity-network-architect.md` to your diff (spawn them as subagents when you can;
   otherwise read both files and work their checklists yourself). Fix findings, re-run the gates,
   and record any deliberate deferral with rationale and target milestone (and a follow-up issue).
9. **Journal.** Add one new file `Development/docs/implementation-journal/YYYY-MM-<issue-slug>.md`
   (what, files, decisions, deferred items, how verified) with its `.meta`. Update CLAUDE.md or
   the guides in `Development/docs/agents/` only when phase status, architecture or conventions
   change and you are not in a turbohttp-waves lane (lanes leave them alone).
10. **Deliver.**
    - Audit the final diff (`git diff origin/main...`): only in-scope files; no debug code; every
      acceptance checkbox maps to a hunk or a test.
    - Commit with a message that names the behavior fixed (no review provenance, no model names),
      ending with `Co-Authored-By: Claude <noreply@anthropic.com>`.
    - Push `wf/...` and open ONE PR against `main` (ready for review unless the user asked for a
      draft). Body:

      ```markdown
      ## Summary
      <behavior delivered, 2-4 lines>

      Closes #<n>            <!-- one line per fully resolved issue; "Part of #<n>" otherwise -->

      ## Acceptance criteria (#<n>)
      - [x] <criterion> — <file:line hunk or test name>

      ## Decisions
      <RFC/plan choices and why, or "none">

      ## Wire / concurrency / limits
      1. Wire/behavior: ...
      2. Concurrency: ...
      3. Abuse/limits: ...

      ## Local gates
      - `'./Tools~/ci/check.sh'` at <sha>: pass
      - Unity EditMode --filter '<regex>': <pass | Unity unavailable locally>
      - Unity PlayMode --filter '<regex>': <pass | Unity unavailable locally>
      Unity tests: host editor (periodic)

      ## Specialist rubrics
      Infrastructure: <clean | findings fixed: ...>; Network: <clean | findings fixed: ...>

      ## Follow-ups
      <issue numbers filed or amended, or "none">

      🤖 Generated with [Claude Code](https://claude.com/claude-code)
      ```
    - Read the PR back (base `main`, closing keywords present). Remove the worktree after the PR is
      open unless asked to keep it, and report the PR URL.

## Follow-up issues

Use the repository issue template (Summary, Location, Problem, Spec reference, Suggested fix,
Acceptance criteria, Context), labels from the existing set (`type:*`, `area:*`, `priority:P0-P3`,
`source:*`) and the milestone that owns the work: protocol/security defects → `v1.0-beta: Protocol
& correctness hardening`; packaging/release blockers → `v1.0-alpha: Release blockers`; docs,
validation and CI → `v1.0 GA: Docs, validation & release`; other defects → `v1.1: Post-release
hardening`. Dedupe first and amend instead of duplicating; link back to the PR.
