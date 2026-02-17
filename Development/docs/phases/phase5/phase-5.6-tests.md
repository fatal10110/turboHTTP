# Phase 5.6: Tests and Integration

**Depends on:** Phase 5.1, 5.2, 5.3, 5.4, 5.5
**Assembly:** `TurboHTTP.Tests.Runtime`
**Files:** 4 new

---

## Step 1: `ContentTypesTests`

**File:** `Tests/Runtime/Core/ContentTypesTests.cs`
**Tests:** 9

Coverage:

1. Verifies every `ContentTypes` constant string value.
2. Prevents accidental MIME value regressions.

---

## Step 2: `JsonExtensionsTests`

**File:** `Tests/Runtime/Core/JsonExtensionsTests.cs`
**Tests:** 19

Coverage:

1. `AsJson<T>` success, empty-body defaults, invalid JSON failure.
2. `TryAsJson<T>` true/false semantics and out parameter behavior.
3. `GetBodyAsString(Encoding)` null and decode behavior.
4. `GetContentEncoding()` charset detection and UTF-8 fallback.
5. JSON round-trip (`WithJsonBody` -> `AsJson`).
6. `GetJsonAsync` and `PostJsonAsync` with `MockTransport`.

---

## Step 3: `FileDownloaderTests`

**File:** `Tests/Runtime/Files/FileDownloaderTests.cs`
**Tests:** 12

Coverage:

1. Basic download writes exact body bytes.
2. Resume flow appends data on `206 PartialContent`.
3. Resume fallback restarts on `200 OK`.
4. `416 Range Not Satisfiable` retries from scratch.
5. `Content-Range` mismatch fallback safety.
6. `Range` header is sent when resuming.
7. MD5 and SHA256 checksum validation.
8. Checksum mismatch exception path.
9. Progress callback invocation.
10. HTTP error propagation.
11. Destination directory auto-creation.
12. Constructor null-guard.

---

## Step 4: `MultipartFormDataBuilderTests`

**File:** `Tests/Runtime/Files/MultipartFormDataBuilderTests.cs`
**Tests:** 24

Coverage:

1. Single/multiple text field formatting.
2. File part metadata and raw byte inclusion.
3. Mixed part ordering and boundary structure.
4. Boundary exposure and content-type generation.
5. Boundary validation (empty, too long, invalid chars, valid chars).
6. CR/LF injection rejection for field names and filenames.
7. Quote escaping in `Content-Disposition`.
8. Null argument guards.
9. `ApplyTo()` request integration.
10. Empty multipart payload behavior.
11. Fluent chaining behavior.

---

## Verification Criteria

1. All 64 Phase 5 tests pass reliably.
2. No regressions in pre-existing core/pipeline tests.
3. `TurboHTTP.Core` and `TurboHTTP.Files` compile with no new warnings.
