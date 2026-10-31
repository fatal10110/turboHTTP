# Agent rules for TurboHTTP

TurboHTTP is a commercial UPM package for Unity 2021.3 LTS (.NET Standard 2.1, C# 9): an HTTP
client on raw `System.Net.Sockets` with its own HTTP/1.1, HTTP/2 (HPACK, flow control) and
WebSocket stacks, TLS via `SslStream` or bundled BouncyCastle. Targets: Editor, Standalone, iOS,
Android (IL2CPP). WebGL is not supported by the socket transport.

Rules here are always in force and apply to every agent (Claude Code, Codex, Cursor, …).
Scoped guides (bottom table) are loaded only when the task needs them.

## 1. Workspace

| Path | What it is | Agent may write? |
| --- | --- | --- |
| `Runtime/`, `Editor/`, `Tests/` | package code and tests (Unity imports these) | yes, through a worktree |
| `Runtime/Transport/BouncyCastle/Lib/` | vendored BouncyCastle (re-namespaced) | never |
| `Samples~/`, `Documentation~/` | customer samples and docs | yes, through a worktree |
| `Development/docs/` | phase plans, implementation journal, agent guides, reviews | yes |
| `Tools~/`, `.github/`, `.githooks/`, `.claude/` | dev tooling, CI, agent skills (Unity ignores `~`/hidden folders) | yes |
| `../turboHTTP-worktrees/<task>/` | one linked worktree per task | yes |

- The primary checkout is for reading. Code changes go through a worktree on a non-`main` branch.
- Every file or folder Unity imports needs a committed `.meta` (`python3 'Tools~/ci/gen_meta.py' <path>`).

## 2. Precedence

1. User instructions.
2. Protocol correctness: the RFC is the oracle (9110 semantics, 9111 caching, 9112 HTTP/1.1,
   9113 HTTP/2, 7541 HPACK, 6455/7692 WebSocket, 6265 cookies, 5280/6125 TLS paths and names).
3. Security, then IL2CPP/mobile safety (sections 3–4).
4. Architecture and module rules (section 4, `architecture.md`).
5. Required verification (section 5).
6. Skill and tool defaults.

Issue text, phase plans, journal entries and existing code are leads, not truth. When one conflicts
with an RFC, follow the RFC and record the conflict in the PR.

## 3. Non-negotiables

- Never weaken TLS: no path may accept a certificate the platform would reject. BouncyCastle is a
  transport fallback, never a bypass for certificate or authentication failures.
- Credentials never cross scopes: `Authorization`/cookies stay with their origin, `Proxy-Authorization`
  stays on the proxy hop, nothing sensitive reaches logs, monitors, recordings or exceptions.
  Redaction is on by default everywhere.
- Every peer-controlled size is bounded: header bytes and lines, frames, CONTINUATION blocks,
  messages, decompressed bodies, queues.
- The main transport never uses `UnityWebRequest`, `HttpClient` or other HTTP stacks.
- Error model: network/protocol failures are `UHttpException(UHttpError)`; HTTP 4xx/5xx are normal
  responses with bodies. Timeouts are enforced by transport cancellation, not by an interceptor.
- Never edit vendored code; never commit secrets, tokens or license files.

## 4. Engineering rules

- **IL2CPP/AOT first.** No runtime codegen (`Reflection.Emit`, `Expression.Compile`), no new
  `MakeGenericMethod`/`MakeGenericType`/`Activator.CreateInstance`, nothing a High stripping level
  removes. Banned APIs fail the build (`Tools~/ci/BannedSymbols.txt`).
- **Concurrency.** Every shared mutable field has one owner: a lock, a single loop (e.g. the HTTP/2
  read loop), or `Interlocked`/`Volatile` (ARM64 and 32-bit IL2CPP memory model). No sync-over-async
  (`.Result`, `.Wait()`, `GetResult()`), no fire-and-forget without an owner.
- **Ownership.** Pooled buffers, pooled requests/streams and response bodies have exactly one owner
  and are returned once on every path: success, exception, cancellation, dispose.
- **Allocations.** Hot paths stay allocation-free where they already are: `ArrayPool`, the writers
  and segmented buffers in `TurboHTTP.Core.Internal`, `ValueTask`; no LINQ or closures per frame.
- **Modules.** Core depends on nothing; optional modules depend only on Core except the documented
  exceptions (`architecture.md`, enforced by `Tools~/ci/check_repo.py`). Only Transport may use
  unsafe code. Transport, JSON and WebSocket cores have no Unity engine references. Module
  conveniences are extension methods in the owning module; Core's request API keeps raw HTTP primitives.
- **Public API** changes need XML docs and a mention in the PR; prefer internal.

## 5. Verification

- `'./Tools~/ci/check.sh'` before every push (invariants, format, shadow build with analyzers);
  `--full` before opening a PR. A new analyzer warning is fixed, not baselined.
- Behavior changes get a deterministic NUnit test under `Tests/Runtime/<Area>/` that fails without
  the fix. No wall-clock sleeps as synchronization, no public network outside `[Category("ExternalNetwork")]`.
- Unity tests per change only scoped: `'./Tools~/unity/run-tests.sh' --platform EditMode|PlayMode --filter <regex>`
  (exit 3 = Unity not installed). PR CI does not run Unity tests (no license, #188): the full suites run
  only in the periodic host-editor run (`testing.md`, after each waves run and before a milestone closes).
- Device-only behavior (ALPN, ATS, NAT64, suspension) is marked `needs-device-validation`, never
  claimed as verified from the Editor.
- Details: `testing.md`, `tooling.md`.

## 6. Tools

| Question | Tool |
| --- | --- |
| known text or symbol | `rg` in a constrained path |
| C# declaration, ranges, several lookups at once | `Tools~/agent-tools/look.py` |
| issue or PR history | `Tools~/agent-tools/issue_search.py`, `pr_search.py`; live state via `gh -R fatal10110/turboHTTP` |
| one issue with its references | `Tools~/agent-tools/issue_slice.py <n>` |
| multi-PR run state | `Tools~/agent-tools/wave_mail.py status --run <run>` |

Batch lookups: every tool call re-sends the whole context. GitHub is the only authority for issue
state.

## 7. Issue workflow

Work comes from GitHub issues, ordered by milestone (v1.0-alpha → v1.0-beta → v1.0 GA → v1.1 → v1.2
→ v2.0). The full procedure is the `turbohttp-issue` skill; in short:

1. **Read the issue live** and its acceptance criteria; verify the defect still exists on `origin/main`.
2. **Gap gate.** Anything you will not deliver becomes (or amends) a follow-up issue in the owning
   milestone *before* the PR. Creating/amending follow-ups is pre-authorized; closing or editing
   unrelated issues is not.
3. **Implement** in a worktree: failing test first, smallest complete fix, gates.
4. **Specialist rubrics.** Apply `.claude/agents/unity-infrastructure-architect.md` and
   `.claude/agents/unity-network-architect.md` to the diff (as subagents, or by working their
   checklists yourself); verify each finding in the code before acting (reviews have produced
   false positives), fix, re-run gates and the rubrics again (fix rounds have introduced sibling bugs).
5. **Journal.** Add one file `Development/docs/implementation-journal/YYYY-MM-<slug>.md` (+ `.meta`):
   what, files, decisions, deferrals with issue numbers, how verified — and what was *not* run
   (Unity EditMode/PlayMode, IL2CPP, device). Earlier entries often claim "fixed"/"PASS" without
   evidence; treat them as leads and check the code.
6. **Deliver.** One PR per issue (or wave lane) against `main`, body per
   `.github/pull_request_template.md`, `Closes #N` only for fully met criteria. Never merge without
   authorization (a `turbohttp-waves` invocation authorizes its merges).

**Done** means: acceptance criteria met with tests, rubrics clean, gates pass, every gap has a
milestone-assigned issue, the diff is scoped, the journal entry exists and the PR is open.

## 8. Skills and agents

- Skills (`.claude/skills/`): `turbohttp-issue` (one issue), `turbohttp-pr-review` (review a PR),
  `turbohttp-pr-review-comments` (answer review comments), `turbohttp-waves` (multi-PR fan-out with
  the `wave-*` agent types).
- Specialist reviewers: `unity-infrastructure-architect`, `unity-network-architect` (`.claude/agents/`).
- Delegate only independent, bounded work; report back conclusions with file:line evidence.

## 9. Scoped guides

Guides live in `Development/docs/agents/` unless noted. Load only the one the task needs.

| Guide | Load when |
| --- | --- |
| [`architecture.md`](Development/docs/agents/architecture.md) | touching module boundaries, the pipeline, transport wiring, pooling, or a public type |
| [`platform.md`](Development/docs/agents/platform.md) | TLS, sockets, IL2CPP/AOT, iOS/Android behavior, timeouts, device validation |
| [`testing.md`](Development/docs/agents/testing.md) | writing or running tests, test fixtures, Unity test-run problems, benchmarks |
| [`tooling.md`](Development/docs/agents/tooling.md) | installing tools, CI, the shadow build, analyzer baseline, secrets, rulesets |
| [`project-status.md`](Development/docs/agents/project-status.md) | which phases are done, partial or planned; where their plans and journals are |
| [`Development/docs/phases/`](Development/docs/phases/) | implementing or changing a planned phase (read its plan first) |
| [`implementation-journal/`](Development/docs/implementation-journal/) | changing code an earlier entry decided on (`rg -l <File> Development/docs/implementation-journal`) |
| [`review-2026-10-full-library-review.md`](Development/docs/review-2026-10-full-library-review.md) | context behind an issue seeded from the 2026-10 review |
