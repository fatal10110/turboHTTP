# 2026-11 BouncyCastle opt-in: Windows player builds at Stripping High (#258)

Test-run record for #258 AC1 on Windows. The run used `origin/main` f3a8975 and the host tooling
from #273 (`Tools~/unity/build-player.sh`). Setup: 2026-11-16, Unity 2021.3.45f2, Windows host
editor started from WSL2, one Unity process at a time (`flock /tmp/turbohttp-unity.lock`). Every
build ran with `--stripping High --network`; each Mono player ran on the host and reached example.com.

## Commands

```
'./Tools~/unity/build-player.sh' --target win64-mono   --stripping High --network
'./Tools~/unity/build-player.sh' --target win64-mono   --stripping High --bouncycastle --network
'./Tools~/unity/build-player.sh' --target win64-il2cpp --stripping High --network
'./Tools~/unity/build-player.sh' --target win64-il2cpp --stripping High --bouncycastle --network
```

## Results

| Build | Exit | Result |
| --- | --- | --- |
| win64-mono, High, no BC | 0 | `TURBOHTTP_BUILD ... backend=Mono2x stripping=High defines=''` succeeded. Player: `TURBOHTTP_SMOKE PASS http=TurboHTTP.Transport.RawSocketTransport ws=TurboHTTP.WebSocket.Transport.RawSocketWebSocketTransport json=ok bc=absent alpn=false rows=8 skipped=2`. `Managed/` holds Auth, Complete, Core, JSON, Retry, Transport, Unity, WebSocket, WebSocket.Transport. It has neither `TurboHTTP.Transport.BouncyCastle.dll` nor `TurboHTTP.SecureProtocol.dll`. The linker link.xml names no BC assembly. Rows: `tls-sslstream PASS backend=SslStream host=example.com status=200`, `tls-bouncycastle SKIP` (not in this build). |
| win64-mono, High, `--bouncycastle` | 0 | `defines='TURBOHTTP_BOUNCYCASTLE'` succeeded. Player: `TURBOHTTP_SMOKE PASS ... json=ok bc=present alpn=false rows=9 skipped=1`. `Managed/` adds `TurboHTTP.SecureProtocol.dll` and `TurboHTTP.Transport.BouncyCastle.dll`. The linker link.xml carries the `TurboHTTP.Transport.BouncyCastle` rule, and `reflection-targets` finds 37 types (35 without BC). Rows: `tls-sslstream PASS`, `tls-bouncycastle PASS backend=BouncyCastle host=example.com status=200 alpn-reported=true alpn-runtime=true alpn=h2`. |
| win64-il2cpp, High, no BC | 3 | `TOOLCHAIN_UNAVAILABLE`: "IL2CPP C++ code builder is unable to build C++ code ... Visual Studio 2019/2022 with C++ compilers and Windows 10 SDK". The build failed after 7 s, before UnityLinker produced `ManagedStripped`. |
| win64-il2cpp, High, `--bouncycastle` | 3 | Same `TOOLCHAIN_UNAVAILABLE`. |

## Findings

- The Mono row at High removes `TurboHTTP.SecureProtocol` when the define is absent, as Android IL2CPP
  High does (`2026-11-player-build-smoke.md`). The 2026-11-02 Mono rows ran at Stripping Disabled, which
  copies every assembly. This run asserts the result at High.
- With the define, the BouncyCastle provider resolves in a stripped (High) Windows Mono player and
  completes a real HTTPS request with ALPN h2.

## Not run

- win64-il2cpp: there is no Windows 10 SDK on this host (`C:\Program Files (x86)\Windows Kits\10`
  is absent). These rows are tracked in #1225.
- iOS Stripping High with the define: `needs-device-validation` (#99). Android IL2CPP High passed on a
  physical device (OnePlus 7, comment on #258 from 2026-11-08).
- Unity EditMode/PlayMode suites: nothing under `Tests/` changed. They run in the periodic host-editor run.
