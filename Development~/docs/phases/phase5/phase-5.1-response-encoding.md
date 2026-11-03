# Phase 5.1: UHttpResponse Content Helpers

**Depends on:** Phase 4 (Pipeline Infrastructure)
**Assembly:** `TurboHTTP.Core`
**Files:** 1 modified

---

## Step 1: Add `GetBodyAsString(Encoding)` Overload

**File:** `Runtime/Core/UHttpResponse.cs`
**Namespace:** `TurboHTTP.Core`

Add an overload that decodes with a caller-supplied encoding:

```csharp
public string GetBodyAsString(Encoding encoding)
```

Required behavior:

1. Throw `ArgumentNullException` when `encoding` is null.
2. Return `null` when `Body` is null or empty.
3. Decode using `encoding.GetString(Body)`.
4. Preserve existing `GetBodyAsString()` behavior (UTF-8 default).

---

## Step 2: Add `GetContentEncoding()` Detection

**File:** `Runtime/Core/UHttpResponse.cs`

Add charset detection from `Content-Type`:

```csharp
public Encoding GetContentEncoding()
```

Detection rules:

1. Read header `Content-Type`.
2. Parse `charset=...` token case-insensitively.
3. Support quoted and unquoted charset values.
4. Return `Encoding.GetEncoding(charset)` when valid.
5. Fallback to `Encoding.UTF8` when header/charset is missing or invalid.

Implementation constraints:

1. No regex dependency in hot path; use string scanning.
2. Never throw for unknown charsets; fallback to UTF-8.
3. Keep behavior deterministic across IL2CPP/Mono runtime targets.

---

## Verification Criteria

1. Existing `GetBodyAsString()` tests still pass.
2. `GetBodyAsString(Encoding)` decodes ASCII/UTF-8 correctly.
3. `GetBodyAsString(Encoding)` throws on null encoding.
4. `GetContentEncoding()` returns UTF-8 when `Content-Type` is absent.
5. `GetContentEncoding()` returns UTF-8 for unknown charset values.
6. `GetContentEncoding()` returns expected encoding for valid charset tokens.
