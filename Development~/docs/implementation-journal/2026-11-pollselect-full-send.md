# 2026-11 PollSelect: SendAsync on a full send buffer (#329)

## What
- On IL2CPP Android, `PollSelectSocketChannel.SendAsync` did not return while the kernel send buffer was full.
- Root cause, confirmed by decompiling Unity 2021.3.45f2 `System.dll` (profiles `unityaot-linux`, `unityaot-macos`,
  `unityjit-win32`; all identical):
  - `Socket.Send(byte[], int, int, SocketFlags, out SocketError)` loops `do { num += Send_internal(...) } while (num < size)`.
    It treats `WouldBlock` and `InProgress` as "retry", so on a non-blocking socket whose peer does not read it never
    returns.
  - `Send(ReadOnlySpan<byte>, ...)` calls `ToArray()` and then the same loop.
  - `Send(IList<ArraySegment<byte>>, SocketFlags, out SocketError)` makes one native call. In libil2cpp that is
    `Send_array_icall` → `SendArray` → `sendmsg`, and it returns the short count or `WouldBlock`.
  - The Editor runs on Windows, where Winsock takes the whole non-blocking send into its own buffers, so the loop never
    repeated there.
- Fix in `SendAvailable`:
  - Every native send goes through a new `SendOnce`, which calls the list overload with a reused one-element
    `ArraySegment<byte>[]`.
  - This covers both callers: the inline attempt on the caller's thread and the poll thread's `ServiceSend`.
  - The inline fast path stays. It is now non-blocking on every runtime.
- Follow-on fix in `RunPollLoop`:
  - Once a stalled send is pending on the poll thread, the loop could wait a full 20 ms write-only slice right after it
    delivered a receive, before the receiver had issued its next receive.
  - Effect on the phone: the full-duplex test took 13.4 s, against 1.0 s before the fix.
  - After a receive delivers data, write-only waits now use 1 ms slices for at most 20 waits (`RearmSliceUs`,
    `RearmSlices`; `ServiceRecv` returns whether it delivered data).
- Tests:
  - New `Tests/Runtime/Transport/PollSelectSocketChannelSendTests.cs`:
    - `SendAsync_PeerNotReading_ReturnsWithoutBlocking`: 4 MB to a loopback peer that does not read. The call must
      return within 5 s, then the peer drains the data and it must arrive intact.
    - `SendAsync_CancelWhileSendBufferFull_CompletesCanceled`.
    - `ListSend_NonBlockingPeerNotReading_ReturnsPromptly`: pins the class-library behavior the fix relies on.
  - `PollSelectSocketChannelTests.FullDuplex_LargeConcurrentSendAndReceive_CompletesWithoutDeadlock` is restructured:
    - The receive loop starts first.
    - The send call runs on its own thread.
    - The 30 s watchdog covers the whole body, including the connect.
    - Byte comparison is manual; `CollectionAssert` would box 4 MB.

## Files
- `Runtime/Transport/Tcp/PollSelectSocketChannel.cs`
- `Tests/Runtime/Transport/PollSelectSocketChannelSendTests.cs` (+ `.meta`)
- `Tests/Runtime/Transport/PollSelectSocketChannelTests.cs`

## Decisions
- **List overload, not capped slices.** The issue proposed capping each array send at `SendBufferSize` after
  `Poll(0, SelectWrite)`. That is not enough:
  - The array overload spins whenever a single call cannot be fully accepted.
  - Linux reports writable once free space reaches about a third of the buffer, in truesize accounting, so no cap is
    guaranteed to fit.
  - The list overload returns the kernel's short count by construction.
- **Allocation cost.** The list overload allocates a `GCHandle[1]` and a `WSABUF[1]` per native send call on
  Mono/IL2CPP. We accept that cost: the old path was allocation-free, but it never returned.
- **Inline path kept.** Small sends still complete on the caller's thread without a thread hop.
- **Ownership of `_sendSegment`.** Only the current owner of the send direction uses it:
  - the `SendAsync` caller during the inline attempt;
  - then the poll thread, after the hand-off under `_sendLock`.

  The segment is cleared after each call, so the caller's array is not kept alive.

## Verification
- Local gate `'./Tools~/ci/check.sh'`: OK.
- Before the fix, phone (OnePlus 7, Android 12, arm64, IL2CPP, Unity 2021.3.45f2, stripping High, filter
  `PollSelectSocketChannel`, with a temporary diagnostic test): 7 total, 4 passed, 3 failed.
  - A raw `Socket.Send(byte[4 MB])` did not return within 5 s.
  - Both `SendAsync` tests did not return within 5 s.
  - `ListSend` returned in 48 ms.
  - The finding is posted on #329.
- After the list-overload fix, before the rearm change:
  - Phone, filter `PollSelectSocketChannel|SocketChannelLifecycleTests`: 15/15 passed. Full-duplex took 13.4 s.
  - Emulator API 36 x86_64 (`run-emulator-tests.sh --api 36 --jobs 1`), filter `PollSelectSocketChannel`: 6/6 passed,
    3.9 s total.
- Final code (with the rearm change): see the PR body. During this lane, Windows interop from WSL failed
  intermittently (`UtilAcceptVsock: accept4 failed 110`), and two Editor runs could not start because of it.

## Not run
- The full Unity EditMode and PlayMode suites: host editor (periodic).
- iOS device.
- Emulators other than API 36.
