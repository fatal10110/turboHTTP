# Phase 7.2: Record/Replay Transport

**Depends on:** Phase 7.1
**Assembly:** `TurboHTTP.Testing`
**Files:** 1 new

---

## Step 1: Implement Modes and Data Model

**File:** `Runtime/Testing/RecordReplayTransport.cs`

Required behavior:

1. Support `Record`, `Replay`, and `Passthrough` modes.
2. Persist request/response interactions as serializable records.
3. Load recordings for replay mode at startup.
4. Include recording format version and timestamp metadata.
5. Serialization path is explicit and AOT-safe (use `TurboHTTP.JSON.JsonSerializer` with project-supported AOT backend).
6. Recording schema uses concrete DTO types only (no anonymous/dynamic shapes), for example:
   - `RecordingFile { Version, Entries }`
   - `RecordingEntry { Method, Url, RequestHeaders, RequestBodyHash, StatusCode, ResponseHeaders, ResponseBody, TimestampTicks }`

---

## Step 2: Implement Recording Path

Required behavior:

1. Forward request to inner transport.
2. Capture method, URL, headers, body, status, response headers/body, and error.
3. Preserve multi-value headers in recording format.
4. Redact sensitive data via configurable policy (headers, query keys, optional body fields).
5. Save recordings to disk via explicit `SaveRecordings()`.

---

## Step 3: Implement Replay Path

Required behavior:

1. Replay recorded interactions using stable request-key matching.
2. Build `UHttpResponse` from stored records.
3. Support mismatch policy enum (`Strict`, `Warn`, `Relaxed`) with `Strict` default.
4. Matching inputs are explicit:
   - include method + normalized URL + body hash
   - body hash uses SHA-256
   - for bodies >1MB, hash first 64KB + last 64KB + content length
   - include stable header subset only
   - exclude volatile headers by default (`Date`, `Age`, `X-Request-ID`, `X-Correlation-ID`, `X-Trace-ID`, `X-Span-ID`, `Traceparent`, `Tracestate`, auth/cookie headers unless explicitly included)
5. Concurrency strategy is explicit: per-request-key `ConcurrentQueue<RecordingEntry>` (no global replay lock).

Implementation constraints:

1. Fail fast when recording file is missing in replay mode.
2. Keep serialization format stable and versionable.
3. Use AOT-safe serialization path for IL2CPP compatibility.
4. Implement `IDisposable` to flush/save (when configured) and dispose inner transport.
   - default policy: auto-flush in record mode on dispose; explicit opt-out only.
5. Document IL2CPP stripping preservation requirements for hashing/serialization (`link.xml` entries where needed).
   - required `link.xml` guidance includes SHA-256 types used by hashing path.
6. Hashing path uses `SHA256.Create()` with null/exception guard and explicit diagnostics when unavailable.
   - fallback policy: if SHA-256 provider unavailable, fail fast with actionable exception (no silent downgrade).

Recommended `link.xml` guidance (adjust assembly names as needed for Unity/.NET profile):

```xml
<linker>
  <assembly fullname="mscorlib">
    <type fullname="System.Security.Cryptography.SHA256" preserve="all" />
    <type fullname="System.Security.Cryptography.SHA256Managed" preserve="all" />
    <type fullname="System.Security.Cryptography.SHA256CryptoServiceProvider" preserve="all" />
  </assembly>
</linker>
```

---

## Verification Criteria

1. Record-then-replay returns equivalent responses.
2. Redaction policy is applied in saved recording artifacts.
3. Strict mismatch mode fails deterministically on request-key mismatch.
4. Replay behavior remains deterministic for parallel request scenarios.
5. IL2CPP/AOT replay serialization path is validated in platform-compat testing.
