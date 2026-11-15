# TurboHTTP full-library review (2026-11-15)

Static review of the whole package at commit `0e0f1bc`. No builds, no Unity test runs and no device
runs were part of it, so device-only behaviour is at most "plausible" here, never verified.

## Method

The codebase was split into 13 slices (table below). Each of the ten code slices was read in full by
three independent reviewers, one per lens:

| Lens | Angles |
| --- | --- |
| protocol | RFC conformance, protocol implementation, security |
| runtime | performance, resource management, concurrency, platform compatibility (IL2CPP, Mono, mobile) |
| design | architecture, code smell, best practices, pluggability and final build size |

Three cross-cutting slices had one dedicated reviewer each: packaging and build size, documentation
and samples against the real API, and the test suite (swept, not read line by line).

Every critical, high and medium finding then went to a second agent whose task was to refute it by
re-tracing the code. Its verdict is the **V** column: **C** confirmed, **P** plausible (the code is as
described, the outcome depends on runtime, device or peer behaviour), **U** unverified. Low-severity
findings were not sent to a verifier and are all **U**. Reviewers had the list of open issues; a
finding an open issue already tracks carries the issue number.

Limits of this review, so the numbers are read correctly:

- The verifiers refuted none of the 333 findings they checked. They did change the severity of 95
  of them (mostly medium to low) and marked 30 as plausible only, so they were not rubber
  stamps, but a zero refutation rate means some false positives probably remain. Seven findings (the
  critical one and six high ones) were additionally checked by hand against the code and hold.
  Verify a finding in the code before acting on it, as `CLAUDE.md` section 7 requires.
- Duplicates between lenses and slices were merged for critical, high and medium findings. Low
  findings were not de-duplicated, so the low count overstates the number of distinct defects.
- The HTTP/2 reviewers each skipped one file in full, the packaging reviewer six and the test
  reviewer worked by sweeps; each slice file lists what was not read.
- Severity scale: **critical** = security hole, corruption or cross-request leak, or a crash/hang in
  a default path; **high** = wrong behaviour, a protocol violation with interop impact, a leak or DoS
  reachable in realistic use, or a break on a supported platform; **medium** = wrong in edge cases, a
  measurable hot-path cost, or an architecture problem that will cause defects or blocks modularity;
  **low** = minor.

## Findings by slice

| Slice | Scope | Critical | High | Medium | Low | Details |
| --- | --- | --- | --- | --- | --- | --- |
| Transport core | RawSocketTransport, connection pool, socket I/O modes, proxy/CONNECT, Happy Eyeballs, NAT64 |  |  | 15 | 45 | [net.md](net.md) |
| TLS | provider selection, SslStream provider, BouncyCastle glue, managed and native certificate verifiers |  |  | 8 | 35 | [tls.md](tls.md) |
| HTTP/1.1 | request serializer, response parser, body sources, chunked coding, Expect: 100-continue |  | 2 | 9 | 46 | [http1.md](http1.md) |
| HTTP/2 | connection, read loop, send path, streams, flow control, HPACK, connection manager | 1 | 3 | 17 | 51 | [http2.md](http2.md) |
| Core | client API, request/response model, headers, interceptor pipeline, dispatch bridges, pooling internals |  | 2 | 9 | 62 | [core.md](core.md) |
| Middleware and Auth | cookies, redirects, decompression, default headers, Auth |  | 5 | 17 | 54 | [mw.md](mw.md) |
| Cache, Retry, RateLimit | HTTP cache, retry policy, rate limiting |  | 1 | 15 | 53 | [cache.md](cache.md) |
| Utility modules | JSON, Files, Observability, Testing, Editor |  | 1 | 12 | 71 | [util.md](util.md) |
| WebSocket | protocol core, permessage-deflate, client and resilient client, raw-socket WebSocket transport |  | 4 | 22 | 53 | [ws.md](ws.md) |
| Unity integration | main-thread dispatch, asset handlers and decoders, mobile bridges, Unity WebSocket, UniTask, bootstrap, native plugins |  | 1 | 27 | 54 | [unity.md](unity.md) |
| Packaging and build size | asmdef graph, link.xml, bootstrap, package.json, build processors |  |  | 7 | 10 | [pkg.md](pkg.md) |
| Documentation and samples | README, Documentation~, Samples~ against the real API and defaults |  |  | 5 | 32 | [docs.md](docs.md) |
| Test suite | determinism, wrong-behaviour assertions, coverage gaps, what CI runs |  |  | 8 | 17 | [tests.md](tests.md) |
| **Total** | | **1** | **19** | **171** | **583** | |

Raw findings: 828. Removed as duplicates of another finding: 54. Refuted by a verifier: 0. Of the 774 findings counted above, 46 are already tracked by an open issue (the issue number is on the finding).

## Critical and high findings

| ID | Sev | Angle | Finding | Where | V | Issue |
| --- | --- | --- | --- | --- | --- | --- |
| [http2-protocol-01](http2.md) | critical | rfc-compat | HPACK Huffman table codes '{' (0x7B) as 0x7fffe/19 bits instead of RFC 7541 Appendix B 0x7ffe/15 bits, breaking decode of conformant peers and corrupting encoded output | `Runtime/Transport/Http2/HpackHuffman.cs:138` | C |  |
| [cache-protocol-01](cache.md) | high | security | A peer-chosen stale-while-revalidate value overflows DateTime in CacheEntry.IsStaleWhileRevalidate, which makes the URL unfetchable and breaks every later MemoryCacheStorage.SetAsync | `Runtime/Cache/CacheEntry.cs:161` | C |  |
| [core-design-01](core.md) | high | architecture | Client-level policy (AllowCleartext, Proxy, DefaultHeaders, DefaultTimeout, redirect settings) is stamped on requests only in CreateRequest, so requests built with the public constructor or with replaced metadata are sent without it. | `Runtime/Core/UHttpClient.cs:747` | C |  |
| [core-design-02](core.md) | high | platform-compat | ResolveUri treats a leading-slash relative URL as absolute on Unix-like runtimes, so the documented client.Get('/path') with BaseUrl becomes file:///path on macOS, Linux, iOS and Android. | `Runtime/Core/UHttpClient.cs:733` | C |  |
| [http1-protocol-02](http1.md) | high | rfc-compat | POST/PUT/PATCH with an empty body are sent with neither Content-Length nor Transfer-Encoding, and a caller-set Content-Length: 0 is stripped | `Runtime/Transport/Http1/Http11RequestSerializer.cs:340` | C |  |
| [http1-runtime-01](http1.md) | high | concurrency | Abort()/Dispose from another thread during a pending body read returns the reader's pooled buffers while the socket read still targets them, and leaves the read to be ended by the platform alone | `Runtime/Transport/Http1/Http11ResponseParser.cs:1100` | C |  |
| [http2-runtime-01](http2.md) | high | concurrency | The response-start work item signals completion on an Http2Stream after dropping its lifetime reference, and Initialize never resets the completion source, so a late signal poisons the pooled stream for the next request. | `Runtime/Transport/Http2/Http2Stream.cs:661` | C |  |
| [http2-runtime-05](http2.md) | high | performance | Every request of a cold burst dials its own TCP+TLS connection before the HTTP/2 connection is published, and a request whose redundant dial fails is failed although a working HTTP/2 connection exists. | `Runtime/Transport/RawSocketTransport.cs:604` | C |  |
| [http2-runtime-06](http2.md) | high | performance | The default 65535-byte stream receive window, credited only as the consumer reads, caps each HTTP/2 download at about 64 KB per round trip (#406 measures loopback and understates it). | `Runtime/Core/Http2Options.cs:13` | C | #406 |
| [mw-protocol-01](mw.md) | high | security | DecompressionHandler accepts an unbounded number of content codings, so one response header forces thousands of stacked inflaters (memory bomb and unbounded recursion depth). | `Runtime/Middleware/DecompressionHandler.cs:742` | C |  |
| [mw-protocol-02](mw.md) | high | security | The decoded-size limit only counts the outermost layer's output, so stacked codings (or zero-output DEFLATE input) burn unbounded CPU that the timeout cannot interrupt. | `Runtime/Middleware/DecompressionHandler.cs:202` | C |  |
| [mw-protocol-03](mw.md) | high | security | DefaultHeadersInterceptor placed inside RedirectInterceptor re-adds Authorization/Cookie/API-key default headers on cross-origin redirect hops. | `Runtime/Middleware/DefaultHeadersInterceptor.cs:37` | C |  |
| [mw-runtime-01](mw.md) | high | concurrency | RedirectHandler dispatches the next hop from inside the previous hop's OnResponseStartAsync, so the previous hop's inner interceptor frames (and its ConcurrencyInterceptor permit) are still held: same-host redirects deadlock, and no timeout ends the wait. | `Runtime/Middleware/RedirectHandler.cs:242` | C |  |
| [mw-runtime-02](mw.md) | high | concurrency | On Unity's Mono/IL2CPP class library every streamed compressed read ends in the sync bridge BodySourceStream.Read, which reads with CancellationToken.None: the consumer's token and the 2 s dispose-drain bound are lost, and Abort() is a no-op once DisposeAsync has started. | `Runtime/Middleware/DecompressionHandler.cs:559` | C |  |
| [unity-protocol-01](unity.md) | high | protocol-impl | The Android deferred-work WorkManager job returns Result.retry() forever and nothing cancels it after the process dies, so each queued request leaves an immortal job that keeps waking the app | `Plugins/Android/TurboHttpBackground.androidlib/src/main/java/com/turbohttp/background/TurboHttpBackgroundWorker.java:24` | C |  |
| [util-runtime-01](util.md) | high | resource-management | MetricsHandler and MonitorInterceptor read the request (Content / TryGetBufferedContent) when a streamed body is disposed, after the fluent SendStreamingAsync has already disposed the client-owned request, so response disposal throws ObjectDisposedException and the completion is never recorded. | `Runtime/Observability/MetricsHandler.cs:101` | C |  |
| [ws-design-01](ws.md) | high | best-practices | WebSocketClient.Create() throws ArgumentNullException whenever no transport is passed and reconnect is not enabled, which is the documented default path | `Runtime/WebSocket/WebSocketClient.cs:58` | C |  |
| [ws-design-03](ws.md) | high | best-practices | User event handlers run unguarded inside FinalizeClose and other critical sections, so one throwing handler leaves the connection permanently half-finalized with its socket open | `Runtime/WebSocket/WebSocketConnection.Lifecycle.cs:111` | C |  |
| [ws-protocol-01](ws.md) | high | protocol-impl | ResilientWebSocketClient keeps reconnecting after CloseAsync or Abort issued during backoff, leaving a live connection after OnClosed, and CloseAsync can block for the whole retry budget. | `Runtime/WebSocket/ResilientWebSocketClient.cs:568` | C |  |
| [ws-protocol-02](ws.md) | high | protocol-impl | ResilientWebSocketClient discards messages still queued for ReceiveAsync when the connection ends terminally (server Close with a non-reconnect code, or retries exhausted). | `Runtime/WebSocket/ResilientWebSocketClient.cs:610` | C |  |

## What the findings add up to

The slice files hold the evidence; this section groups the medium and higher findings by the angle
that was asked for. IDs link to the slice file through the tables above and below.

### RFC conformance and protocol implementation

- **HPACK (critical).** The Huffman table codes `{` as `0x7fffe`/19 bits; RFC 7541 Appendix B says
  `0x7ffe`/15 bits (http2-protocol-01). A conformant server that Huffman-codes a field value containing
  `{` (JSON in `report-to`, `nel`, cookies) makes the client fail the connection with
  COMPRESSION_ERROR, and the client's own Huffman output for `{` is wrong. The reviewers note that a
  default Unity player cannot negotiate `h2` without the opt-in BouncyCastle provider, which limits
  the reach today and explains why interop runs did not catch it.
- **HTTP/1.1 framing.** POST/PUT/PATCH with an empty body go out with no `Content-Length`, and a
  caller-set `Content-Length: 0` is stripped (http1-protocol-02, high; RFC 9110 8.6). A connection is
  pooled after a request that sent `Connection: close` (http1-protocol-03), bytes after a complete
  response are discarded and the connection is pooled as clean (http1-protocol-04), and an early
  final response is lost when the body write fails (net-protocol-01).
- **HTTP/2.** Field blocks with a pseudo-header after a regular field or a repeated `:status` are
  accepted (http2-protocol-04); a connection that failed in the read loop is never closed
  (http2-protocol-02); the buffered collector drops response trailers (core-protocol-02).
- **Cache (RFC 9111).** `must-revalidate` is treated like `no-cache` (cache-protocol-03); with one
  option combination entries are fresh forever (cache-protocol-02); a redirect chain's final response
  is stored under the original URL when Cache sits outside Redirect (cache-protocol-06); a truncated
  body can be committed (cache-protocol-08).
- **Cookies (RFC 6265).** Quotes are stripped from values, `Expires` is parsed with
  `DateTime.TryParse` instead of the cookie-date algorithm, and control characters are stored
  (mw-protocol-05, -07, -08).
- **WebSocket.** The RFC 6455/7692 wire core traced clean. The defects are in the clients above it:
  `ResilientWebSocketClient` keeps reconnecting after `CloseAsync`/`Abort` and discards queued
  messages on a terminal close (ws-protocol-01, -02, high).
- **Connection establishment.** Happy Eyeballs waits out the stagger timer after a fast failure and
  stops after two blackholed addresses (net-protocol-05, -06); the default first-byte watchdog fails
  an idempotent request whose server needs more than 10 s on a reused connection (net-protocol-02).

### Security

- **Credential scope depends on interceptor order.** `DefaultHeadersInterceptor` inside
  `RedirectInterceptor` re-adds `Authorization`/`Cookie` on cross-origin hops (mw-protocol-03, high);
  `AuthInterceptor` sends its token to every origin the client addresses (mw-design-11); the cache's
  authorized-request guard only sees headers present at its own pipeline position
  (cache-protocol-07); `client.WebSocket()` copies default headers into the handshake
  (unity-protocol-14). Only two orderings are enforced, and only for first-party types (core-design-08).
- **Decompression.** `Content-Encoding` accepts any number of stacked codings (mw-protocol-01, high)
  and the decoded-size limit counts only the outermost layer, so stacked or zero-output input burns
  CPU the timeout cannot interrupt (mw-protocol-02, high).
- **Peer-controlled values that throw.** A large `stale-while-revalidate` overflows `DateTime` and
  breaks the URL and later cache stores (cache-protocol-01, high); large delta-seconds throw
  `OverflowException` (cache-protocol-05).
- **Cookie scope.** A trailing dot defeats the public-suffix check: `Domain=com.` is stored and sent
  to every `*.com.` host (mw-protocol-06).
- **TLS.** No high finding. The BouncyCastle host-name check still falls back to the subject CN,
  which on Android accepts certificates the platform's own verifier rejects (tls-protocol-01).
- **Redaction gaps.** Redirect and cache timeline events, exception messages, URL-valued headers and
  Monitor body captures bypass `RedactionPolicy` (mw-protocol-04, cache-protocol-10, util-protocol-01,
  -02, -03; partly #117); recordings store an HMAC of the unredacted body next to its key
  (util-protocol-04).
- **Proxy and client policy.** Request-level `ProxySettings` are not validated, so an `https://`
  proxy address is contacted in cleartext (net-protocol-04); cleartext, proxy and redirect policy
  travel in caller-mutable request metadata and are absent for standalone requests (core-design-01,
  high).
- **Unbounded inputs.** Texture and audio decoding have no default bound on source bytes or decoded
  size (unity-protocol-06, -07, -08); the HPACK encoder table follows whatever size the server
  announces (http2-protocol-03); Vary variants accumulate without limit per URL (cache-protocol-09).

### Resource management and concurrency

- **Abort or dispose racing a pending read.** HTTP/1.1 body source returns pooled buffers while the
  socket read still targets them (http1-runtime-01, high) and can start a second concurrent drain
  read (http1-runtime-02); the cache tee has the same shape (cache-runtime-02); a late completion
  signal poisons a pooled `Http2Stream` for the next request (http2-runtime-01, high).
- **Redirects nest dispatches.** The next hop is dispatched from inside the previous hop's
  `OnResponseStart`, so the previous hop's concurrency permit is still held: same-host redirects
  deadlock under `ConcurrencyInterceptor` (mw-runtime-01, high).
- **User callbacks inside critical sections.** A throwing WebSocket event handler leaves the
  connection half-finalized with its socket open (ws-design-03, high) or kills the reconnect loop
  (ws-runtime-06).
- **Leaks on abandonment.** A dropped `UHttpStreamingResponse` never aborts its body source, so the
  lease and per-host permit leak (core-runtime-02); transport dispose neither cancels in-flight
  HTTP/1.1 exchanges nor wakes queued requests (net-runtime-07); abort during WebSocket connect leaks
  the stream that arrives later (ws-runtime-03).
- **Global before per-host permit.** Requests queued for one saturated host hold global permits and
  block every other host (cache-protocol-12).
- **Opt-in socket modes.** PollSelect joins its own poll thread on dispose (net-runtime-01); SAEA
  and PollSelect cancellation bypasses the shutdown-first close order (net-runtime-03, #384).

### Performance

- **HTTP/2 downloads** (#406): 64 KiB stream window credited only as the consumer reads caps a
  stream at about 64 KiB per round trip (http2-runtime-06, high); one WINDOW_UPDATE write per DATA
  frame, two stream writes per frame, a second copy of every DATA payload (http2-runtime-07, -08,
  -09); the first request waits for the SETTINGS ACK (http2-runtime-10).
- **Cold burst to an HTTP/2 origin** dials one TCP+TLS connection per request and discards all but
  one (http2-runtime-05, high).
- **Decompression on Unity** goes through `DeflateStream`'s async-over-sync path: a pool thread per
  read, the consumer's token and the dispose-drain bound are lost (mw-runtime-02, high; mw-runtime-03).
- **Per-request cost in Core**: the plugin capability guard hashes the request body about six times
  (core-runtime-04), `Clone`/`CopyWithSharedContent` deep-clone twice (core-runtime-05), `HttpHeaders`
  allocates a list and array per header (core-runtime-06).
- **Cache** copies every candidate body before the header-only storability decision (cache-runtime-01).
- **WebSocket**: unbuffered frame reader, 8 KB chunked writes, a new deflater and payload-sized
  arrays per message, an extra copy per message on the event path (ws-runtime-09, -11, -12, -13).
- **Unity**: about 15 allocations per main-thread dispatch (unity-runtime-02), whole-body copies in
  texture, audio and file helpers (unity-runtime-09, -15).

### Platform compatibility

- **Leading-slash relative URLs** (`client.Get("/path")` with `BaseUrl`) parse as `file:///path` on
  Unix-like runtimes, so the form every doc and sample uses fails on macOS, Linux, iOS and Android
  and works in the Windows Editor (core-design-02, high).
- **Android background work**: the WorkManager job returns `Result.retry()` forever and nothing
  cancels it once the process died (unity-protocol-01, high).
- **Unity runtime specifics**: `Task.AsUniTask()` in the WebSocket UniTask adapters fails on worker
  threads (unity-runtime-20); a replaced PlayerLoop or a destroyed dispatcher GameObject stops
  main-thread dispatch for good (unity-runtime-03, -04); the Windows close order is chosen from an
  IOCP assumption neither Mono nor IL2CPP satisfies (net-runtime-04, plausible).
- **Packaging**: `package.json` declares none of the five built-in engine modules the Unity
  assemblies need (pkg-pluggability-06); the WebGL build guard fails every WebGL build of a project
  that merely has the package installed (pkg-pluggability-07).

### Architecture, code smell and best practices

- **Client policy as request metadata** (core-design-01, high) is the root of several other findings:
  standalone requests skip cleartext/proxy/default-header policy, and the proxy decision is made once
  for the first URL and never re-evaluated for redirect targets (net-protocol-03).
- **`RawSocketTransport`** is one 2,434-line class; its copy-pasted blocks have diverged, and the
  proxy copy of the stale-connection retry lacks the hardening the direct copy received
  (net-design-01, -02, net-protocol-07, http1-design-06).
- **Duplicated parsers and validators that disagree**: three received-field validators
  (http1-design-04), a third HTTP response-head parser and a second proxy configuration in WebSocket
  (ws-design-07, -09), two WebSocket clients with diverged receive plumbing (ws-design-22), Unity
  download helpers that re-implement `FileDownloader` (unity-design-15).
- **Error model**: BCL exception types classified by three mappers and a message-string match
  (http1-design-02); WebSocket connect failures collapse into retryable errors (ws-design-05); every
  TLS handshake failure becomes `CertificateError` (tls-design-04, #451).
- **Options that are not honoured**: `Http2Options.MaxFrameSize`/`MaxConcurrentStreams`
  (http2-design-04), `RedirectInterceptor` constructor defaults (mw-design-02), the WebSocket
  transport's `tlsBackend` (ws-design-10), `IdleTimeout` without a ping interval (ws-design-15).
- **API defects on the documented path**: `WebSocketClient.Create()` throws
  `ArgumentNullException` unless reconnect is enabled (ws-design-01, high) and discards its options
  (ws-design-04); a third-party `IHttpTransport` cannot read stream bodies (core-design-13, #76).

### Pluggability and final build size

The assembly graph is sound: references match the architecture table and optional modules depend
only on Core. What defeats stripping is rooting, not references (packaging reviewer's figures):

- A stripped "HTTP/1.1 GET with JSON" player still links about 55 of 75 thousand first-party lines
  where roughly 26 thousand are needed.
- The linker processor applies every module's `link.xml` whenever the module compiles, which is
  always; 32 of 44 rules protect code nothing reaches by reflection (pkg-pluggability-01, plausible).
- `TurboHTTP.Complete` is always linked and cannot be excluded from a package install, so the
  documented lean configuration is unreachable (pkg-pluggability-02).
- The HTTP/2 stack (8.4 thousand lines, 38% of Transport) is statically referenced by
  `RawSocketTransport` and linked into every player (pkg-pluggability-05).
- The vendored BouncyCastle assembly (about 139 thousand lines) has no define constraint, so it ships
  in every unstripped Mono player (pkg-pluggability-04, #258); the Testing module ships the same way.
- `TurboHTTP.Unity` runs eleven startup hooks in every player (unity-design-02) and the Editor-only
  Monitor registration is rooted in players (util-design-01); the plugin system is about 13% of Core
  with no first-party consumer (core-design-10).

Restructurings the packaging reviewer ranks by payoff per effort:

1. Put a define constraint on `TurboHTTP.SecureProtocol` (one line).
2. Give the Android cleartext hook an explicit root (pkg-pluggability-03), then delete the
   `link.xml` rules that name TurboHTTP types and wrap the Monitor registration in `UNITY_EDITOR`.
3. Add an opt-out define for `TurboHTTP.Complete` and trim its references.
4. Move HTTP/2 behind a registration seam or a define (largest first-party saving).
5. Split audio out of `TurboHTTP.Unity` and declare engine-module dependencies in `package.json`.
6. Move the asmdef invariants into `check_repo.py` so PR CI enforces them.

### Tests and documentation

- PR CI executes none of the roughly 2,990 NUnit cases; about 2,700 are engine-free and could run in
  a plain .NET lane the way `h2-interop` already links test sources (tests-quality-01).
- Three HTTP/2 negative tests cannot fail because their assertion sits inside
  `catch (Exception) { }` (tests-quality-02); about 25 sites use fixed sleeps as synchronisation
  (tests-quality-05); a filtered Unity run that matches no test exits 0 (tests-quality-21).
- **Time-critical:** the committed public certificate chain used by
  `PublicChain_ProductionConfiguration_IsTrustedByPlatformRootStore` expires on 2026-11-26; on
  Android the verifier ignores the fixed verification time, so that run fails from 2026-11-27
  (tests-quality-13).
- Documentation: the install instructions name a tag that does not exist (docs-accuracy-02, #101),
  the plugin example throws as written (docs-accuracy-03), the WebSocket snippet loses early messages
  (docs-accuracy-05), and the timeout and auth-scope claims are stronger than the code
  (docs-accuracy-07, -08).

### Suggested first fixes

1. http2-protocol-01: correct the Huffman entry for `{` and add a test that checks the whole table
   against RFC 7541 Appendix B (a Kraft-sum assertion catches any such typo).
2. core-design-02: resolve relative URLs against `BaseUrl` before trying an absolute parse, and
   reject non-http(s) schemes in Core.
3. mw-protocol-01, mw-protocol-02: cap the number of content codings and count intermediate bytes.
4. mw-protocol-03 with mw-design-11 and cache-protocol-07: bind default and auth headers to an
   origin, or enforce the orderings that keep credentials in scope.
5. cache-protocol-01, cache-protocol-05: clamp peer-supplied durations.
6. http1-protocol-02: send `Content-Length: 0` for methods that define content.
7. ws-design-01, ws-protocol-01, ws-protocol-02, ws-design-03: the WebSocket client-layer defects.
8. mw-runtime-01: release the previous hop before dispatching a redirect.
9. http1-runtime-01, http2-runtime-01: serialise abort against in-flight reads and late completions.
10. unity-protocol-01 and tests-quality-13: bound the Android worker's retries; re-capture or gate
    the expiring certificate before 2026-11-26.

## Medium findings

One line each; evidence, failure scenario, fix direction and the verifier note are in the slice file.

### Transport core (15)

| ID | Angle | Finding | Where | V | Issue |
| --- | --- | --- | --- | --- | --- |
| [net-design-01](net.md) | architecture | RawSocketTransport is a 2434-line multi-responsibility class whose HTTP/2 hand-off sequence is copy-pasted four times and has already diverged | `Runtime/Transport/RawSocketTransport.cs:622` | C |  |
| [net-design-02](net.md) | code-smell | The HTTP/1.1 stale-connection retry exists in a direct and a proxy copy, and only the direct copy received the fresh-socket retry and the first-byte watchdog | `Runtime/Transport/RawSocketTransport.cs:1093` | C |  |
| [net-protocol-01](net.md) | rfc-compat | HTTP/1.1 non-Expect send path never reads the server's early final response when the request body write fails | `Runtime/Transport/RawSocketTransport.cs:1923` | C |  |
| [net-protocol-02](net.md) | protocol-impl | Default first-byte watchdog fails any idempotent request whose server needs more than 10 s to answer when the first attempt used a pooled connection | `Runtime/Transport/RawSocketTransport.cs:344` | C |  |
| [net-protocol-03](net.md) | security | Proxy selection and bypass are decided once in UHttpClient for the first URI; the transport reuses that decision for redirect targets and never evaluates BypassList itself | `Runtime/Transport/RawSocketTransport.cs:1310` | C |  |
| [net-protocol-04](net.md) | security | Request-level ProxySettings are never validated: an https:// (or any non-http) proxy address is silently contacted in cleartext, on port 80 when the URI has no explicit port | `Runtime/Transport/RawSocketTransport.cs:861` | C |  |
| [net-protocol-05](net.md) | protocol-impl | Happy Eyeballs does not start the next address when every in-flight attempt has already failed; it sleeps until the scheduled stagger time | `Runtime/Transport/Connection/HappyEyeballsConnector.cs:150` | C |  |
| [net-protocol-06](net.md) | rfc-compat | MaxConcurrentAttempts (default 2) stops the Happy Eyeballs race after two blackholed addresses; remaining addresses are never tried before the connect deadline | `Runtime/Transport/Connection/HappyEyeballsConnector.cs:140` | C |  |
| [net-protocol-07](net.md) | protocol-impl | Proxy paths lack the direct path's stale-connection hardening: no first-byte watchdog, and the one retry takes the next idle connection instead of a new socket | `Runtime/Transport/RawSocketTransport.cs:1121` | C |  |
| [net-runtime-01](net.md) | concurrency | PollSelectSocketChannel.Dispose joins its own poll thread when called from an I/O continuation: 500 ms stall and both pooled buffers abandoned on every non-keep-alive completion | `Runtime/Transport/Tcp/PollSelectSocketChannel.cs:535` | C |  |
| [net-runtime-03](net.md) | platform-compat | Token cancellation in the SAEA and PollSelect channels closes the socket directly, bypassing the shutdown-first order Dispose uses against the IL2CPP stale-selector-job hazard | `Runtime/Transport/Tcp/SaeaSocketChannel.cs:170` | P | #384 |
| [net-runtime-04](net.md) | platform-compat | Close order is chosen from a compile-time 'IOCP' label for Windows, but neither Mono nor IL2CPP uses IOCP for Socket I/O, so Windows IL2CPP players get close-first with no descriptor hold | `Runtime/Transport/Tcp/SaeaPlatformDetection.cs:72` | P |  |
| [net-runtime-07](net.md) | resource-management | Disposing the transport/pool neither cancels in-flight HTTP/1.1 exchanges nor wakes requests queued on a per-host permit | `Runtime/Transport/Tcp/TcpConnectionPool.cs:948` | C |  |
| [net-runtime-08](net.md) | performance | The idle pool is a FIFO queue: the connection that has been idle longest is reused first | `Runtime/Transport/Tcp/TcpConnectionPool.cs:508` | C |  |
| [net-runtime-10](net.md) | performance | SAEA mode still allocates per pending operation and copies every byte twice, so it does not deliver the allocation saving it is documented for | `Runtime/Transport/Tcp/SaeaSocketChannel.cs:149` | C |  |

### TLS (8)

| ID | Angle | Finding | Where | V | Issue |
| --- | --- | --- | --- | --- | --- |
| [tls-design-03](tls.md) | architecture | ITlsProvider is a public abstraction that third parties cannot plug in for HTTP: the selector is a closed enum with a hard-coded BouncyCastle slot and provider identity is a string compare | `Runtime/Transport/Tls/TlsProviderSelector.cs:54` | C |  |
| [tls-design-04](tls.md) | code-smell | TLS failures are classified by BCL exception type only: every non-certificate handshake failure becomes UHttpErrorType.CertificateError and the actual reason is only in a nested inner exception | `Runtime/Transport/BouncyCastle/BouncyCastleTlsProvider.cs:226` | C | #451 |
| [tls-protocol-01](tls.md) | security | BouncyCastle host-name check falls back to the subject CN, which on Android accepts certificates the platform verifier rejects | `Runtime/Transport/Tls/TlsCertificateNameMatcher.cs:64` | C |  |
| [tls-protocol-02](tls.md) | protocol-impl | A failed write of a reply record (close_notify answer, KeyUpdate) fails the BouncyCastle read that already holds decrypted data or a clean close | `Runtime/Transport/BouncyCastle/BouncyCastleTlsStream.cs:332` | P |  |
| [tls-runtime-01](tls.md) | concurrency | BouncyCastleTlsStream.Dispose sends close_notify with a blocking, unbounded synchronous write on the inner stream, which is sync-over-async on the SAEA and poll/select streams. | `Runtime/Transport/BouncyCastle/BouncyCastleTlsStream.cs:271` | C |  |
| [tls-runtime-04](tls.md) | platform-compat | The Windows crypt32 verifier disables root auto-update, so BouncyCastle rejects chains whose root Windows has not lazily installed yet, although SslStream/SChannel on the same machine accepts them. | `Runtime/Transport/Tls/WindowsCertificateVerifier.cs:39` | P |  |
| [tls-runtime-15](tls.md) | resource-management | An abandoned BouncyCastle handshake keeps its stream, engine and buffers alive until the pending inner read ends, which IL2CPP may never deliver. | `Runtime/Transport/BouncyCastle/BouncyCastleTlsProvider.cs:122` | P | #344 |
| [tls-runtime-16](tls.md) | concurrency | Concurrent BouncyCastle handshakes over SAEA run all handshake CPU work and certificate evaluation inline on the socket-completion pool workers, which is consistent with the clamped-pool stall still seen on 2021.3 IL2CPP iOS. | `Runtime/Transport/BouncyCastle/BouncyCastleTlsStream.cs:131` | P | #447 |

### HTTP/1.1 (9)

| ID | Angle | Finding | Where | V | Issue |
| --- | --- | --- | --- | --- | --- |
| [http1-design-02](http1.md) | best-practices | Serializer and parser signal failures with BCL exception types that three separate mappers and one message-string match classify, so caller errors become retryable NetworkError or Unknown | `Runtime/Transport/RawSocketTransport.cs:805` | C |  |
| [http1-design-04](http1.md) | architecture | Received header and trailer fields are validated by three unrelated routines (HTTP/1.1 header, HTTP/1.1 trailer, HTTP/2) that accept different inputs | `Runtime/Transport/Http1/Http11ResponseParser.cs:741` | C |  |
| [http1-design-06](http1.md) | architecture | RawSocketTransport (2,434 lines) owns the HTTP/1.1 exchange state machines, so they cannot be exercised without a pool and sockets, and the body decoder is welded to ConnectionLease | `Runtime/Transport/RawSocketTransport.cs:2018` | C |  |
| [http1-design-09](http1.md) | best-practices | HTTP/1.1 response limits are private constants with no option, and they disagree with the HTTP/2, CONNECT and WebSocket limits for the same thing | `Runtime/Transport/Http1/Http11ResponseParser.cs:156` | C |  |
| [http1-protocol-03](http1.md) | rfc-compat | A connection is returned to the pool after a request that carried 'Connection: close' whenever the response does not echo close | `Runtime/Transport/RawSocketTransport.cs:1832` | C |  |
| [http1-protocol-04](http1.md) | security | Bytes that follow a complete response in the reader buffer are silently discarded and the connection is pooled as clean | `Runtime/Transport/Http1/Http11ResponseBodySource.cs:660` | C |  |
| [http1-protocol-06](http1.md) | rfc-compat | Field values and framing tokens are trimmed with char.IsWhiteSpace, which removes the obs-text bytes 0x85/0xA0 and accepts VT/FF/CR as optional whitespace | `Runtime/Transport/Http1/Http11ResponseParser.cs:777` | C |  |
| [http1-protocol-07](http1.md) | security | Response header values containing NUL, bare CR or other control characters are delivered to the application unchanged | `Runtime/Transport/Http1/Http11ResponseParser.cs:722` | C |  |
| [http1-runtime-02](http1.md) | concurrency | DisposeAsync() during a pending read starts a second, concurrent drain read on the same BufferedStreamReader and resets the shared value-task source under the first reader | `Runtime/Transport/Http1/Http11ResponseBodySource.cs:279` | C |  |

### HTTP/2 (17)

| ID | Angle | Finding | Where | V | Issue |
| --- | --- | --- | --- | --- | --- |
| [http2-design-04](http2.md) | code-smell | Http2Options.MaxFrameSize and MaxConcurrentStreams are documented as advertised settings but are never sent as configured | `Runtime/Transport/Http2/Http2Settings.cs:102` | C |  |
| [http2-design-09](http2.md) | code-smell | Request content-length is transport-owned on HTTP/1.1 but caller-owned and never generated on HTTP/2 | `Runtime/Transport/Http2/Http2Connection.cs:379` | C |  |
| [http2-design-15](http2.md) | best-practices | Http2ConnectionManager.Dispose does not fence an in-flight connection init: the new connection is published into the disposed manager and leaked, and disposing the init semaphores breaks their holders and waiters | `Runtime/Transport/Http2/Http2ConnectionManager.cs:465` | C |  |
| [http2-design-16](http2.md) | code-smell | SendFrameWithPooledPayloadAsync swallows cancellation, so the caller's compensation is dead code and a cancelled body read loses a stream WINDOW_UPDATE for good | `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs:116` | C |  |
| [http2-design-17](http2.md) | code-smell | A transport-initiated stall abort surfaces to the body reader as ObjectDisposedException instead of a UHttpException | `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs:482` | C |  |
| [http2-protocol-02](http2.md) | protocol-impl | A connection that fails in the read loop or on a frame write is never closed: GOAWAY is sent but the transport stream stays open until a later request for the same key evicts it (or forever for a GOAWAY-drained connection) | `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs:107` | C |  |
| [http2-protocol-03](http2.md) | security | The HPACK encoder's dynamic table grows to whatever SETTINGS_HEADER_TABLE_SIZE the server sends (up to 2^31-1), with a linear scan per header | `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs:857` | C |  |
| [http2-protocol-04](http2.md) | rfc-compat | Response field blocks with a pseudo-header after a regular field, or with a repeated :status, are accepted instead of being treated as malformed | `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs:626` | C |  |
| [http2-runtime-02](http2.md) | resource-management | A request-body failure after the response started resets the stream but never faults the response body source, so its reader waits forever and its pooled buffers and connection buffered-byte count are never released. | `Runtime/Transport/Http2/Http2Connection.cs:487` | C |  |
| [http2-runtime-03](http2.md) | concurrency | Fail/Cancel from another thread can race TryStartResponse on the read loop: the dispatch completes with the failure while the handler still gets OnResponseStart with a body source that is never terminated. | `Runtime/Transport/Http2/Http2Stream.cs:254` | C |  |
| [http2-runtime-04](http2.md) | concurrency | A stream WINDOW_UPDATE is silently dropped when the reader's token is cancelled, while the local window credit is kept, so the peer's stream window shrinks permanently and the download can stall. | `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs:307` | C |  |
| [http2-runtime-07](http2.md) | performance | The body reader sends one stream WINDOW_UPDATE per consumed DATA frame and awaits that write before returning data; the allocation gate test bypasses this path. | `Runtime/Transport/Http2/Http2ResponseBodySource.cs:413` | C | #406 |
| [http2-runtime-08](http2.md) | performance | Every frame is written as separate header and payload stream writes, so each frame costs two TLS records, two send syscalls and, with TCP_NODELAY, two segments. | `Runtime/Transport/Http2/Http2FrameCodec.cs:221` | C | #406 |
| [http2-runtime-09](http2.md) | performance | Each DATA payload is copied from the leased frame buffer into a second pooled buffer, and each frame allocates a lease, a frame object and task objects, with no read buffering. | `Runtime/Transport/Http2/Http2ResponseBodySource.cs:296` | C | #406 |
| [http2-runtime-10](http2.md) | performance | InitializeAsync waits for the server's SETTINGS ACK before the first request may be sent, adding one round trip to every new HTTP/2 connection. | `Runtime/Transport/Http2/Http2Connection.cs:265` | C |  |
| [http2-runtime-12](http2.md) | platform-compat | HTTP/2 connections have no idle timeout and the reuse probe is off by default, so the first request after a silent NAT drop or app resume waits for the deadline or up to PingInterval + PingTimeout. | `Runtime/Transport/Http2/Http2ConnectionManager.cs:184` | P |  |
| [http2-runtime-14](http2.md) | concurrency | The read loop awaits _writeLock to write control frames, so a DATA write blocked by TCP back-pressure stops all inbound processing until the 60 s stall timer kills the connection. | `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs:101` | P |  |

### Core (9)

| ID | Angle | Finding | Where | V | Issue |
| --- | --- | --- | --- | --- | --- |
| [core-design-08](core.md) | architecture | Interceptor ordering is enforced for two pairs only and only for first-party types; Cache outside Redirect and Cache outside Cookie are accepted, and third-party interceptors cannot declare a role. | `Runtime/Core/Pipeline/InterceptorOrdering.cs:53` | C |  |
| [core-design-10](core.md) | pluggability | The plugin system (about 1,360 of Core's 10,700 lines) has no first-party consumer, is rooted in every build that keeps public API, and its capability checks cost several full passes over the request body per request without being a real boundary. | `Runtime/Core/PluginContext.cs:96` | C |  |
| [core-design-13](core.md) | architecture | IHttpTransport is public and advertised as replaceable, but a transport outside the friend assemblies cannot read stream or factory request bodies, cannot see the transport flags, and UHttpRequestBody cannot be subclassed. | `Runtime/Core/UHttpRequestBody.cs:100` | C | #76 |
| [core-protocol-02](core.md) | protocol-impl | The buffered collector discards HTTP/2 response trailers whenever the body source can be detached | `Runtime/Core/Pipeline/ResponseCollectorHandler.cs:208` | C |  |
| [core-runtime-02](core.md) | resource-management | A dropped UHttpStreamingResponse never aborts its body source: the HTTP/1.1 lease and per-host permit leak for the life of the transport, and release builds keep an empty finalizer | `Runtime/Core/UHttpStreamingResponse.cs:77` | C |  |
| [core-runtime-03](core.md) | resource-management | UHttpResponse's finalizer returns pooled body buffers to ArrayPool while a copied-out Body sequence can still reference them | `Runtime/Core/UHttpResponse.cs:280` | P |  |
| [core-runtime-04](core.md) | performance | The plugin capability guard hashes the whole buffered request body about six times per request and re-wraps the plugin interceptor on every request | `Runtime/Core/PluginContext.cs:150` | C |  |
| [core-runtime-05](core.md) | performance | UHttpRequest.Clone and CopyWithSharedContent deep-clone headers and metadata twice | `Runtime/Core/UHttpRequest.cs:144` | C |  |
| [core-runtime-06](core.md) | performance | HttpHeaders allocates a List<string> and array per header, grows its dictionary from zero, and boxes enumerators on every name walk | `Runtime/Core/HttpHeaders.cs:42` | C |  |

### Middleware and Auth (17)

| ID | Angle | Finding | Where | V | Issue |
| --- | --- | --- | --- | --- | --- |
| [mw-design-02](mw.md) | code-smell | RedirectInterceptor's defaultFollowRedirects and defaultMaxRedirects constructor arguments are never honoured for client-created requests, although the documentation shows them as the way to configure the limit | `Runtime/Middleware/RedirectInterceptor.cs:265` | C |  |
| [mw-design-11](mw.md) | architecture | AuthInterceptor has no target scope and overwrites an explicit per-request Authorization header: the provider's token goes to whatever origin each request addresses | `Runtime/Auth/AuthInterceptor.cs:118` | C |  |
| [mw-design-13](mw.md) | architecture | There is no way to react to a rejected token: AuthInterceptor ignores 401, IAuthTokenProvider has no invalidate hook, and OAuthClient.RefreshTokenAsync silently returns the stored token while it is not locally expired | `Runtime/Auth/OAuthClient.cs:171` | C |  |
| [mw-design-20](mw.md) | architecture | CookieJar is a sealed concrete type with no way to clear, remove, enumerate or persist cookies, so sessions cannot be ended locally and persistent cookies never outlive the process | `Runtime/Middleware/CookieJar.cs:21` | C | #121 |
| [mw-protocol-04](mw.md) | security | Redirect URLs reach exception messages and timeline events unredacted (query values, user-info), bypassing the default RedactionPolicy. | `Runtime/Middleware/RedirectHandler.cs:186` | C | #117 |
| [mw-protocol-05](mw.md) | security | CookieJar stores cookie names and values containing control characters, which makes every later request to that cookie's scope fail at header validation. | `Runtime/Middleware/CookieJar.cs:382` | C |  |
| [mw-protocol-06](mw.md) | security | A trailing dot defeats the public-suffix and single-label checks: `Domain=com.` set by `evil.com.` is stored and sent to every `*.com.` host. | `Runtime/Middleware/CookieJar.cs:827` | C |  |
| [mw-protocol-07](mw.md) | rfc-compat | CookieJar strips surrounding double quotes from cookie values, so the server gets back a different value than it set (and a test asserts this). | `Runtime/Middleware/CookieJar.cs:383` | C |  |
| [mw-protocol-08](mw.md) | rfc-compat | Expires is parsed with DateTime.TryParse instead of the RFC 6265 cookie-date algorithm, so common deletion dates are ignored and non-cookie formats are accepted. | `Runtime/Middleware/CookieJar.cs:476` | C |  |
| [mw-protocol-09](mw.md) | rfc-compat | Only the first Content-Encoding field line is honoured; further lines are ignored but removed, so the body is forwarded half-decoded as identity or decoded in the wrong order. | `Runtime/Middleware/DecompressionHandler.cs:735` | C |  |
| [mw-protocol-12](mw.md) | rfc-compat | DecompressionInterceptor adds Accept-Encoding to Range requests and DecompressionHandler tries to inflate 206 partial bodies, which are byte ranges of the coded representation. | `Runtime/Middleware/DecompressionInterceptor.cs:68` | C |  |
| [mw-protocol-13](mw.md) | rfc-compat | OAuthClient sends redirect_uri as Uri.ToString(), which appends a slash and unescapes, so it no longer equals the registered redirect URI. | `Runtime/Auth/OAuthClient.cs:67` | C |  |
| [mw-runtime-03](mw.md) | performance | Every compressed response from the real transport is decoded through DeflateStream's async-over-sync path on Unity: one thread-pool dispatch per read and one pool thread parked in GetResult() for as long as the network read is pending. | `Runtime/Middleware/InflateLayerStream.cs:228` | C |  |
| [mw-runtime-04](mw.md) | platform-compat | On Mono/IL2CPP the inflater's Read keeps pulling input until the destination is full or the stream ends, so a streamed compressed body is delivered in destination-sized quanta instead of as data arrives. | `Runtime/Middleware/InflateLayerStream.cs:628` | C |  |
| [mw-runtime-05](mw.md) | resource-management | DecompressionBodySource does not delegate DrainAsync/DisposeAsync to the wrapped source: it inflates the rest of the body for up to 2 s, replacing the transport's bounded drain (HTTP/1.1) and immediate RST_STREAM (HTTP/2). | `Runtime/Middleware/DecompressionHandler.cs:393` | C |  |
| [mw-runtime-06](mw.md) | concurrency | OAuthClient's refresh gate serializes failures instead of sharing them: after a failed refresh every waiter repeats the token request one by one, and the wait is not covered by the request timeout. | `Runtime/Auth/OAuthClient.cs:166` | C |  |
| [mw-runtime-07](mw.md) | performance | Each compressed response allocates about 16 KB of unpooled buffers plus a redundant BufferedStream copy layer, and the production transport never reaches the pooled, copy-free buffered decode path. | `Runtime/Middleware/DecompressionHandler.cs:269` | C |  |

### Cache, Retry, RateLimit (15)

| ID | Angle | Finding | Where | V | Issue |
| --- | --- | --- | --- | --- | --- |
| [cache-design-03](cache.md) | architecture | MemoryCacheStorage's expiry sweep deletes stale entries that still have validators, so conditional revalidation only happens when no other store ran in between | `Runtime/Cache/MemoryCacheStorage.cs:92` | C |  |
| [cache-design-04](cache.md) | pluggability | The Vary variant index lives only in the interceptor and cannot be rebuilt from ICacheStorage, so a persistent or shared custom store loses every Vary response and can serve an invalidated variant | `Runtime/Cache/CacheInterceptor.cs:105` | C |  |
| [cache-design-06](cache.md) | best-practices | Storage exceptions on the foreground paths (lookup, 304 merge, post-revalidation removal, unsafe-method invalidation) fail the HTTP request, while background stores are best-effort | `Runtime/Cache/CacheInterceptor.StreamingDispatch.cs:143` | C |  |
| [cache-design-11](cache.md) | architecture | Stale-while-revalidate starts one background revalidation per stale hit with no per-key de-duplication or bound | `Runtime/Cache/CacheInterceptor.cs:352` | C |  |
| [cache-protocol-02](cache.md) | rfc-compat | With DoNotCacheWithoutFreshness=false and heuristic freshness off, responses without any freshness information are stored with ExpiresAtUtc=null and served as fresh forever | `Runtime/Cache/CacheInterceptor.cs:631` | C |  |
| [cache-protocol-03](cache.md) | rfc-compat | must-revalidate is treated like no-cache: a response with `max-age=N, must-revalidate` is never served fresh and is revalidated (or dropped) on every request | `Runtime/Cache/CacheInterceptor.cs:629` | C |  |
| [cache-protocol-05](cache.md) | rfc-compat | Large delta-seconds values throw OverflowException from TimeSpan.FromSeconds instead of being clamped; a 304 carrying one fails the caller's request | `Runtime/Cache/CacheInterceptor.Parsing.cs:99` | C |  |
| [cache-protocol-06](cache.md) | protocol-impl | With CacheInterceptor outside RedirectInterceptor (an accepted order), the final response of a redirect chain is stored under the original URL's key | `Runtime/Cache/CacheStoringHandler.cs:40` | C |  |
| [cache-protocol-07](cache.md) | security | Storability and Vary decisions use the request as it looked at the cache's pipeline position, so Authorization/Cookie added by any inner interceptor other than AuthInterceptor bypasses the authorized-request and Vary guards | `Runtime/Cache/CacheStoringHandler.cs:41` | C |  |
| [cache-protocol-08](cache.md) | protocol-impl | TeeBodySource can commit a truncated or empty body: a 0-byte result is taken as natural EOF even for an empty destination or after the inner source drained itself in GetTrailersAsync | `Runtime/Cache/CacheStoringHandler.cs:246` | C |  |
| [cache-protocol-09](cache.md) | security | The variant index bounds the number of base keys but not the storage keys per base key: evicted Vary variants accumulate in a bucket without limit | `Runtime/Cache/CacheInterceptor.Variants.cs:187` | C |  |
| [cache-protocol-10](cache.md) | security | Cache timeline events carry the full cache key: URL with query values and, for CacheRevalidate, the Vary header values; the Monitor copies timeline data without redaction | `Runtime/Cache/CacheInterceptor.cs:425` | C | #117 |
| [cache-protocol-12](cache.md) | protocol-impl | ConcurrencyLimiter takes the global permit before waiting for the per-host permit, so requests queued for one saturated host hold global permits and block every other host | `Runtime/RateLimit/ConcurrencyLimiter.cs:96` | C |  |
| [cache-runtime-01](cache.md) | performance | The cache copies every candidate response body (tee or buffered snapshot, then a flat byte[]) before the header-only storability decision, so non-storable responses pay the full copy cost | `Runtime/Cache/CacheInterceptor.cs:940` | C |  |
| [cache-runtime-02](cache.md) | concurrency | TeeBodySource.Abort and DisposeAsync return the accumulator's pooled segments while a concurrent ReadAsync may still be copying into them | `Runtime/Cache/CacheStoringHandler.cs:281` | C |  |

### Utility modules (12)

| ID | Angle | Finding | Where | V | Issue |
| --- | --- | --- | --- | --- | --- |
| [util-design-01](util.md) | pluggability | ObservabilityModuleRegistration is a [Preserve]d RuntimeInitializeOnLoadMethod compiled for players, so the Editor-only HTTP Monitor is rooted in every player build although UHttpClient only consults the hook under UNITY_EDITOR. | `Runtime/Observability/ObservabilityModuleRegistration.cs:23` | C |  |
| [util-design-03](util.md) | architecture | The JSON bridge forwards library-internal DOM parsing to the user-replaceable JsonSerializer.Default, so RecordReplayTransport (and OAuth token parsing) silently break when the project registers a Newtonsoft/System.Text.Json adapter. | `Runtime/JSON/JsonModuleRegistration.cs:34` | C |  |
| [util-design-04](util.md) | architecture | TurboHttpSettings overwrites MonitorInterceptor's public static configuration on every domain reload and play-mode transition, so a RedactionPolicy (or capacity) set through the documented public API is silently replaced by Default/None. | `Editor/Settings/TurboHttpSettings.cs:143` | C |  |
| [util-design-05](util.md) | code-smell | The Monitor window's header masking and Export redact with the static RedactionPolicy.Default instead of the active MonitorInterceptor.RedactionPolicy, and Export writes captured bodies unredacted in both text and base64. | `Editor/Monitor/HttpMonitorWindow.Export.cs:133` | C | #117 |
| [util-design-06](util.md) | best-practices | DownloadOptions needs two settings to verify a checksum: ExpectedSha256/ExpectedMd5 are ignored unless VerifyChecksum is also true, with no error. | `Runtime/Files/FileDownloader.cs:35` | C |  |
| [util-protocol-01](util.md) | security | Monitor timeline data and error text bypass RedactionPolicy: redirect and cache events put full URLs with raw query strings into the Monitor, its Export and the log | `Runtime/Observability/MonitorInterceptor.cs:661` | C | #117 |
| [util-protocol-02](util.md) | security | URL-valued header fields (Location, Content-Location, Referer, Link) are never query-redacted in logs, Monitor captures, Monitor exports or recordings | `Runtime/Observability/RedactionPolicy.cs:134` | C |  |
| [util-protocol-03](util.md) | security | Monitor captures, displays and exports request and response bodies with no redaction | `Runtime/Observability/MonitorInterceptor.cs:349` | C | #117 |
| [util-protocol-04](util.md) | security | RecordReplay stores an HMAC of the unredacted request body together with its key, so a redacted low-entropy secret can be guessed offline from the recording | `Runtime/Testing/RecordReplayTransport.Utilities.cs:69` | C |  |
| [util-protocol-05](util.md) | protocol-impl | RecordReplay body redaction ignores Content-Encoding: a gzip/deflate JSON or form body is silently recorded as the placeholder and the recording cannot be replayed | `Runtime/Testing/RecordReplayTransport.Redaction.cs:81` | C |  |
| [util-runtime-07](util.md) | resource-management | MultipartFormDataBuilder holds every file fully in memory and then copies it again into a growing pooled writer that is capped at 64 MiB, so large uploads cost about three times the file size or fail outright. | `Runtime/Files/MultipartFormDataBuilder.cs:171` | C |  |
| [util-runtime-09](util.md) | resource-management | RecordReplayTransport can write recordings that it cannot load: Replay parses the whole file through LiteJson, which rejects input above 16 MiB, while Record accepts 16 MB per response and any number of responses. | `Runtime/Testing/RecordReplayTransport.cs:593` | C |  |

### WebSocket (22)

| ID | Angle | Finding | Where | V | Issue |
| --- | --- | --- | --- | --- | --- |
| [ws-design-04](ws.md) | architecture | WebSocketClient.Create(options) inspects only ReconnectPolicy and then discards the options, so the same options must be passed again to ConnectAsync and a mismatch silently changes behaviour | `Runtime/WebSocket/WebSocketClient.cs:46` | C |  |
| [ws-design-05](ws.md) | code-smell | Connect-phase and protocol failures are collapsed into ConnectionClosed/ProtocolViolation, so timeouts, DNS, TLS certificate failures and usage errors are all reported as retryable network errors, unlike the HTTP client | `Runtime/WebSocket/WebSocketConnection.cs:354` | C |  |
| [ws-design-06](ws.md) | best-practices | Sending on a closed or reconnecting client throws InvalidOperationException from a client-level pre-check, contradicting the connection's WebSocketException contract; a test pins it | `Runtime/WebSocket/WebSocketClient.cs:569` | C |  |
| [ws-design-07](ws.md) | architecture | WebSocketProxySettings re-implements proxy configuration and bypass matching that Core already exposes, with different rule semantics than the HTTP client | `Runtime/WebSocket/WebSocketProxySettings.cs:82` | C |  |
| [ws-design-09](ws.md) | architecture | WebSocketHandshakeValidator is a third hand-written HTTP/1.1 response-head and body parser whose acceptance rules differ from the transport's parser | `Runtime/WebSocket/WebSocketHandshakeValidator.cs:358` | C |  |
| [ws-design-10](ws.md) | code-smell | RawSocketWebSocketTransport's tlsBackend constructor argument (and the factory's TlsBackend parameter) is never honoured | `Runtime/WebSocket.Transport/RawSocketWebSocketTransport.cs:289` | C |  |
| [ws-design-12](ws.md) | best-practices | WithCompression captures MaxMessageSize at call time, so the decompression limit silently diverges from the connection limit when options are set afterwards | `Runtime/WebSocket/WebSocketConnectionOptions.cs:164` | C |  |
| [ws-design-13](ws.md) | architecture | Client layering by wrapping stacks three bounded receive queues and three pump tasks for one socket | `Runtime/WebSocket/ResilientWebSocketClient.cs:115` | C |  |
| [ws-design-15](ws.md) | code-smell | IdleTimeout is only evaluated inside the keep-alive loop, so it is silently ignored when PingInterval is zero | `Runtime/WebSocket/WebSocketConnection.cs:300` | C |  |
| [ws-design-17](ws.md) | architecture | About twenty internal plumbing types and two exception types outside the UHttpException hierarchy are public and will be frozen by the 1.0 API | `Runtime/WebSocket/WebSocketFrameReader.cs:15` | C |  |
| [ws-design-19](ws.md) | best-practices | The typed-message API forces a UTF-8 -> string -> UTF-8 round trip per text message and its serializer contract is loosely typed and bound to a global JSON serializer | `Runtime/WebSocket/WebSocketClientExtensions.cs:24` | C |  |
| [ws-design-22](ws.md) | code-smell | WebSocketClient and ResilientWebSocketClient duplicate their receive plumbing and the copies have diverged (NullReferenceException before connect, close code dropped) | `Runtime/WebSocket/ResilientWebSocketClient.cs:212` | C |  |
| [ws-protocol-03](ws.md) | protocol-impl | A data write stalled by TCP backpressure holds the frame lock with no deadline, which starves keep-alive pings, Pong replies and the protocol-error Close. | `Runtime/WebSocket/WebSocketConnection.SendReceive.cs:728` | C |  |
| [ws-protocol-04](ws.md) | protocol-impl | The default MaxFragmentCount of 64 rejects messages far below MaxMessageSize from servers that fragment at small frame sizes. | `Runtime/WebSocket/WebSocketConstants.cs:26` | C |  |
| [ws-protocol-05](ws.md) | protocol-impl | Keep-alive counts only Pong frames as liveness, so a Ping sent while a large inbound frame is still arriving times out and aborts a healthy connection. | `Runtime/WebSocket/WebSocketConnection.SendReceive.cs:1000` | C |  |
| [ws-runtime-03](ws.md) | resource-management | Abort or Dispose during WebSocketConnection.ConnectAsync neither cancels the transport connect nor disposes the stream that arrives afterwards. | `Runtime/WebSocket/WebSocketConnection.cs:193` | C |  |
| [ws-runtime-05](ws.md) | concurrency | ResilientWebSocketClient.ConnectAsync resets the manual-close flag and the queue and disposes (without cancelling) the old lifecycle CTS while the previous pump or reconnect loop can still be running. | `Runtime/WebSocket/ResilientWebSocketClient.cs:116` | P |  |
| [ws-runtime-06](ws.md) | concurrency | The reconnect loop invokes OnError, OnReconnecting and OnReconnected unguarded inside an unobserved task: a throwing handler either kills reconnection silently or makes the loop tear down each new connection. | `Runtime/WebSocket/ResilientWebSocketClient.cs:561` | C |  |
| [ws-runtime-09](ws.md) | performance | The frame reader has no receive buffer: every frame costs two or three separate stream reads, each a full async round trip. | `Runtime/WebSocket/WebSocketFrameReader.cs:88` | C |  |
| [ws-runtime-11](ws.md) | performance | Frames larger than 8 KB are written as a separate header write plus one stream write per 8 KB chunk. | `Runtime/WebSocket/WebSocketFrameWriter.cs:300` | C |  |
| [ws-runtime-12](ws.md) | performance | permessage-deflate creates a new DeflateStream and payload-sized MemoryStream arrays for every message in both directions and copies the body three to four times. | `Runtime/WebSocket/PerMessageDeflateExtension.cs:252` | C |  |
| [ws-runtime-13](ws.md) | performance | With an OnMessage subscriber every message is copied into a fresh byte[] while the pooled original is parked in a pull queue that nobody reads. | `Runtime/WebSocket/WebSocketClient.cs:429` | C |  |

### Unity integration (27)

| ID | Angle | Finding | Where | V | Issue |
| --- | --- | --- | --- | --- | --- |
| [unity-design-01](unity.md) | pluggability | Runtime/Unity/link.xml preserves all ten decoder types (including the three always-throwing stubs) although nothing reaches them by reflection, and thereby roots TurboHTTP.Unity in every player build | `Runtime/Unity/link.xml:2` | C |  |
| [unity-design-02](unity.md) | pluggability | TurboHTTP.Unity runs eleven startup hooks unconditionally: a DontDestroyOnLoad GameObject, a PlayerLoop system, lowMemory subscriptions and a temp-file sweep are installed in every player, with no way to take one feature without the others | `Runtime/Unity/MainThreadDispatcher.cs:242` | C |  |
| [unity-design-09](unity.md) | architecture | Low-memory shedding is reported as cancellation, so WebSocket connected/closed callbacks are dropped despite the documented never-dropped guarantee and texture loads look user-cancelled | `Runtime/Unity/MainThreadDispatcher.cs:1000` | C |  |
| [unity-design-10](unity.md) | performance | The dispatcher clones its settings object every frame and on every ExecuteAsync call | `Runtime/Unity/MainThreadDispatcher.cs:932` | C |  |
| [unity-design-11](unity.md) | best-practices | AudioClipHandler.Configure and UnityTempFileManager.Configure dispose and replace semaphores that in-flight operations still hold, and the temp-file manager releases a different semaphore than it acquired | `Runtime/Unity/UnityTempFileManager.cs:229` | C |  |
| [unity-design-15](unity.md) | architecture | UnityExtensions.DownloadTo*Async and PathSafety re-implement TurboHTTP.Files.FileDownloader with diverged behaviour: the whole body is buffered in memory and capped at 100 MB | `Runtime/Unity/UnityExtensions.cs:127` | C |  |
| [unity-design-16](unity.md) | architecture | UHttpClient.WebSocket(...) copies three client options and silently ignores the rest, including the proxy; it also leaks the client on a failed connect and hides the bridge type | `Runtime/Unity.WebSocket/UnityWebSocketExtensions.cs:44` | C |  |
| [unity-design-17](unity.md) | architecture | Background replay is half-implemented: expired requests are queued with no consumer, the only drain API is undocumented, and the interceptor never disposes what it queued | `Runtime/Unity/Mobile/BackgroundNetworkingInterceptor.cs:51` | C |  |
| [unity-design-22](unity.md) | code-smell | The adaptive detector counts every non-2xx HTTP response as a network failure and compares proportion-style thresholds with an alpha-0.5 EWMA, so one 404 or one timeout reclassifies the network | `Runtime/Unity/Mobile/AdaptiveHandler.cs:100` | C |  |
| [unity-protocol-03](unity.md) | protocol-impl | A request cancelled by background expiry that is not queued surfaces as a bare OperationCanceledException although the caller never cancelled, and CoroutineWrapper then invokes no callback at all | `Runtime/Unity/Mobile/BackgroundNetworkingInterceptor.cs:111` | C |  |
| [unity-protocol-06](unity.md) | security | Texture decoding has no default bound on source bytes or decoded pixels, although the documentation says TextureOptions protects against hostile images | `Runtime/Unity/Texture2DHandler.cs:522` | P |  |
| [unity-protocol-07](unity.md) | security | The MaxPixels pre-decode guard fails open when it cannot parse the header and its JPEG scanner can be made to miss the frame header, so the limit is enforced only after LoadImage has allocated the image | `Runtime/Unity/Texture2DHandler.cs:500` | P |  |
| [unity-protocol-08](unity.md) | security | The audio pipeline has no bound on source size or decoded size: every clip below the 2 MB streaming threshold is fully decompressed in memory | `Runtime/Unity/AudioClipHandler.cs:350` | P |  |
| [unity-protocol-10](unity.md) | protocol-impl | Adaptive networking clamps every non-explicit timeout to [2 s, 30 s], so with the library defaults it can only shorten timeouts, and an infinite default timeout becomes 2 seconds | `Runtime/Unity/Mobile/AdaptiveInterceptor.cs:55` | C |  |
| [unity-protocol-11](unity.md) | protocol-impl | CoroutineWrapper silently swallows an exception thrown by the onSuccess callback | `Runtime/Unity/CoroutineWrapper.cs:223` | C |  |
| [unity-protocol-14](unity.md) | security | client.WebSocket() copies the HTTP client's default headers (including credentials) into the handshake but drops the client's proxy settings, neither of which matches the documented inheritance | `Runtime/Unity.WebSocket/UnityWebSocketExtensions.cs:51` | C |  |
| [unity-protocol-16](unity.md) | protocol-impl | AiffPcmDecoder accepts AIFF-C files without reading the compression type and decodes little-endian, float or compressed sample data as big-endian PCM | `Runtime/Unity/Decoders/AiffPcmDecoder.cs:59` | C |  |
| [unity-runtime-02](unity.md) | performance | Each queued main-thread dispatch allocates about 15 objects, and UnityWebSocketBridge pays that for every received message. | `Runtime/Unity/MainThreadDispatcher.cs:500` | C |  |
| [unity-runtime-03](unity.md) | platform-compat | If another package replaces the PlayerLoop after the dispatcher installed its hook, queued work is never drained because the Update fallback is disabled by a stale flag. | `Runtime/Unity/MainThreadDispatcher.cs:290` | C |  |
| [unity-runtime-04](unity.md) | resource-management | Destroying the dispatcher GameObject leaves MainThreadDispatcher permanently rejecting work, including main-thread calls that need no dispatch. | `Runtime/Unity/MainThreadDispatcher.cs:306` | C |  |
| [unity-runtime-06](unity.md) | resource-management | CoroutineWrapper cleans up only in an iterator finally, so a stopped coroutine neither cancels the request nor disposes the response, and suppressed completions leak the response. | `Runtime/Unity/CoroutineWrapper.cs:226` | C |  |
| [unity-runtime-09](unity.md) | performance | GetTextureAsync copies the whole encoded body (twice when MaxPixels is set) and keeps the pooled original alive until the decode finishes. | `Runtime/Unity/Texture2DHandler.cs:499` | C |  |
| [unity-runtime-14](unity.md) | performance | The 'threaded' managed audio decode runs synchronously on the calling thread, which is the Unity main thread when response.AsAudioClipAsync is awaited from a MonoBehaviour. | `Runtime/Unity/Decoders/WavPcmDecoder.cs:40` | C |  |
| [unity-runtime-15](unity.md) | performance | Audio and download-to-file helpers flatten the buffered body into one array, and the file helpers buffer the whole download in memory first. | `Runtime/Unity/AudioClipHandler.cs:571` | C |  |
| [unity-runtime-19](unity.md) | performance | With Android background networking enabled every request makes three blocking hand-offs to the single JNI thread to add and remove one entry of a Java map that nothing reads. | `Runtime/Unity/Mobile/Android/AndroidBackgroundWorkBridge.cs:99` | C |  |
| [unity-runtime-20](unity.md) | platform-compat | The WebSocket UniTask adapters use Task.AsUniTask(), which needs a current SynchronizationContext and fails on worker threads; the HTTP adapters avoid it for that stated reason. | `Runtime/UniTask.WebSocket/WebSocketUniTaskExtensions.cs:26` | C |  |
| [unity-runtime-26](unity.md) | performance | JsonUtilitySerializer parses a top-level array three times and copies JSON bytes an extra time in both directions. | `Runtime/Bootstrap/JsonUtilitySerializer.cs:298` | C |  |

### Packaging and build size (7)

| ID | Angle | Finding | Where | V | Issue |
| --- | --- | --- | --- | --- | --- |
| [pkg-pluggability-01](pkg.md) | pluggability | The linker processor treats 'the module's DLL was compiled' as 'the module is in the build', so link.xml rules that name TurboHTTP types root optional modules (Unity, JSON, WebSocket.Transport, Transport) in every stripped player; 32 of the 44 rules protect code that nothing reaches by reflection. | `Editor/TurboHttpLinkerProcessor.cs:149` | P |  |
| [pkg-pluggability-02](pkg.md) | pluggability | TurboHTTP.Complete cannot be excluded from a package install, so the documented lean configuration is unreachable and untested; its bootstrap statically pulls the WebSocket transport, JSON and JsonUtility into every player, and 10 of its 15 asmdef references (including TurboHTTP.Testing) are unused. | `Runtime/TurboHTTP.Complete.asmdef:27` | C |  |
| [pkg-pluggability-03](pkg.md) | architecture | The Android cleartext platform policy is installed by a private startup hook in the optional TurboHTTP.Unity assembly, which has no AlwaysLinkAssembly; it survives stripping only because the unrelated decoder link.xml happens to root that assembly, and there is no public call to install it by hand. | `Runtime/Unity/Mobile/Android/AndroidCleartextTrafficPolicy.cs:28` | P |  |
| [pkg-pluggability-04](pkg.md) | pluggability | The vendored BouncyCastle assembly (TurboHTTP.SecureProtocol, 681 files, 138,572 lines, 891 types) has no define constraint, so it compiles in every project and ships in every unstripped Mono player with TURBOHTTP_BOUNCYCASTLE off; the changelog claims it is not linked, and CI never builds the define-off configuration. | `Runtime/Transport/BouncyCastle/Lib/TurboHTTP.SecureProtocol.asmdef:13` | C | #258 |
| [pkg-pluggability-05](pkg.md) | pluggability | The HTTP/2 stack (22 files, 8,358 lines, 38% of TurboHTTP.Transport) is statically referenced by RawSocketTransport and linked into every player, although without the opt-in BouncyCastle provider no Unity runtime can negotiate h2. | `Runtime/Transport/RawSocketTransport.cs:74` | C |  |
| [pkg-pluggability-06](pkg.md) | platform-compat | package.json declares no dependencies although TurboHTTP.Unity and TurboHTTP.Complete need five built-in engine modules; a project that disabled one of them gets compile errors inside the immutable package. | `package.json:31` | C |  |
| [pkg-pluggability-07](pkg.md) | platform-compat | The WebGL build guard fails every WebGL player build of a project that merely has the package installed, with no opt-out, while the runtime code and docs still describe a WebGL path with manual registration. | `Editor/TurboHttpWebGlBuildGuard.cs:31` | C |  |

### Documentation and samples (5)

| ID | Angle | Finding | Where | V | Issue |
| --- | --- | --- | --- | --- | --- |
| [docs-accuracy-02](docs.md) | docs | README and CHANGELOG tell users to install tag v1.0.0-pre.1, but the public repository has no tags and no releases | `README.md:34` | C | #101 |
| [docs-accuracy-03](docs.md) | docs | The documented IHttpPlugin example declares PluginCapabilities.None and then calls RegisterInterceptor, which throws for a plugin without interceptor capabilities | `Documentation~/APIReference.md:350` | C |  |
| [docs-accuracy-05](docs.md) | best-practices | client.WebSocket(url) returns an already connected bridge, and messages that arrive before the caller subscribes OnMessage are disposed; the documented snippet subscribes after the await | `Documentation~/Modules/WebSocket.md:27` | C |  |
| [docs-accuracy-07](docs.md) | security | AuthInterceptor attaches the token to every origin a client is used for; the Authentication sample says it is never sent to another host and no doc warns about third-party URLs | `Samples~/04-Authentication/AuthenticationExample.cs:12` | C |  |
| [docs-accuracy-08](docs.md) | docs | Docs say the request timeout 'covers each request', but a streaming response body has no deadline, and neither the docs nor the FileDownload sample tell the caller to bound body reads | `Documentation~/Modules/Core.md:130` | C |  |

### Test suite (8)

| ID | Angle | Finding | Where | V | Issue |
| --- | --- | --- | --- | --- | --- |
| [tests-quality-01](tests.md) | tests | PR CI executes none of the ~2,990 NUnit test cases: the required check only compiles them, so a behaviour regression outside 15 scripted HTTP/2 rows and the TLS/H2 interop rows merges green. | `.github/workflows/ci.yml:148` | C |  |
| [tests-quality-02](tests.md) | tests | Three HTTP/2 negative tests cannot fail: their only assertion sits inside try { ... } catch (Exception) { } which swallows NUnit's AssertionException. | `Tests/Runtime/Transport/Http2/Http2ConnectionTests.FrameHandling.cs:452` | C |  |
| [tests-quality-05](tests.md) | tests | Tests use fixed wall-clock sleeps as synchronisation and then assert a state, against the determinism rule; they fail on a slow player and pass vacuously for the negative direction. | `Tests/Runtime/Transport/Http2/Http2FlowControlTests.cs:213` | C |  |
| [tests-quality-12](tests.md) | tests | Test code behind TURBOHTTP_INTEGRATION_TESTS (9 blocks in 8 files) and TURBOHTTP_USE_SYSTEM_TEXT_JSON is compiled by no CI job; it compiles only in the host-editor ExternalNetwork run, where one error hides every result. | `Tests/Runtime/Transport/Tls/TlsIntegrationTests.cs:1` | C |  |
| [tests-quality-13](tests.md) | platform-compat | PublicChain_ProductionConfiguration_IsTrustedByPlatformRootStore runs ungated on Android players, where the verifier ignores the fixed verification time and is registered only by an earlier BouncyCastle handshake: the committed leaf expires on 2026-11-26. | `Tests/Runtime/Transport/Tls/ServerCertificateVerifierTests.cs:191` | C |  |
| [tests-quality-18](tests.md) | tests | Runtime types that own pooled buffers, parse peer-controlled binary or decide trust have no direct test. | `Tests/Runtime/Unity/AudioPipelineV2Tests.cs:41` | C |  |
| [tests-quality-19](tests.md) | tests | On Unity's own runtimes the full HTTP/2 path over a real socket and TLS (ALPN, preface, flow control on SslStream/BouncyCastle records) is exercised by one ExternalNetwork test, which also accepts an HTTP/1.1 fallback. | `Tests/Runtime/Integration/IntegrationTests.cs:603` | C |  |
| [tests-quality-21](tests.md) | tests | A filtered Unity run that matches no test exits 0, and no gate checks the total number of executed tests, so a scoped verification or a fixture that silently disappeared reads as a pass. | `Tools~/unity/summarize_results.py:72` | C |  |
