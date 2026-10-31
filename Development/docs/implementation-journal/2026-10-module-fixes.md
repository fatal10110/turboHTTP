# 2026-10 module-fixes: safe FileDownloader resume (#50, #66), Retry-After capped by MaxDelay (#54), shared backoff jitter (#183)

## What

- **#50 (error responses destroyed downloads).** `FileDownloader` deleted the file at the destination on
  any non-206 answer to a resume attempt, before checking success, so a 503/404/401 wiped a partial (and a
  5xx while re-downloading over a complete file wiped the complete file). Success is now checked before any
  file is touched: 4xx/5xx throw `UHttpException` and leave the partial, its validator and the destination
  as they were. Only a 416, a 206 that does not continue the partial, or a 2xx full representation discards
  or truncates the partial.
- **#66 (resume could splice representations).**
  - Bytes go to `<dest>.part`; the destination is created, or atomically replaced (`File.Replace`), only
    after the body completed and the optional checksum matched. A failed, cancelled or corrupt download
    never leaves a truncated file at the destination and never touches an existing complete file there.
  - When a download starts, the response's strong validator is stored in `<dest>.part.validator`. A later
    call resumes only a partial with a stored validator, sending `Range: bytes=N-` + `If-Range` +
    `Accept-Encoding: identity`. A changed resource answers 200 and restarts from scratch.
  - A 206 is appended only when `Content-Range` is a valid `bytes first-last/complete|*` whose first byte
    equals the partial's length, and (when the response carries an `ETag` and the stored validator is an
    entity-tag) the same entity-tag. Anything else (missing/garbled Content-Range, wrong start, different
    ETag) discards the partial and refetches without Range. A 206 to a request without Range throws.
  - A checksum mismatch deletes the partial and its validator (the previous destination file stays).
  - Without `EnableResume`, an interrupted partial is deleted.
- **#54 (Retry-After unbounded).** `RetryPolicy.ComputeDelay` clamps the Retry-After override to
  `MaxDelay`. `RetryDetectorHandler` gets the cap: a retryable 5xx whose Retry-After exceeds `MaxDelay` is
  delivered to the caller (status, headers with `Retry-After`, body) instead of being discarded and retried;
  `RetryInterceptor` records a `RetryAfterExceedsMaxDelay` timeline event and returns.
- **#183 (duplicated jitter).** `TurboHTTP.Core.Internal.BackoffJitter.NextDouble()` (thread-static
  `Random`, seed = GUID hash ^ Interlocked counter * golden ratio ^ managed thread id) replaces the two
  copies in `RetryPolicy` and `WebSocketReconnectPolicy`.

## Files

- `Runtime/Files/FileDownloader.cs`: download flow rewritten around `.part` + validator sidecar;
  `SendDownloadRequestAsync`, `IsResumableContinuation`, `TryParseContentRange` (internal),
  `SelectStrongValidator` (internal), validator read/write, `CommitPartFile`; XML docs on
  `DownloadFileAsync` describe the file layout.
- `Runtime/Retry/RetryPolicy.cs` (clamp, `MaxDelay` docs, jitter helper), `RetryDetectorHandler.cs`
  (cap, `RetryAfterExceededCap`), `RetryInterceptor.cs` (passes `MaxDelay`, event + early return).
- `Runtime/WebSocket/WebSocketReconnectPolicy.cs`: uses `BackoffJitter`.
- `Runtime/Core/Internal/BackoffJitter.cs` (new).
- `Runtime/Files/AssemblyInfo.cs`, `Runtime/Retry/AssemblyInfo.cs` (new): `InternalsVisibleTo` the runtime
  test assembly (same pattern as Cache/WebSocket/Transport); no new asmdef references.
- Tests: `Tests/Runtime/Files/FileDownloaderResumeSafetyTests.cs`, `Tests/Runtime/Retry/RetryAfterCapTests.cs`,
  `Tests/Runtime/Core/BackoffJitterTests.cs` (new); `FileDownloaderTests` resume tests now seed
  `<dest>.part` + validator (a file at the destination is complete by definition now);
  `RetryInterceptorTests.RetryAfterHeader_OverridesConfiguredDelay` raises `MaxDelay` to 10 s.
- Docs: `Documentation~/Modules/Middleware.md` (Retry-After behavior), `Documentation~/APIReference.md`
  (download file layout and resume rules; fixed `new FileDownloader()` -> `new FileDownloader(client)`).

## Decisions

- **Retry-After above MaxDelay fails fast (return the response) rather than clamping.** Clamping would
  retry earlier than the server asked (RFC 9110 section 10.2.3 says how long the client "ought to wait");
  waiting lets a hostile server park the call and its concurrency permit. Returning the response keeps the
  header available to the caller. `MaxDelay` is reused as the cap (no new public property). This reverses
  the explicit decision in `2026-03-phase22-retry-review-fixes.md` that Retry-After may exceed MaxDelay.
- **Resume requires a strong validator.** RFC 9110 section 13.1.5 forbids If-Range with a weak entity-tag
  and with a date unless the date is a strong validator (section 8.8.2.2: Last-Modified at least one second
  before Date). Without one, a partial cannot be proven to belong to the current representation, so it is
  not resumed (it is overwritten by the next full download). Strong ETag is preferred; a weak ETag
  disables the Last-Modified fallback.
- **`Accept-Encoding: identity` on every request when `EnableResume` is on** (not only on Range
  requests): byte offsets only make sense against one fixed coding, and a content-coded first download
  would never match the identity range of a later attempt (different strong validators per coding).
- **Destination is never a resume source.** Older versions resumed from the destination file itself; such a
  file is now treated as complete and replaced only by a verified new download.
- **416 and invalid 206 delete the partial** (known unusable, issue #50 suggested fix 2); 4xx/5xx never do.

## Deferred

- #54 suggested fix 3 ("consider an overall retry budget") is not an acceptance criterion and was not done.

## How verified

- `'./Tools~/ci/check.sh'`: pass (repo invariants, dotnet format, shadow build with analyzers).
- The touched tests were also compiled and run outside Unity in a scratch .NET 8 NUnit project (shadow-built
  Core/Retry/Files/Testing/WebSocket plus a stub `UnityEngine.CoreModule` providing only
  `Application.platform/isEditor/isPlaying`): 75/75 pass (RetryInterceptorTests, RetryAfterCapTests,
  FileDownloaderTests, FileDownloaderResumeSafetyTests, BackoffJitterTests, WebSocketReconnectPolicyTests).
  With the four runtime files reverted to `origin/main`, 15 of the 19 new behavior tests fail (the 4 that
  pass are the within-cap/sanity cases).
- **Not run:** Unity EditMode/PlayMode (no Unity license/editor on this host; PR CI only), IL2CPP, devices.
  `File.Replace` with a null backup path is used for the atomic replace; its behavior on Mono/IL2CPP mobile
  file systems is covered by CI EditMode only, not by a device run.
