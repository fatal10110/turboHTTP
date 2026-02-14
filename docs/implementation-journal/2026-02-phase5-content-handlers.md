# Phase 5: Content Handlers

**Date:** 2026-02-14
**Phase:** 5 (Content Handlers)
**Status:** Complete (reviews passed with fixes applied)

## What Was Implemented

Content handling utilities for common use cases: JSON deserialization, file downloads with resume/checksum, multipart uploads, encoding-aware string decoding, and MIME type constants. Together with Phase 4 (pipeline), this completes M1 milestone ("usable").

## Files Created

### Step 1 — UHttpResponse Enhancements (1 modified)

| File | Description |
|------|-------------|
| `Runtime/Core/UHttpResponse.cs` | **Modified** — Added `GetBodyAsString(Encoding)` overload, `GetContentEncoding()` method that auto-detects charset from Content-Type header (RFC 9110 Section 8.3.1) using manual string parser (no Regex, IL2CPP-safe). Falls back to UTF-8. |

### Step 2 — ContentTypes (1 new)

| File | Description |
|------|-------------|
| `Runtime/Core/ContentTypes.cs` | Static class with 12 MIME type constants (Json, Xml, FormUrlEncoded, MultipartFormData, PlainText, Html, OctetStream, Png, Jpeg, Gif, Pdf, Zip). |

### Step 3 — JSON Extensions (1 new, in TurboHTTP.Core assembly)

| File | Description |
|------|-------------|
| `Runtime/Core/JsonExtensions.cs` | Extension methods: `AsJson<T>()`, `AsJson<T>(IJsonSerializer)`, `TryAsJson<T>()` on `UHttpResponse`; `GetJsonAsync<T>()`, `PostJsonAsync<TReq,TRes>()`, `PutJsonAsync<TReq,TRes>()`, `PatchJsonAsync<TReq,TRes>()`, `DeleteJsonAsync<T>()` on `UHttpClient`. All use `TurboHTTP.JSON.JsonSerializer` (not System.Text.Json directly). All async methods use `ConfigureAwait(false)`. |

### Step 4 — FileDownloader (1 new, in TurboHTTP.Files assembly)

| File | Description |
|------|-------------|
| `Runtime/Files/FileDownloader.cs` | Download files with resume support (Range header + HTTP 206), HTTP 416 handling (retry from scratch), Content-Range validation, progress reporting (`IProgress<DownloadProgress>`), and checksum verification (MD5, SHA256). Includes `DownloadProgress`, `DownloadOptions`, `DownloadResult`, and `ChecksumMismatchException` types. Auto-creates destination directory. |

### Step 5 — MultipartFormDataBuilder (1 new, in TurboHTTP.Files assembly)

| File | Description |
|------|-------------|
| `Runtime/Files/MultipartFormDataBuilder.cs` | Fluent builder for `multipart/form-data` bodies. CRLF injection prevention on name/filename. Quote escaping in Content-Disposition headers. Boundary validation per RFC 2046 (bchars, 1-70 length). `AddField()` for text, `AddFile()` for byte arrays, `AddFileFromDisk()` for files on disk. `Build()` returns `byte[]`, `GetContentType()` returns Content-Type with boundary, `ApplyTo()` sets both on a `UHttpRequestBuilder`. |

### Step 6 — Tests (4 new)

| File | Tests | Description |
|------|-------|-------------|
| `Tests/Runtime/Core/ContentTypesTests.cs` | 9 | All MIME type constants verified |
| `Tests/Runtime/Core/JsonExtensionsTests.cs` | 19 | AsJson, TryAsJson, GetBodyAsString(Encoding), GetContentEncoding, JSON round-trip, GetJsonAsync, PostJsonAsync, error handling |
| `Tests/Runtime/Files/FileDownloaderTests.cs` | 12 | Basic download, resume (206), resume fallback (200), 416 retry, Content-Range mismatch, MD5/SHA256 checksums, checksum mismatch, progress, HTTP error, directory creation, null client |
| `Tests/Runtime/Files/MultipartFormDataBuilderTests.cs` | 24 | Single/multiple fields, file with metadata, file bytes, mixed parts, content type, boundary validation (empty/long/invalid chars), CRLF injection rejection, quote escaping, null validation, ApplyTo, empty build, fluent chaining |

## Decisions Made

1. **JSON uses TurboHTTP.JSON.JsonSerializer, not System.Text.Json:** The Phase 5 plan document referenced System.Text.Json directly, but the project already has a JSON abstraction layer (`TurboHTTP.JSON.JsonSerializer` static facade + `IJsonSerializer` interface). Phase 5 extensions use this facade, keeping the option open for users to swap serializers (LiteJson, Newtonsoft, etc.).

2. **FileDownloader requires explicit UHttpClient:** Constructor requires a non-null `UHttpClient` (no default/hidden allocation). Users control client lifecycle, middleware configuration, and TLS backend.

3. **ChecksumMismatchException:** Custom exception type with `Algorithm`, `Expected`, and `Actual` properties. Typed exception enables structured catch/retry logic in user code.

4. **MultipartFormDataBuilder boundary constructor is public:** Initially `internal`, made `public` so tests and users who need reproducible output can provide a fixed boundary. Validated per RFC 2046 bchars + 70-char max.

5. **ContentTypes in Core assembly:** MIME constants live in `TurboHTTP.Core` (autoReferenced) so all modules and user code can reference them without extra assembly references.

6. **GetContentEncoding() uses manual parser, not Regex:** Avoids Regex allocation on every call and IL2CPP code stripping risk. Falls back to UTF-8 for unknown/missing charset.

7. **AsJson uses UTF-8 unconditionally:** Per RFC 8259 Section 8.1, JSON MUST be encoded as UTF-8. Ignores Content-Type charset for JSON specifically.

8. **Progress is single-shot (documented):** Current transport design buffers entire body in memory. Progress reports once after download completes. Documented as limitation; incremental progress deferred to Phase 10 streaming transport.

## Review Findings & Fixes

Two specialist reviews (unity-infrastructure-architect, unity-network-architect) identified the following issues. All HIGH and MEDIUM severity items were fixed.

### Fixed (HIGH + MEDIUM)

| Severity | Component | Issue | Fix |
|----------|-----------|-------|-----|
| HIGH | MultipartFormDataBuilder | Content-Disposition header injection via unsanitized name/filename (CRLF, quotes) | Added `ValidateNoCrLf()` to reject CR/LF in names, `EscapeQuotedString()` to escape `"` and `\` in Content-Disposition |
| MEDIUM | MultipartFormDataBuilder | Boundary not validated in public constructor (RFC 2046 violations possible) | Added `IsValidBoundaryChar()` validation, 1-70 char length check |
| MEDIUM | FileDownloader | No Content-Range validation on 206 (silent corruption if range mismatch) | Added `ParseContentRangeStart()`, verifies start byte matches requested offset |
| MEDIUM | FileDownloader | No HTTP 416 handling (throws instead of retry-from-scratch) | Added 416 detection, deletes partial file, retries without Range header |
| MEDIUM | UHttpResponse | Regex.Match() on every GetContentEncoding() call (perf + IL2CPP stripping risk) | Replaced with manual string parser, removed `System.Text.RegularExpressions` import |
| MEDIUM | JsonExtensions | Missing ConfigureAwait(false) on async extension methods | Added `.ConfigureAwait(false)` to all `await` calls in JsonExtensions + FileDownloader |
| MEDIUM | JsonExtensions | TryAsJson catches all exceptions (should not swallow OOM etc.) | Narrowed to `catch(JsonSerializationException)` + `catch(ArgumentNullException)` |
| MEDIUM | FileDownloader/DownloadProgress | Progress API implies incremental but only reports once | Added doc noting single-shot limitation; full-body-in-memory architectural note |

### Deferred (LOW / Later Phases)

| Issue | Target Phase |
|-------|-------------|
| File.Exists TOCTOU race (theoretical, not practical) | Not planned |
| Non-ASCII filenames need RFC 5987 `filename*=` encoding | Phase 8 |
| Full-body-in-memory download limitation | Phase 10 (streaming transport) |
| Incremental progress reporting | Phase 10 (streaming transport) |
| Checksum mismatch should delete corrupt file for cleaner resume | Phase 10 |

## Test Results

- Total: 506 (up from 415 pre-Phase 5)
- Passed: 479
- Failed: 0
- Skipped: 27 (all legitimately skipped — network access, Explicit, conditional)
- New Phase 5 tests: 64 (9 + 19 + 12 + 24)

## Directory Changes

- Created: `Tests/Runtime/Files/`
- New files in: `Runtime/Core/`, `Runtime/Files/`, `Tests/Runtime/Core/`, `Tests/Runtime/Files/`
