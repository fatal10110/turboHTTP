# 2026-11 docs-agents (#107, #189, #161, #110)

Docs-only lane. No `.cs` changes; Unity EditMode/PlayMode, IL2CPP and device runs were not run.

## What

- #189: deleted `Development/docs/architecture.md` (13 assemblies, `IHttpMiddleware` pipeline, "UHttpRequest
  is immutable") and `Development/docs/architecture_review.md` (2025-era review: Performance module,
  stale Core claims; no inbound links). `Development/docs/agents/architecture.md` is the single current
  architecture document. Its "Older documents" section no longer names the deleted file.
  Left alone on purpose: historical references in journals, the 2026-10-30 review tracker and the issue
  seed text, and the pre-22 phase docs (they carry the old names; the guide says to trust the code).
- #107: `agents/architecture.md` assemblies table now says Core has no assembly references but is not
  engine-free (`noEngineReferences: false`; `UnityEngine` only in `PlatformInfo`, `PlatformConfig`,
  `UHttpClient` play-mode check, `TurboDiagnostics`), lists the real `TurboHTTP.Complete` references
  (it omits `Transport.BouncyCastle` and `UniTask`) and marks BouncyCastle glue as not
  `noEngineReferences`. `CLAUDE.md` section 4 gets the same Core wording; the two bare
  `architecture.md` mentions now link to `Development/docs/agents/architecture.md`.
  `AGENTS.md` is a 4-line pointer to CLAUDE.md, so nothing to align. Criterion 2 (old architecture.md
  section 3) is superseded by #189 (file removed, agents guide current).
- #161: premise (a local uncommitted CLAUDE.md edit) does not exist; CLAUDE.md has no status section and
  `agents/project-status.md` already records 22d as planned with #125 then #126-#129. Row 4 of that
  table got a small update (old doc removed).
- #110: the rule sentence is quoted identically in the 22c overview principle 6, the 22c main plan,
  22c.4 Step 6 (plus 22c.2, 22c.3 and `agents/platform.md`); `rg 'no runtime catch-and-retry'
  Development/docs/phases` shows only that sentence. The AC3 test exists as
  `Tests/Runtime/Transport/TcpConnectionPoolTlsFallbackTests.cs`
  (`Auto_CertificateFailure_PropagatesWithoutProviderSwitch`,
  `Auto_PlatformTlsException_PropagatesWithoutProviderSwitch`), not in `TcpConnectionPoolTests`.
  22d.2 Step 4.5 cannot be edited: `Development/docs/phases/phase22d/` is not in the repo (review
  2026-10-30 S2). #125 already carries the verbatim sentence as an amendment comment. #110 therefore
  stays open ("Part of").

## Decisions

- Delete rather than stub the old files: no inbound link needs to survive, git history keeps them.
- The stale descriptive sentence "BouncyCastle has no full chain validation until #33 lands" in the 22c
  docs was not touched (the rule itself is correct; #240 added Editor/desktop validation). Minimal diff.

## Deferred

- 22d.2 Step 4.5 quoting the sentence: with the 22d plan correction (#125).
- Pre-22 phase docs and 00-overview/CHANGELOG naming drift: #108 (owner decision, needs #10, #3).

## Verification

`rg -w 'IHttpMiddleware|HttpPipeline' CLAUDE.md AGENTS.md Development/docs/agents` hits only the
"Older documents" note. Asmdef facts checked against `Runtime/**/*.asmdef`. Gate: `check.sh` (see PR).
