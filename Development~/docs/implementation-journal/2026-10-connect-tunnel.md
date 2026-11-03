# 2026-10 connect-tunnel: bounded shared CONNECT reply reader, 2xx never drained, dispose-on-cancel (#44, #166, #162)

## What
- **One bounded CONNECT reply reader** (`Runtime/Transport/Http1/ConnectReplyReader.cs`, internal `ConnectReply` + `ConnectReplyReader`). Both `RawSocketTransport` (#44) and the WebSocket `ProxyTunnelConnector` (#166) use it.
  - It reads one byte at a time, so it never consumes a byte past the header block.
  - Every byte read counts against an 8 KB per-line cap and a 16 KB header-block cap. The status line counts toward the block cap.
  - A bare CR is replaced by SP (RFC 9112 section 2.2).
  - EOF before the blank line is an error. The old reader treated EOF as the end of the headers.
  - The status line must be `HTTP/... NNN`.
- **Status before drain** (RFC 9110 section 9.3.6): any 2xx switches to tunnel mode, and Content-Length/Transfer-Encoding are ignored.
  - `RawSocketTransport.EstablishConnectTunnelAsync` drains a body only on the 407 path it retries on. The drain follows RFC 9112 section 6.3: TE overrides CL, a non-chunked TE cannot be reused, an invalid CL is an error, and the drain is capped at 1 MiB.
  - The WS connector returns the raw stream on 2xx. `PrefetchedStream` was removed because nothing is over-read any more.
- **#162 dispose-on-cancel** in `RawSocketTransport`:
  - `RegisterDisposeOnCancel(lease.Connection, ct)` wraps `SendOnLeaseAsync` (request write and response-head read, Expect-continue included) in `DispatchOnLeaseAsync`.
  - It also wraps `EstablishConnectTunnelAsync` (CONNECT write/read and the TLS handshake over the tunnel) in `AcquirePreparedProxyLeaseAsync`.
  - Any non-OCE failure while the token is cancelled becomes `OperationCanceledException(ct)`. The existing catch ladder then maps it to Timeout or Cancelled.
  - The registration is disposed before the head reaches the body source or the lease reaches the pool. If the token fired in that window, the head or TLS stream is disposed and the call throws.

## Files
- Runtime: `Transport/Http1/ConnectReplyReader.cs` (new), `Transport/RawSocketTransport.cs`, `Transport/AssemblyInfo.cs` (IVT `TurboHTTP.WebSocket.Transport`), `WebSocket.Transport/ProxyTunnelConnector.cs`, `WebSocket.Transport/AssemblyInfo.cs` (new, IVT tests). Removed `WebSocket.Transport/Internal/ModuleInitializerAttribute.cs`.
- Tests: `Transport/Http1/ConnectReplyReaderTests.cs`, `Transport/Http1/RawSocketTransportCancellationTests.cs`, `WebSocket/ProxyTunnelConnectorTests.cs` (new). `Transport/Http1/RawSocketTransportTests.cs` now calls the reader directly instead of reflecting on the removed privates.

## Decisions
- The reader lives in Transport, not Core, and stays internal. The IVT to `TurboHTTP.WebSocket.Transport` is not a new asmdef reference: that asmdef already references Transport.
  - The IVT makes Transport's internal `ModuleInitializerAttribute` polyfill visible, which caused CS0436. WebSocket.Transport's identical copy was deleted, and it now uses the polyfill from Transport.
- Byte-at-a-time reading instead of a buffered reader. A CONNECT reply is a few hundred bytes once per tunnel. Exact framing keeps tunnel bytes and the next reply after a drained 407 on the stream, with no prefetch wrapper under the TLS stream or the pool rebind.
- 2xx (200-299) means tunnel in both stacks (RFC 9110 section 9.3.6). Before, both checked `== 200`.
- The WS header cap drops from 64 KB to 16 KB, the same as the HTTP transport.
- The WS 407 retry rule is unchanged in effect: retry only with Content-Length, no Transfer-Encoding and no `Connection: close`. A 407 with both TE and CL is no longer retried (TE overrides CL).
- Non-2xx failures that are not retried do not drain the body. The lease or stream is disposed anyway.
- h2 preface: `Http2Connection.InitializeAsync` already honours the request token through its SETTINGS-ack wait, and `Http2ConnectionManager` disposes the stream on failure. Only a test was added.

## Deferred
- #226 (v1.0-beta): the same dispose-on-cancel for the WebSocket transport's CONNECT and upgrade-handshake reads. #162 names only RawSocketTransport.
- #162's device criterion (iOS/Android IL2CPP run in the #99 matrix) is still open, so the PR says "Part of #162".

## Verification
- `'./Tools~/ci/check.sh'`: OK.
- The new tests, compiled against the shadow-build assemblies on .NET 8 (ad-hoc NUnit project, outside the repo): 29/29 pass.
- Against origin/main (detached temp worktree): the #44/#166 regression tests fail. That is 5/6; the chunked-407-not-retried test passes on both, as intended.
- On .NET 8 the #162 tests also pass on old code, because CoreCLR `NetworkStream` honours the token. They are a regression only under Unity Mono/IL2CPP.
- Not run: Unity EditMode/PlayMode (host editor, periodic), IL2CPP, devices.
