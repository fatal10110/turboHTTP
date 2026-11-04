# Phase 5.3: JSON Extensions

**Depends on:** Phase 5.1 (UHttpResponse Content Helpers), Phase 5.2 (Content Type Constants), Phase 3D (JSON Abstraction)
**Assembly:** `TurboHTTP.Core`
**Files:** 1 new

---

## Step 1: Response JSON Extensions

**File:** `Runtime/Core/JsonExtensions.cs`
**Namespace:** `TurboHTTP.Core`

Add response extensions:

```csharp
public static T AsJson<T>(this UHttpResponse response)
public static T AsJson<T>(this UHttpResponse response, IJsonSerializer serializer)
public static bool TryAsJson<T>(this UHttpResponse response, out T result)
```

Behavior:

1. Throw `ArgumentNullException` for null `response` or null `serializer`.
2. Return `default(T)` for null/empty response body.
3. Use `TurboHTTP.JSON.JsonSerializer` by default.
4. Catch parse failures in `TryAsJson` and return `false`.

---

## Step 2: Client JSON Convenience Methods

**File:** `Runtime/Core/JsonExtensions.cs`

Add request/response helpers on `UHttpClient`:

```csharp
public static Task<T> GetJsonAsync<T>(this UHttpClient client, string url, CancellationToken cancellationToken = default)
public static Task<TResponse> PostJsonAsync<TRequest, TResponse>(this UHttpClient client, string url, TRequest data, CancellationToken cancellationToken = default)
public static Task<TResponse> PutJsonAsync<TRequest, TResponse>(this UHttpClient client, string url, TRequest data, CancellationToken cancellationToken = default)
public static Task<TResponse> PatchJsonAsync<TRequest, TResponse>(this UHttpClient client, string url, TRequest data, CancellationToken cancellationToken = default)
public static Task<T> DeleteJsonAsync<T>(this UHttpClient client, string url, CancellationToken cancellationToken = default)
```

Required flow:

1. Build request with fluent API (`Get`, `Post`, `Put`, `Patch`, `Delete`).
2. Set `Accept(ContentTypes.Json)` on requests expecting JSON responses.
3. For write operations, serialize request via `.WithJsonBody(data)`.
4. Call `EnsureSuccessStatusCode()` before deserialization.

---

## Step 3: Error and Compatibility Semantics

1. Non-success status must throw `UHttpException` through `EnsureSuccessStatusCode()`.
2. Invalid JSON must throw `JsonSerializationException` in `AsJson`.
3. JSON pipeline must remain serializer-agnostic (`IJsonSerializer` compatible).

---

## Verification Criteria

1. `AsJson` deserializes valid payloads and returns default for empty payloads.
2. `TryAsJson` returns false on invalid JSON without throwing.
3. `GetJsonAsync`/`PostJsonAsync` issue requests and deserialize responses.
4. Non-2xx responses throw from convenience methods.
5. New methods compile without direct dependency on `System.Text.Json`.
