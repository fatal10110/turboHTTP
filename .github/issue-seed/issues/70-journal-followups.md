---
key: JRN-01
title: [Transport] Hard-coded 100 MB response cap also applies to streaming responses
milestone: v1.0-beta: Protocol & correctness hardening
labels: type:bug, area:http1, area:http2, area:transport, priority:P1, source:review-2026-10
---
## Summary
`MaxResponseBodySize` (100 MB) was introduced to bound *buffered* bodies, but it is enforced on every path, including `SendStreamingAsync`. Large streamed downloads (FileDownloader, media, SSE that runs long) fail at 100 MB even though nothing is buffered.

## Location
- `Runtime/Transport/Http1/Http11ResponseParser.cs:156` — `private const int MaxResponseBodySize = 100 MB`; `:495` rejects a `Content-Length` above it during head parsing (also reached by streaming callers); `:772-776` chunked totals
- `Runtime/Transport/Http1/Http11ResponseBodySource.cs` — same cap on streamed reads
- `Runtime/Transport/Http2/Http2Settings.cs:25`, `Http2Connection.ReadLoop.cs:147-149` — per-stream cap on received DATA

## Problem
A 500 MB `GET` through `FileDownloader` (streaming, written to disk chunk by chunk) fails with a size error. The cap is not configurable on HTTP/1.1.

## Suggested fix
- Apply the cap only where bytes are accumulated in memory (buffered collection, cache snapshot, decompression buffers).
- Make it configurable (`UHttpClientOptions`/`StreamingOptions`); streaming has no cap by default or an explicit opt-in limit.

## Acceptance criteria
- [ ] Loopback tests stream > 100 MB over Content-Length, chunked, read-to-end (HTTP/1.1) and HTTP/2 without error.
- [ ] Buffered requests still fail above the configured cap.
- [ ] Option documented.

## Context
Source: implementation journal `2026-03-phase22a.2-review-fixes.md` (cap extended to read-to-end bodies), found by the 2026-10 journal audit.

===ISSUE===
---
key: JRN-02
title: [Core] Pooled UHttpRequest lifecycle: negative hold-count branch skips pool return; no lifecycle tests
milestone: v1.0-beta: Protocol & correctness hardening
labels: type:bug, area:core, priority:P2, source:review-2026-10
---
## Summary
The Phase 19b review-fix journal says `TryReturnToPool()` is always called with `_disposeRequested` as the gate. The code returns early instead when the hold count goes negative, and none of the pooled-request lifecycle cases listed as open in that journal have tests.

## Location
- `Runtime/Core/UHttpRequest.cs:473-485` — `ReleaseResponseHold`: `if (remaining < 0) { Interlocked.Exchange(ref _responseHoldCount, 0); return; }` (no `TryReturnToPool`)
- `Runtime/Core/UHttpClient.cs:347`, `:411` — cross-client guard for pooled requests (untested)

## Problem
A double release (e.g. double `Dispose` of a response) leaves the request leased forever (pool slot leak) instead of returning it once. Regressions in the `_sendInProgress` / `_responseHoldCount` / `_disposeRequested` state machine are not caught.

## Suggested fix
Decide the intended behavior (likely: clamp and still call `TryReturnToPool`, which is idempotent) and document it at the branch. Add the tests. Coordinate with the lease-generation fix in [[MOD-13]].

## Acceptance criteria
- [ ] Double `Dispose` of a pooled response neither leaks nor double-returns the request.
- [ ] Sending a pooled request on a different client throws `InvalidOperationException`.
- [ ] The slot is returned exactly once across send/response/dispose orderings (test enumerates them).

## Context
Source: `2026-02-phase19b-review-fixes.md` (Bug 1, Bug 5, open test items), 2026-10 journal audit.

===ISSUE===
---
key: JRN-03
title: [Testing] RecordReplayTransport records empty bodies for streaming responses
milestone: v1.0-beta: Protocol & correctness hardening
labels: type:bug, area:testing, priority:P2, source:review-2026-10
---
## Summary
In record mode the recording handler only captures `IResponseBodySource.TryGetBufferedData`. Streaming HTTP/1.1 sources never expose buffered data, so recordings silently contain an empty body and replays return empty responses.

## Location
- `Runtime/Testing/RecordReplayTransport.cs:671` — `RecordingHandler` body capture
- `Runtime/Transport/Http1/Http11ResponseBodySource.cs:96-100` — `TryGetBufferedData` always false

## Suggested fix
Tee or drain the body source while recording (bounded by a max-record size), or fail the recording loudly. Keep redaction ([[UNI-07]]) applied to the captured bytes.

## Acceptance criteria
- [ ] Record against a real `RawSocketTransport` HTTP/1.1 streaming response, replay, bodies are byte-identical.
- [ ] Oversized recordings fail with a clear error instead of truncating.

## Context
Source: `2026-03-phase22a.2-review-fixes.md`, 2026-10 journal audit.

===ISSUE===
---
key: JRN-04
title: [Middleware] Streaming decompression never validates the gzip trailer or premature EOF
milestone: v1.0-beta: Protocol & correctness hardening
labels: type:bug, area:middleware, priority:P2, source:review-2026-10
---
## Summary
The gzip CRC32/ISIZE trailer check exists only on the buffered path for a single-gzip chain. The streaming `DecompressionBodySource` does no trailer or EOF validation, and Unity's `GZipStream` does not throw on truncated input, so a truncated compressed response is delivered as a shorter successful body.

## Location
- `Runtime/Middleware/DecompressionHandler.cs:146-180` — trailer validation in `DecompressBufferedBody` (chain length 1, gzip only)
- `Runtime/Middleware/DecompressionHandler.cs` `DecompressionBodySource` (~:209 onwards) — no trailer/EOF check
- `Tests/Runtime/Middleware/DecompressionInterceptorTests.cs:130` — only the buffered MockTransport path is tested

## Suggested fix
Track compressed bytes and verify the gzip trailer (CRC32 + ISIZE) at end of stream on the streaming path; fault the body read on mismatch or premature EOF. Define behavior for multi-coding chains (`gzip, br` etc.). Note: early dispose currently skips CRC validation by design; keep that, but never report a truncated stream as success.

## Acceptance criteria
- [ ] Streaming truncation test (cut inside deflate data and inside the trailer) fails the body read under Unity Mono, not only in the .NET harness.
- [ ] Multi-coding behavior documented and tested.

## Context
Source: `2026-03-temp-unity-fixes-gzip-mocktransport-cancellation.md`, `2026-03-phase22a.5-step1-decompression-body-source.md`, 2026-10 journal audit. Related: [[MOD-25]] (limits), [[MOD-08]] (HEAD/204/304).

===ISSUE===
---
key: JRN-05
title: [Testing] Real-TLS CONNECT tunnel tests always skip in Unity PlayMode
milestone: v1.0 GA: Docs, validation & release
labels: type:test, area:testing, area:proxy, area:tls, priority:P2, source:review-2026-10
---
## Summary
Tunnel reuse, stale-retry and HTTP/2 reroute through CONNECT have no Unity coverage: their tests call `Assert.Ignore` because the localhost certificate is generated with `CertificateRequest`, which is unavailable on Unity Mono. The skip count rose from 1 to 5 when they landed.

## Location
- `Tests/Runtime/Transport/Http1/RawSocketTransportProxyTunnelTests.cs:838-842`, `:909-913`, `:964-968`, `:1046-1050` — `Assert.Ignore(...)`
- same file `:1401` — `TryCreateLocalhostCertificate`

## Suggested fix
Embed a pre-generated test PFX (test-only CA + localhost leaf, long validity, documented as test material) or generate one with BouncyCastle; trust it only inside the test via the provider's test hook. Make unexpected skips fail CI.

## Acceptance criteria
- [ ] The four tests execute (not skip) in the `unity-tests` PlayMode job.
- [ ] CI reports a failure when the skipped count rises above the documented baseline.

## Context
Source: `2026-04-phase22c-proxy-http2-alpn.md`, `2026-04-phase22b.2-connect-tunnel-streaming.md`, 2026-10 journal audit. Related: [[PH-22c]], [[TEST-03]].

===ISSUE===
---
key: JRN-06
title: [Editor] Re-enable the ignored HTTP Monitor replay-builder test
milestone: v1.0 GA: Docs, validation & release
labels: type:test, area:editor, priority:P3, source:review-2026-10
---
## Summary
`ReplayBuilder_BuildsReplayRequestFromCapturedEvent` has started with `Assert.Ignore` since the monitor was rewritten as an interceptor.

## Location
- `Tests/Editor/HttpMonitorEditorTests.cs:115-117`

## Suggested fix
Rebuild the test on a `MonitorInterceptor`/`MonitorHandler` capture fixture.

## Acceptance criteria
- [ ] Test runs without `Assert.Ignore` in the EditMode job and covers replay of a captured request (headers redacted per [[UNI-06]]).

## Context
Source: `2026-03-phase22.3-implementation-review.md` (V-4), 2026-10 journal audit.

===ISSUE===
---
key: JRN-07
title: [Core] AdaptivePolicy exposes no-op AllowConcurrencyAdjustment / AllowRetryAdjustment flags
milestone: v1.1: Post-release hardening
labels: type:bug, area:core, priority:P3, source:review-2026-10
---
## Summary
`AdaptivePolicy` declares `AllowConcurrencyAdjustment` and `AllowRetryAdjustment` (default `true`), but `AdaptiveInterceptor` only adapts timeouts; nothing reads the two flags.

## Location
- `Runtime/Core/AdaptivePolicy.cs:10-11`
- `Runtime/Core/AdaptiveInterceptor.cs` — timeout-only

## Suggested fix
Either wire them (concurrency via `ConcurrencyLimiter`, retry via `RetryPolicy` hints) or remove/obsolete them and document the interceptor as timeout-only. Can ship with [[MOD-28]] (moving adaptive types out of Core).

## Acceptance criteria
- [ ] No public option without behavior; test for whatever remains.

## Context
Source: `2026-02-phase14-review.md` (I-10, marked "Documented"), 2026-10 journal audit.

===ISSUE===
---
key: JRN-08
title: [Files] MultipartFormDataBuilder: non-ASCII filenames need RFC 8187 filename*
milestone: v1.1: Post-release hardening
labels: type:bug, area:files, priority:P3, source:review-2026-10
---
## Summary
Non-ASCII file names are emitted raw in `filename=`. Servers decode them inconsistently; RFC 7578 §4.2 / RFC 8187 define `filename*=UTF-8''<pct-encoded>` with an ASCII fallback.

## Location
- `Runtime/Files/MultipartFormDataBuilder.cs:11-15` — XML doc says "planned for a future release"

## Suggested fix
Emit an ASCII-safe `filename=` fallback plus `filename*=UTF-8''…` when the name is not ASCII. Keep quoted-string escaping and CR/LF rejection.

## Acceptance criteria
- [ ] Tests for ASCII, Latin-1 and CJK names; CR/LF still rejected.

## Context
Source: `2026-02-phase5-content-handlers.md` (deferred to Phase 8), 2026-10 journal audit.

===ISSUE===
---
key: JRN-09
title: [Cache] Response trailers are silently dropped on cache replay
milestone: v1.1: Post-release hardening
labels: type:bug, area:cache, priority:P3, source:review-2026-10
---
## Summary
Responses with trailers are cached without them; a cache hit returns the body with `Trailers` empty.

## Location
- `Runtime/Cache/CacheEntry.cs` — no trailer field
- `Runtime/Cache/CacheStoringHandler.cs:343` — trailers loaded before store but not persisted

## Suggested fix
Persist trailers in `CacheEntry`/`ICacheStorage` and replay them, or do not store responses that carry trailers. Document the choice.

## Acceptance criteria
- [ ] Test: response with trailers → cache hit exposes the same trailers (or is not cached).

## Context
Source: `2026-03-phase22b.3-response-trailers.md` (and its review fixes, 22b.4), 2026-10 journal audit.

===ISSUE===
---
key: JRN-10
title: [Core] Remove transitional legacy shims from runtime assemblies
milestone: v1.1: Post-release hardening
labels: type:chore, area:core, area:testing, priority:P3, source:review-2026-10
---
## Summary
Test-migration helpers from the 22a.1 handler-contract break still ship in runtime assemblies.

## Location
- `Runtime/Core/LegacyHttpHandlerCompatExtensions.cs:1` — public static class in Core behind `#if UNITY_INCLUDE_TESTS`
- `Runtime/Testing/MockTransport.cs:201`, `Runtime/Testing/RecordReplayTransport.cs:265` — obsolete `SendAsync`

## Suggested fix
Move the compat extensions into `Tests/`, delete (or make hard-obsolete) `SendAsync`, migrate callers.

## Acceptance criteria
- [ ] No test-only or obsolete transitional API in `Runtime/`; tests still pass.

## Context
Source: `2026-03-phase22a.1-handler-contract-clean-break.md`, 2026-10 journal audit. Related: [[MOD-28]].

===ISSUE===
---
key: JRN-11
title: [Testing] Http2BodySizeTests.ExceedingMaxResponseBodySize_StreamShouldFail is tautological
milestone: v1.1: Post-release hardening
labels: type:test, area:testing, area:http2, priority:P3, source:review-2026-10
---
## Summary
The test re-implements the size check and asserts on its own exception; it never exercises the HTTP/2 read loop.

## Location
- `Tests/Runtime/Transport/Http2BodySizeTests.cs:15-40`

## Suggested fix
Drive a real `Http2Connection` through the duplex-stream harness with a small `MaxResponseBodySize` and assert the stream fails with RST_STREAM(CANCEL); or delete it if `Http2ConnectionTests` already covers the case. Coordinate with [[JRN-01]].

## Acceptance criteria
- [ ] The test fails when the read-loop cap is removed.

## Context
Source: `2026-03-phase22.2-implementation-review.md` (TQ-2), 2026-10 journal audit.

===ISSUE===
---
key: JRN-12
title: [Unity] Managed decoder adapters (StbImageSharp, NVorbis, NLayer) are registered stubs that always throw
milestone: v1.2: Features & performance
labels: type:bug, area:unity, priority:P3, source:review-2026-10
---
## Summary
The three managed decoders are registered by default but throw `NotSupportedException` ("adapter is not wired in this build") even when their define is set, so `CanDecode` can return true for a decoder whose `DecodeAsync` always fails.

## Location
- `Runtime/Unity/Decoders/StbImageSharpDecoder.cs:44-50`, `NVorbisDecoder.cs:42-46`, `NLayerMp3Decoder.cs:42-46`
- `Runtime/Unity/Decoders/DecoderRegistry.cs:419-424` — default registration
- `Runtime/Unity/TurboHTTP.Unity.asmdef` — `versionDefines: []` (defines are manual only)

## Suggested fix
Implement the adapters behind `versionDefines`/asmdef references with tests, or stop registering them and document that managed decode is WAV/AIFF only. Align the third-party notice review ([[REL-05]]): these are not bundled libraries today.

## Acceptance criteria
- [ ] No registered decoder whose `CanDecode` is true while `DecodeAsync` always throws.
- [ ] Docs list the supported managed formats.

## Context
Source: `2026-02-phase15-unity-runtime-hardening.md`, `2026-02-phase15-review.md` (W-1), 2026-10 journal audit.
