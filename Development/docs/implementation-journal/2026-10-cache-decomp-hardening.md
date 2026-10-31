# 2026-10 Cache and decompression hardening (#51, #52, #57, #150)

## What
- **#51 Cache never stores 206; Range/If-Range bypass.** `CacheInterceptor.ShouldConsiderForStorage` rejects 206
  (RFC 9111 section 3.3: this cache does not implement partial-content storage). All storage paths go through it:
  the miss path (`CacheStoringHandler`), the queued store (`StoreResponseAsync`) and the revalidation path, which
  now uses `CacheStoringHandler` as well. A request with `Range` or `If-Range` skips the cache completely: no
  lookup, no `CacheMiss` event, no store.
- **#52 No full buffering for unsafe methods or revalidation.** The cache no longer calls the buffered collector
  (`TransportDispatchHelper.CollectResponseAsync`), so it never touches `StreamingResponseRequested`. New partial
  `CacheInterceptor.StreamingDispatch.cs`:
  - `UnsafeInvalidationHandler` (pass-through). For a 2xx/3xx status it invalidates the target URI and the
    same-origin Location/Content-Location before the response reaches the caller, then forwards the body as is.
    RFC 9111 section 4.4 says "non-error status". The old rule was `< 500`, which also invalidated on 4xx.
  - `RevalidationHandler`. On 304 it releases the empty body, merges the headers (`MergeNotModifiedEntry` now
    takes `HttpHeaders`) and serves the stored entry. On any other status it removes the old entry and streams the
    response through `CacheStoringHandler`, which tees it into the store. The caller's `OnRequestStart` is
    deferred until that point (always with the original request), because a 304 builds its own lifecycle.
  - `BackgroundRevalidationSink` replaces `NullHttpHandler` for stale-while-revalidate. It reads a streamed
    replacement to its natural end so the tee stores it, bounded by `MaxCacheableResponseBodyBytes`. The old
    no-op handler would have aborted the tee.
- **#57 No decoding for responses without content.** `DecompressionHandler` records the request method in
  `OnRequestStart`. It passes the response through untouched, with Content-Encoding and Content-Length kept, for
  HEAD, 1xx/204/304, `Length == 0` or an empty buffered body.
- **#150 gzip trailer and premature EOF on every path.** Each gzip layer is now `GzipInputTrackingStream` (counts
  compressed bytes and keeps the last 8) -> `GZipStream` -> `DecoderCompletionStream`. The completion stream
  CRC32s the decoded bytes. When the decoder reports EOF, it drains the rest of the layer input (up to 64 KiB,
  more fails) and checks CRC32 + ISIZE. A stacked deflate layer is wrapped too, so it drains the gzip layer below
  it to its end. The buffered and streaming paths share the chain, so `ValidateSingleGzipTrailer` is gone. Both
  `DecompressionBodySource` EOF sites (`ReadAsync`, `ProbePastLimitAsync`) go through it. Failures surface as the
  existing `UHttpException("Response decompression failed.")`. `DisposeStreams` logs dispose failures with
  `Debug.WriteLine` instead of swallowing them (#180 adds the sink later).

## Decisions
- #51 allow-list: only 206 is added to the rejects. 201/202/205 with explicit freshness stay storable, which
  RFC 9111 section 3 permits. A Range request answered 200 is not stored either (conservative).
- #150 vs phase doc: `phase-22a.5-interceptor-rewrite.md` ("GZIP Trailer Validation in Streaming Mode") relies on
  GZipStream's internal CRC check. Unity's Mono GZipStream reports truncation as a clean EOF
  (`2026-03-temp-unity-fixes-gzip-mocktransport-cancellation.md`). The .NET 8 harness behaves the same: the new
  truncation tests fail against the old code there too. The issue was followed.
- Multi-coding: every gzip layer validates its own trailer. A raw deflate layer has no trailer and relies on
  the runtime inflater. A transport truncation always hits the outermost layer, so it is detected whenever that
  layer is gzip.
- Revalidation non-304: remove first, then store after body EOF. This replaces the old store-then-remove step.
  Both are serialized per base key, and a miss between them is safe.

## Deferred
- #219 (v1.0-beta): `deflate` should be zlib (RFC 9110 section 8.4.1.2), and a multi-member gzip body fails
  trailer validation. This was pre-existing on the buffered path.
- #74 (decompression limits) and #180 (diagnostic sink) are untouched.

## Files
`Runtime/Cache/CacheInterceptor.cs`, `Runtime/Cache/CacheInterceptor.StreamingDispatch.cs` (new),
`Runtime/Middleware/DecompressionHandler.cs`, `Tests/Runtime/Cache/CacheInterceptorTests.RangeAndStreaming.cs`
(new), `Tests/Runtime/Middleware/DecompressionBodilessAndTruncationTests.cs` (new),
`Tests/Runtime/Performance/StreamingAllocationGateTests.cs` (two cache response-start gates).

## Verification
- `./Tools~/ci/check.sh`: OK.
- Scratch .NET 8 NUnit harness, not committed. It compiled the Cache and Decompression test files against the
  shadow-built DLLs. With the change: 75/76 pass. The one failure is
  `NonBufferedCompressedResponse_IsDecompressed_WhenStreamingResponseIsRequested`, which constructs `UHttpClient`
  and hits Unity ECalls outside Unity; it fails the same way on main. Against the origin/main runtime, all 22 new
  regression tests fail.
- Not run: Unity EditMode/PlayMode, so the new `StreamingAllocationGateTests` cache gates (they need
  `UHttpClient`) were never executed. No IL2CPP or device runs. Unity tests: host editor (periodic).
