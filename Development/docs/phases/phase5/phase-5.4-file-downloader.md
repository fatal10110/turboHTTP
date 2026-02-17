# Phase 5.4: File Downloader

**Depends on:** Phase 5.1 (UHttpResponse Content Helpers), Phase 5.2 (Content Type Constants)
**Assembly:** `TurboHTTP.Files`
**Files:** 1 new

---

## Step 1: Define Download Models

**File:** `Runtime/Files/FileDownloader.cs`
**Namespace:** `TurboHTTP.Files`

Define supporting types:

```csharp
public class DownloadProgress
public class DownloadOptions
public class DownloadResult
public class ChecksumMismatchException : Exception
```

Required fields:

1. Progress: bytes downloaded, total bytes, percentage, elapsed time, speed.
2. Options: resume toggle, checksum toggle, expected MD5/SHA256, progress callback.
3. Result: path, size, elapsed time, was-resumed flag.
4. Exception: algorithm, expected hash, actual hash.

---

## Step 2: Implement `FileDownloader.DownloadFileAsync`

**File:** `Runtime/Files/FileDownloader.cs`

Primary API:

```csharp
public Task<DownloadResult> DownloadFileAsync(
    string url,
    string destinationPath,
    DownloadOptions options = null,
    CancellationToken cancellationToken = default)
```

Execution flow:

1. Validate arguments and normalize options.
2. Detect existing partial file when resume is enabled.
3. Send request with `Range` header when resuming.
4. Handle `416 Range Not Satisfiable` by deleting partial file and retrying from scratch.
5. Handle non-206 resume responses by restarting from scratch.
6. Validate `Content-Range` start position when present.
7. Enforce success semantics (`EnsureSuccessStatusCode()` on error statuses).
8. Create destination directory when needed.
9. Write response body bytes to disk (append or create mode).
10. Report progress after write completion.
11. Verify checksum(s) and throw `ChecksumMismatchException` on mismatch.
12. Return `DownloadResult`.

---

## Step 3: Helper Methods

1. `ParseContentRangeStart(string)` extracts resume start byte.
2. `ComputeHash(string, HashAlgorithm)` computes normalized lowercase hex hash.

Implementation notes:

1. `FileDownloader` constructor requires a non-null `UHttpClient`.
2. Response body is currently buffered before disk write due current transport contract.
3. Progress callback reports one completion snapshot, not incremental chunks.

---

## Verification Criteria

1. Fresh download writes exact response bytes to disk.
2. Resume appends bytes on `206 PartialContent`.
3. Resume fallback replaces file when server responds `200 OK`.
4. `Range` header is sent for resumed requests.
5. MD5 and SHA256 verification succeed for valid hashes.
6. Checksum mismatch throws `ChecksumMismatchException`.
7. Destination directory is auto-created.
8. HTTP failure propagates via `UHttpException`.
