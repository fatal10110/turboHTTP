# TurboHTTP Full Library + Plan Review (2026-10-30)

Static review at commit `94a0e7b` (no builds, no device runs). Five parallel reviewers (HTTP/1.1+pool+TLS, HTTP/2, Core+modules, WebSocket+Unity+platform, roadmap+packaging), with spot-verification of the top items by reading the code.

Legend: **C** = confirmed by tracing code, **P** = plausible (depends on runtime/device behavior).

---

## 0. Release blockers (fix before anything else)

| # | Area | Issue | Where | |
|---|---|---|---|---|
| B1 | Security | BouncyCastle TLS does **no certificate chain validation** (TODO + `#warning`). Any cert with matching hostname/dates is accepted. Hostname match also weak (`*.com` matches, CN used when SAN exists, IP SANs ignored). | `Transport/BouncyCastle/TurboTlsAuthentication.cs:59-70, 151-171` | C |
| B2 | Security | Auto TLS mode silently and **permanently** (process-wide) switches to BouncyCastle on `NotSupportedException`/`TypeLoadException`/PNSE. `SslStreamTlsProvider` throws PNSE whenever `h2` is in the ALPN list and ALPN reflection is missing — and the pool always passes `{"h2","http/1.1"}`. So on any runtime without SslStream ALPN, **every HTTPS connection becomes MITM-able** by default. | `Tls/SslStreamTlsProvider.cs:145-151, 337`, `Tcp/TcpConnectionPool.cs:591-620` | C |
| B3 | Platform | Default transport registration relies only on `[ModuleInitializer]` (listed unsupported in Unity 2021.3 manual); nothing references Transport statically; no `RuntimeInitializeOnLoadMethod` backstop. Same for WebSocket.Transport. Likely `"No default transport configured"` on device. | `Transport/TransportModuleInitializer.cs`, `WebSocket.Transport/WebSocketTransportModuleInitializer.cs` | P |
| B4 | Packaging | 10 runtime `.cs` files have no `.meta` (e.g. `Core/StreamingOptions.cs`, `Http2/Http2ResponseBodySource.cs`, `Internal/ExpectContinueHelper.cs`). Immutable UPM install ignores them → compile failure. | see `git ls-files` check | C |
| B5 | Packaging | Android Kotlin plugin imports `androidx.work`; dependency only declared in a package-local `mainTemplate.gradle` Unity ignores → Gradle build fails for every Android user. | `Plugins/Android/...` | P |
| B6 | Docs/Samples | README, `Documentation~`, `Samples~` use `Middlewares`/`*Middleware`/`SendAsync` — API no longer exists (now `Interceptors`, `SendBufferedAsync`/`SendStreamingAsync`). Samples won't compile. | README, Samples~, Documentation~ | C |
| B7 | Legal | `package.json` placeholder author/email; `LICENSE.md` is an MIT-style grant for a closed-source product; vendored BouncyCastle has no MIT notice / third-party notices. | root | C |
| B8 | Process | Zero device/IL2CPP/CI evidence. Overview's own gate ("validate on physical iOS before past 3B") never met. | — | C |

---

## 1. Protocol correctness

### HTTP/2 (RFC 9113 / 7541)
| Sev | Issue | Where | |
|---|---|---|---|
| Critical | HEADERS/CONTINUATION for unknown/cancelled stream dropped **without HPACK decode** → decoder dynamic table desync → wrong headers on later responses or COMPRESSION_ERROR. Also leaves `_continuationStreamId` wrong → spurious connection PROTOCOL_ERROR. | `Http2Connection.ReadLoop.cs:220, 278` | C |
| Critical | Request trailer block encoded under `_writeLock`, sent later after lock release; returns slice of shared `_outputBuffer` → overwritten/returned to ArrayPool (use-after-return, can leak another request's `authorization`) and encoder table order ≠ wire order. | `Http2Connection.Send.cs:364-389`, `HpackEncoder.cs:45-89` | C |
| High | Stream ID allocated under `_streamCreateLock`, HEADERS sent later under `_writeLock` → IDs hit wire out of order → server connection PROTOCOL_ERROR. | `Http2Connection.cs:288` vs `:328` | C |
| High | Frame writes use request `ct`; header+payload are separate writes; cancel mid-frame desyncs framing; send window debited before write leaks on cancel. | `Http2FrameCodec.cs:188-194`, `Send.cs:54-61,173-185` | C |
| High | Any non-cancel exception from `DispatchAsync` (incl. MAX_CONCURRENT_STREAMS reached, handler exceptions) disposes the shared connection via key-based `Remove` → kills all in-flight streams; may dispose a newer healthy connection; replays request after handler fault. | `RawSocketTransport.cs:302-312, 563-576` | C |
| High | No queueing at MAX_CONCURRENT_STREAMS — 129th concurrent request fails (and kills the rest, above). | `Http2Connection.cs:281-286` | C |
| High | GOAWAY: connection disposed immediately instead of graceful drain; streams > last-stream-id not tagged as safely retryable. | `Http2ConnectionManager.cs:43-50,185-197` | C |
| High | Single shared `SemaphoreSlim` window waiter → lost wake-ups; SETTINGS window increase / RST / completion never wake uploaders → upload hangs until timeout (413 reported as Timeout). | `Http2Connection.cs:68`, `ReadLoop.cs:640-651` | C |
| High | Upload stops silently when response headers arrive (`IsResponseCompleted`), no END_STREAM/RST; cancellation registration already disposed → unbounded hang for full-duplex endpoints. | `Send.cs:137,216,266,317,346`, `Http2Stream.cs:98-106,251-255` | C |
| High | No cap on accumulated header block / CONTINUATION count (CVE-2024-27316 class) → OOM; pooled streams retain grown buffers. | `ReadLoop.cs:251,281` | C |
| High | Read loop uses stream refs without lifetime ref; cancel can return stream to static pool while read loop touches it → cross-request response delivery. | `ReadLoop.cs:116/220/278`, `Http2Stream.cs` | P |
| Med | PING ACKs ignored, no liveness timeout; unconditional 30s PINGs (battery; gRPC servers GOAWAY "too_many_pings"). | `Lifecycle.cs:85-117` | C |
| Med | Negative stream window after INITIAL_WINDOW_SIZE decrease treated as FLOW_CONTROL_ERROR (RFC allows negative). Snapshot race on new streams. | `ReadLoop.cs:478-486` | C |
| Med | WINDOW_UPDATE sent before local recv window adjusted → race → self-inflicted FLOW_CONTROL_ERROR. | `Lifecycle.cs:259-260` | C |
| Med | `content-length` vs DATA sum never checked → truncated bodies delivered as success (and cached). | `ReadLoop.cs:204-211` | C |
| Med | Request headers not validated (CR/LF/NUL, uppercase, `:`-prefixed) — trailers are. h2→h1 desync surface. | `Http2Connection.cs:337-360` | C |
| Med | `SetMaxDynamicTableSize` from read loop without `_writeLock` races `Encode`; min-size signaling missing. | `ReadLoop.cs:491-496`, `HpackEncoder.cs:94-98` | C |
| Med | Early final response (100-continue path) never closes client side (no END_STREAM/RST) → server stream slot leaked. 1xx with END_STREAM not rejected. | `Http2Connection.cs:385-400` | C |
| Low | Response handlers run synchronously on read-loop thread → one slow handler stalls all streams. | `Http2Stream.cs:257` | P |
| Low | Stream-level WINDOW_UPDATE overflow → connection error (should be stream). Idle-stream frames not PROTOCOL_ERROR. First frame not checked SETTINGS. Malformed responses don't send RST_STREAM. Huffman EOS padding check incomplete. | various | C |

### HTTP/1.1 (RFC 9110 / 9112)
| Sev | Issue | Where | |
|---|---|---|---|
| High | `Expect: 100-continue` early final response returns connection to pool though declared body never sent → server reads next request as body → **request desync**. A test asserts this behavior (`..._KeepAliveRejectionWithBody_ReusesConnectionAfterDrain`). | `RawSocketTransport.cs:1605-1612, 1688-1692` | C |
| High | BouncyCastle `TlsStream` async falls back to base `Stream` Begin/End which serialises read+write → H2 read loop blocks writes; 100-continue path deadlocks. BC is the designated IL2CPP H2 path. | `BouncyCastle/BouncyCastleTlsProvider.cs:121-126` | P |
| High | PollSelect mode: idle tight spin (`Thread.Sleep(0)`) per pooled connection → full core per connection, battery/thermal. | `Tcp/PollSelectSocketChannel.cs:156-182` | C |
| Med | EOF mid-headers treated as end of headers → truncated response accepted as 200 (cacheable). Same for trailers. | `Http11ResponseParser.cs:264-276` | C |
| Med | Transfer-Encoding: only first field read; final coding not required to be `chunked`; TE+CL accepted with reuse; TE in HTTP/1.0 not faulted. | `Http11ResponseParser.cs:425-458` | C |
| Med | obs-fold lines / whitespace before colon accepted as new fields; names not tchar-checked → smuggling surface. | `Http11ResponseParser.cs:282, 591-605` | C |
| Med | 101 Switching Protocols treated as final keep-alive → upgraded connection returned to pool. | `Http11ResponseParser.cs:243, 416-422` | C |
| Med | Stale-connection retry ignores "EOF before status line" (`FormatException`) — the most common stale case. | `RawSocketTransport.cs:353-357, 710-714` | C |
| Med | CONNECT reply line reader unbounded → OOM from malicious proxy; 2xx CONNECT body drained (RFC says ignore). | `RawSocketTransport.cs:1312-1364` | C |
| Low | Expect-continue cancel returns reader buffer to pool while read still pending (use-after-return). | `RawSocketTransport.cs:1596-1668` | P |
| Low | `Uri.Host` instead of `IdnHost` for Host/SNI; IP literals sent in SNI (RFC 6066 forbids). | `Http11RequestSerializer.cs:290-316`, `TurboTlsClient.cs:62-67` | P |
| Low | No SO_KEEPALIVE; 2-min idle → mobile NAT half-dead reuse → full timeout. | `TcpConnectionPool.cs:543-566` | P |
| Low | Bodiless non-idempotent request replayed after full send. Header values allow NUL/CTLs. | `RawSocketTransport.cs:159-168`, serializer `:514-528` | C |

### WebSocket (RFC 6455 / 7692)
| Sev | Issue | Where | |
|---|---|---|---|
| Critical | Listener awaits bounded `_messageQueue` (100) before raising `OnMessage`; only `ReceiveAsync` drains it. Event-only consumers (incl. `UnityWebSocketClient`) stall after ~100 msgs → socket unread → pong timeout → reconnect loop. | `WebSocket/WebSocketClient.cs:381-398`, `ResilientWebSocketClient.cs:380-396` | C |
| High | Cancel mid-message leaves partial fragmented message on wire, connection stays Open → next send = 1002. | `WebSocketConnection.SendReceive.cs:466-531` | C |
| High | `MaxMessageSize` not enforced for single-frame messages; `MaxFrameSize` default 16 MB > 4 MB message cap; queues bounded by count not bytes → OOM. | `MessageAssembler.cs:111-115` | C |
| Med | Unrequested/rejected extensions silently accepted (RFC 6455 §9.1 requires fail). | `WebSocketConnection.cs:174-210` | C |
| Med | permessage-deflate: accepts `client_max_window_bits<15` but always compresses with 15; accepts server context takeover but inflater is per-message; RSV1 on control frames not rejected. | `PerMessageDeflateExtension.cs` | C |
| Med | Non-101 handshake body read ignores Content-Length → hangs to timeout; 401 lost, resilient client retries auth failures forever. | `WebSocketHandshakeValidator.cs:166-180, 570-579` | C |
| Med | Close/dispose races (`_frameWriter` nulled without `_sendLock`) → NRE/ODE. | `WebSocketConnection.Lifecycle.cs` | C |
| Low | Echoes 1000 instead of received close code; closes TCP before server; ping→pong blocked behind large send. | — | C |

---

## 2. Security (beyond B1/B2)
| Sev | Issue | Where | |
|---|---|---|---|
| High | Interceptor order foot-gun: Redirect outer + Auth inner → Auth re-adds bearer token on cross-origin hop (redirect strip undone) → **token leak to third party**. Cache outer + Auth inner bypasses `AllowCacheForAuthorizedRequests`. | `Auth/AuthInterceptor.cs:32`, `RedirectInterceptor` | C |
| Med | Cookie handler pins first-hop URI → Set-Cookie of final hop stored under original host (cross-host cookie injection). | `Middleware/CookieInterceptor.cs:69` | C |
| Med | Cookie jar: tiny hand-written PSL (`github.io`, `co.za`, ... missing), IP hosts allow domain cookies, no Secure-overwrite protection, no `__Host-`/`__Secure-`, no size caps; write lock on every request. | `Middleware/CookieJar.cs` | C |
| Med | Logging logs full URL incl. query tokens; sensitive header list incomplete. Editor Monitor captures Authorization/Cookie raw by default (contradicts AGENTS.md), capture on even with window closed, up to ~10 GB retained. | `Observability/LoggingInterceptor.cs:86`, `MonitorInterceptor.cs:66`, `UHttpClient.cs:720-767` | C |
| Med | RecordReplay persists `access_token`/`refresh_token`/`client_secret` (only camelCase redacted; form bodies and `+json` not redacted). | `Testing/RecordReplayTransport.Redaction.cs:71-76` | C |
| Low | Raw sockets bypass iOS ATS / Android cleartext policy; proxy Basic creds over cleartext regardless of challenge scheme. | `ProxyTunnelConnector.cs:37-42` | C |

---

## 3. Functionality (modules)
| Sev | Issue | Where | |
|---|---|---|---|
| High | FileDownloader deletes partial/complete file on **any** non-206 (503/404/401) before status check. | `Files/FileDownloader.cs:163` | C |
| High | Cache stores any 2xx incl. 206; Range/If-Range ignored on lookup/store → partial bodies served as full and vice versa. | `Cache/CacheInterceptor.cs:768` | C |
| High | Cache unsafe-method & revalidation paths force full buffering (and flip shared `StreamingResponseRequested` flag) → streamed POST/SSE/downloads buffered in memory. | `CacheInterceptor.cs:348, 377` | C |
| High | OAuth refresh drops `refresh_token` when server doesn't rotate it → all requests 401 after first refresh. | `Auth/OAuthClient.cs:200` | C |
| High | `Retry-After` overrides `MaxDelay` uncapped → `Retry-After: 86400` stalls a day. | `Retry/RetryPolicy.cs:156-161` | C |
| Med | Decompression fails on HEAD/204/304 with `Content-Encoding` ("GZIP payload is truncated"). Default 256 MB decompress cap + `ToArray()` = mobile OOM. | `Middleware/DecompressionHandler.cs` | C |
| Med | `FollowRedirects=true` / `MaxRedirects` are silent no-ops without RedirectInterceptor. | `Core/UHttpClientOptions.cs:98` | C |
| Med | Client disposes interceptors it was handed; shared interceptor across clients breaks. | `Core/UHttpClient.cs:483` | C |
| Med | Pooled `UHttpRequest` has no lease generation (ABA) → stale ref mutates another caller's request. | `Core/UHttpRequest.cs` | C |
| Med | Cache: unbounded variant index; shared mutable headers between consumer and background store; 304 merge copies all headers (Content-Length/Encoding) and keeps stale Age; Expires without Date correction. | `CacheInterceptor*.cs`, `CacheStoringHandler.cs:68-110` | C |
| Med | Resume without If-Range/ETag; partial at final path; 206 without Content-Range appended; no `Accept-Encoding: identity`. | `FileDownloader.cs:111-180` | C |
| Med | Auth→JSON via reflection (`ProjectJsonBridge`) — hidden dependency, target methods not in link.xml → stripped under IL2CPP. OAuthClient refresh deadlocks if its client also carries the OAuth AuthInterceptor. | `Core/ProjectJsonBridge.cs`, `OAuthClient.cs:154-205` | P |
| Med | Background networking: iOS foreground `backgroundTimeRemaining=DBL_MAX` → overflow → `TimeSpan.Zero` → all foreground requests cancelled after 25 s when enabled. Begin/End routed through MainThreadDispatcher which doesn't tick while backgrounded. | `Plugins/iOS/TurboHttpBackgroundTaskBridge.mm:77`, `IosBackgroundTaskBridge.cs` | C/P |
| Med | Unity: `UnityWebSocketBridge` detects drop via substring "dropped" but message says "rejected"; lifecycle events share user queue (can be lost); pause/resume race disposes new client; Texture2D/AudioClip leaked on cancel; streamed AudioClip temp file deleted immediately. | `Unity.WebSocket/*`, `Unity/*` | C/P |
| Low | LiteJson can't deserialize POCOs → `AsJson<T>` fails out of the box; BOM breaks parse; `DateTime.Parse` shifts `Z` to local. | `JSON/LiteJson` | C |
| Low | `RateLimit` module is concurrency-only (no token bucket / 429 handling); host semaphore eviction race >1024 hosts. | `RateLimit/ConcurrencyLimiter.cs` | P |
| Low | Streaming: `context.Stop()/Clear()` on headers → logging/metrics durations wrong. | `UHttpClient.cs` | C |

---

## 4. Architecture & plug-and-play
- **Good:** Core has zero asmdef refs; interceptor/handler model with explicit body-ownership contract is sound; framing headers transport-owned; ConnectionLease/semaphore lifetime correct; h2 direct vs tunnel keys don't alias.
- **InternalsVisibleTo** from Core to 10 modules: modules can't version independently; third-party interceptors lack the internal safe-copy primitives first-party ones use (public `Clone()` throws for stream bodies).
- **No pipeline ordering model.** Order-dependent correctness (security, §2) with nothing enforcing it. Add `Order`/phase metadata or construction-time validation.
- **Discoverability:** all modules `autoReferenced:false`, `TurboHTTP.Complete` has no scripts → Assembly-CSharp users can't see JSON/Retry/Auth. Make user-facing modules auto-referenced or ship asmdef guidance.
- **Cross-module refs** contradict CLAUDE.md rule (Unity.WebSocket→Unity+WebSocket, UniTask→WebSocket, WebSocket.Transport→Transport, BouncyCastle→Transport, Editor→Observability). AGENTS.md lists exceptions; CLAUDE.md doesn't. UniTask→WebSocket forces HTTP-only UniTask users to pull WebSocket.
- **Core bloat:** `AdaptiveInterceptor`, `BackgroundNetworkingInterceptor`, `NetworkQualityDetector`, `ProxySettings` in Core; `ObjectPool<T>`/`ByteArrayPool` public in `Core.Internal`.
- **WebGL:** Core + most modules compile for WebGL, Transport doesn't → builds succeed, runtime throws. Exclude WebGL everywhere or add a build-time error.
- **link.xml in UPM packages likely ignored** — add `IUnityLinkerProcessor`. Transport link.xml preserves SslStream under `System.Net.Security` (Unity: `System.dll`) → no-op. BouncyCastle (~1268 files) preserved via Transport link.xml → ships in every IL2CPP build though "optional".
- `Development/` has a `.meta` → ships to customers (incl. internal security reviews). Rename `Development~` or move out.

---

## 5. Plan / roadmap
- **Scope creep:** 18/18a/19/22a-d built before Phase 8 (docs), 9 (device validation), 17 (release/CI). Freeze features; re-baseline v1.0.
- **Defer from v1.0:** 22d (very high complexity, no customer driver, allows public proxy API break), 25 gRPC, 19c, 23, 24 (README already claims AssetBundle support that doesn't exist), 16, 14.3 background networking (native plugins unvalidated), 18a → experimental. Ship 22c with h2-through-CONNECT off by default until device-validated.
- **Minimal path to v1.0:** B1–B7 → fix Critical/High protocol bugs above → rewrite README/docs/samples to real API → Phase 9 IL2CPP iOS/Android matrix → CI (game-ci) incl. `.meta` check → submit.
- **Plan defects:**
  - 22b.1: early-final-response keep-alive reuse is a desync bug (code + test). 417 retry-without-expectation missing. Plan contradicts itself on send-failure vs. final response. BouncyCastle concurrent read/write not in validation matrix.
  - 22c overview principle 6 ("no runtime catch-and-retry") contradicts 22c main plan, test, and code (`TcpConnectionPool.cs:594-620`); 22d.2 repeats it.
  - 22d.2: contradictory H1 reuse (always-close vs reuse) — always-close is a perf regression vs 22b.2; `Dispose` always RST (should END_STREAM on clean close); CONNECT_ERROR mapping / trailers-after-2xx missing.
  - 22d.1: `SemaphoreSlim` for MAX_CONCURRENT_STREAMS can't shrink; no REFUSED_STREAM retry rule; interaction with per-host pool undefined. `AuthBucket` unsalted SHA-256 of creds → HMAC with per-process key.
  - 22d.3: `H2ForwardUnsupported` trigger undefined; Host vs `:authority` rule missing; 407 scheme scope undefined.
  - Unquantified criteria ("no measurable latency regression", "bounded memory").
- **Docs drift:** CLAUDE.md describes `HttpPipeline`/`*Middleware`/`UHttpRequestBuilder`/immutable `UHttpRequest` — code is `InterceptorPipeline`/`*Interceptor`/mutable pooled request. Status list stale (22b.1/22b.2 done; 9/12/14/15/18/18a/19 missing). Lists nonexistent `Performance` module; "13 runtime assemblies" (actual 18). `00-overview.md` dead link, says System.Text.Json default, phase list stops at 17. CHANGELOG lists nonexistent modules.

---

## 6. Test gaps
- No CI, no device/IL2CPP runs, no `.meta` check.
- H2 tests mock-only → add interop matrix (nghttpx, h2o, Go, Kestrel, nginx, Envoy) + scripted adversarial server (CONTINUATION flood, HPACK index after cancel, GOAWAY drain with POSTs, INITIAL_WINDOW_SIZE decrease, MAX_CONCURRENT_STREAMS lowering, >100 concurrent with random cancels).
- No fuzzing: H1 parser, chunked/trailers, HPACK, frame codec, WS frame reader, LiteJson. Transport is `noEngineReferences` → SharpFuzz on plain .NET.
- No negative TLS tests (untrusted root, self-signed, expired, wrong host) — BouncyCastle would fail today.
- No soak test (overview's "1h, <10 MB growth").
- WebSocket: event-only 500-message test; mid-send cancel test.
- Flip the 100-continue reuse test.

---

## Suggested fix order
1. B1+B2 (TLS): make Auto fail closed; never fall back to unvalidated BC; treat `h2` as optional when `http/1.1` offered.
2. B3 bootstrap (`RuntimeInitializeOnLoadMethod` + `[Preserve]` in an engine-referencing assembly) + B4 metas + B5 Android plugin.
3. H2 Criticals/Highs: HPACK decode-always, trailer encode+write under one lock, ID alloc inside write lock, connection-token for frame writes, stream-scoped error handling + stream-slot queueing, GOAWAY drain, per-stream window waiters, header-block cap.
4. H1: 100-continue desync, EOF-in-headers, TE/obs-fold strictness, 101 handling, stale retry on EOF.
5. WS: OnMessage stall, mid-send cancel, size limits.
6. Module bugs: FileDownloader delete, OAuth refresh token, Retry-After cap, Cache 206/Range, Auth/Redirect ordering.
7. Docs/samples/package metadata/licensing → device matrix → CI → release.
