# 2026-11 architecture.md assembly table (#284, #312)

## What
- `TurboHTTP.Complete` row: no longer says the bootstrap is its only code. It names `JsonUtilitySerializer` (#271)
  next to `TurboHttpBootstrap`, states the "install only over the built-in default" rule, that `TurboHTTP.JSON`
  stays engine-free, and that Complete omits `Transport.BouncyCastle`, `UniTask` and `UniTask.WebSocket`.
- `TurboHTTP.UniTask` row: refs `Core, UniTask`. New `TurboHTTP.UniTask.WebSocket` row: refs `Core, WebSocket, UniTask`
  (#116). Note on `TurboHTTP.Tests.UniTask` in that row.
- Transport section: one clause saying the bootstrap also installs `JsonUtilitySerializer` as default.

## Files
- `Development~/docs/agents/architecture.md`

## Decisions
- The Complete references column is unchanged (matches `Runtime/TurboHTTP.Complete.asmdef`; `ALLOWED_REFS` has `None` for Complete, so omission of UniTask is a convention, not machine-checked).
- Tests.UniTask is mentioned briefly in the UniTask.WebSocket row because the table lists no other test assemblies.
- Other docs that mention `TurboHTTP.UniTask` (Documentation~/Modules/UniTask.md, README, CHANGELOG, Third-Party Notices, 00-overview, phase 19) are out of scope and untouched.

## Verified
- Read `Runtime/UniTask/*.asmdef`, `Runtime/UniTask.WebSocket/*.asmdef`, `Tests/Runtime/UniTask/*.asmdef`, `Runtime/TurboHTTP.Complete.asmdef`, `Runtime/Bootstrap/*.cs` and `Tools~/ci/check_repo.py` `ALLOWED_REFS` on origin/main fdd458e.
- `Tools~/ci/check.sh`.
- Not run: Unity EditMode/PlayMode, IL2CPP, device (docs only). No deferrals.
