# 2026-10 Redaction on by default (#65, #92, #93, #149; part of #186)

Lane `redaction`, run w1030 (v1.0-beta).

## What
- **#65 LoggingInterceptor.** New public immutable `RedactionPolicy` (Observability). By default logs replace every URL
  query value and URL user-info with `[REDACTED]` at all log levels (query names are kept), and the default sensitive
  header list now also has Proxy-Authenticate, Api-Key, X-Auth-Token, X-Amz-Security-Token, X-Goog-Api-Key,
  X-CSRF-Token and X-XSRF-Token. Configure it with `new LoggingInterceptor(..., redactionPolicy: RedactionPolicy.Default.WithSensitiveHeaders(..)
  .WithoutSensitiveHeaders(..).WithQueryValueRedaction(false))`. The request, response, completion and error lines all
  print the redacted URL. The marker changed from `****` to `[REDACTED]`.
- **#92 Monitor.** Redaction now happens at capture time (`MonitorInterceptor.RedactionPolicy`, default
  `RedactionPolicy.Default`, opt out with `RedactionPolicy.None`). It runs before `HeaderValueTransform`, so a transform
  that throws leaves the redacted value in place. Capture is gated: `IsCapturing = CaptureEnabled && (ViewerCount > 0 ||
  CaptureWithoutViewer)`. `HttpMonitorWindow` calls `AttachViewer()` in OnEnable and disposes the handle in OnDisable.
  When the gate is closed, `Wrap` passes the request through untouched (no MonitorHandler, no snapshots). History is
  also bounded by bytes: `MaxTotalCaptureBytes` (default 32 MB), an estimate of body bytes plus UTF-16 strings,
  tracked in `_historySizes`/`_historyBytes` under `HistoryLock`. The oldest events are evicted, each body is capped
  at a quarter of the budget, and an event that still exceeds the budget is dropped. The window and export use
  `RedactionPolicy.Default` instead of their own list, and the export also redacts the URL. Replay never sends a
  `[REDACTED]` header and warns when the event was captured redacted. The request list is virtualized: fixed 22 px
  rects, spacers, only visible rows drawn. New preferences: Redact Captured Secrets (on), Capture While Monitor Closed
  (off), Total Capture Budget (32 MB). The mask toggle now defaults to on.
- **#186 part 2.** New Core-internal `EditorMonitorHook`, registered by `ObservabilityModuleRegistration.EnsureRegistered()`
  (`[Preserve]`, `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`). `UHttpClient` no longer uses `Type.GetType`,
  `Activator.CreateInstance` or `GetProperty`. Removed along with them: the `check_repo.py` REFLECT_ALLOWLIST entry, the
  analyzer-baseline line, the Observability link.xml rule, and the linker test case for MonitorInterceptor.
- **#93 RecordReplay secrets.** Added default fields access_token, refresh_token, id_token, client_secret, password,
  code and code_verifier (headers and query names were extended as well). JSON and form field matching ignores case
  and `_ - . space`. Bodies are classified by media type: `application/json`, `text/json` and `*+json` (RFC 6839
  section 3.1) are JSON; `application/x-www-form-urlencoded` names are decoded before matching. Redaction **fails
  closed**: a JSON or form body that will not parse is stored as the placeholder. In the request key, sensitive header
  values become `hmac-sha256:` under a per-recording 32-byte salt stored as `KeySalt` in the file (schema stays 1;
  the field is optional), and so does the request body hash. Headers named in `MatchHeaderNames` override the default
  exclusions (Authorization, Cookie, ...).
- **#149 streaming bodies.** When `TryGetBufferedData` is false, `RecordingHandler` drains the source with the dispatch
  token, bounded by the new option `MaxRecordBytes` (default 16 MB). It then disposes the original source and forwards
  a buffered copy downstream. An oversized body, streaming or buffered, gives the handler a `UHttpException` that names
  MaxRecordBytes and records nothing. It also marks the transport failed, so `SaveRecordings` throws and writes no
  file, including the auto-flush on dispose.

## Files
Runtime/Core/EditorMonitorHook.cs (new), Runtime/Core/UHttpClient.cs; Runtime/Observability/{RedactionPolicy.cs,
ObservabilityModuleRegistration.cs (new), LoggingInterceptor.cs, LoggingHandler.cs, MonitorInterceptor.cs, link.xml};
Runtime/Testing/RecordReplayTransport{,.Keys,.Redaction,.Serialization,.Utilities}.cs, link.xml (HMACSHA256,
RNGCryptoServiceProvider); Editor/Monitor/HttpMonitorWindow{,.Panels,.Export,.Replay}.cs, Editor/Settings/TurboHttpSettings.cs;
Tools~/ci/check_repo.py, analyzer-baseline.txt. Tests: Tests/Runtime/Pipeline/LoggingRedactionTests.cs,
Tests/Runtime/Observability/MonitorRedactionTests.cs, Tests/Runtime/Testing/RecordReplayRedactionTests.cs,
Tests/Runtime/Testing/RecordReplayStreamingTests.cs, Tests/Editor/HttpMonitorRedactionSettingsTests.cs (new); edits to
MonitorInterceptorTests (reset adds CaptureWithoutViewer/policy/budget; the multi-value test opts out of redaction),
TurboHttpLinkerProcessorTests (reflection target gone) and HttpMonitorEditorTests. The last one drops an unused
`SuccessfulStubTransport` that still called the removed `OnResponseStart`/`OnResponseEnd` API and kept
TurboHTTP.Tests.Editor from compiling.

## Decisions
- One shared policy for Logging and Monitor lives in Observability, because Editor references it. RecordReplay keeps
  its own aligned list because Testing cannot reference Observability. Making a single object serve every module is
  #117 (v1.1), amended.
- In Record mode a streaming response is buffered whole, bounded by MaxRecordBytes, before the consumer sees it. A
  recording transport is test tooling, and this is the only way to fail before any partial body has been forwarded.
- `UHttpErrorType.Unknown` is used for oversize, since no "too large" type exists. The message names the option, the
  limit and the URL with sensitive query values redacted.
- Monitor body redaction was optional in #92 and is not done here (#117).

## Deferred
- Single policy object across modules, Monitor and Logging body redaction, and L-5 UTF-16 binary detection: #117 (amended).

## Verification
- `'./Tools~/ci/check.sh'`: OK (invariants, format, shadow build with analyzers).
- New and affected runtime tests were run outside Unity on .NET 8 with NUnit 3.14 against the shadow-built DLLs: a
  scratch project compiling LoggingRedactionTests, LoggingInterceptorTests, MonitorRedactionTests,
  MonitorInterceptorTests, RecordReplayRedactionTests, RecordReplayStreamingTests and RecordReplayTransportTests. Result:
  68/68 passed, including the loopback RawSocketTransport chunked test. Tests/Editor was compile-checked the same way.
- Not run: Unity EditMode/PlayMode (left to the periodic host-editor run), IL2CPP, devices. The Editor window
  (virtualized list responsiveness with 1000 entries, viewer attach on open/close) cannot be checked headless and needs
  a manual look in the host editor.
