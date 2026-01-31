# Phase 3.5: Tests & Integration

**Depends on:** 3.4 (all production code complete)
**Files to create:** 5 new test files + update CLAUDE.md

---

## Step 1: Core Unit Tests

**File:** `Tests/Runtime/Core/UHttpClientTests.cs`

```
Test cases:
- Constructor_WithNullOptions_UsesDefaults (null is allowed, creates default options)
- Constructor_WithDefaultOptions_Succeeds
- Get_ReturnsBuilder
- Post_ReturnsBuilder
- Put_ReturnsBuilder
- Delete_ReturnsBuilder
- Patch_ReturnsBuilder
- Head_ReturnsBuilder
- Options_ReturnsBuilder
- RequestBuilder_WithRelativeUrl_ResolvesAgainstBaseUrl
- RequestBuilder_WithAbsoluteUrl_IgnoresBaseUrl
- RequestBuilder_WithRelativeUrl_NoBaseUrl_ThrowsArgumentException
- RequestBuilder_MergesDefaultHeaders_WithRequestHeaders
- RequestBuilder_MultiValueHeaders_AllValuesCopied
- RequestBuilder_WithJsonBody_SetsContentTypeAndBody
- RequestBuilder_WithJsonBody_WithOptions_AcceptsJsonSerializerOptions
- RequestBuilder_WithTimeout_OverridesDefault
- RequestBuilder_WithBearerToken_SetsAuthorizationHeader
- ClientOptions_Clone_ProducesIndependentCopy
- Client_ImplementsIDisposable
- HttpTransportFactory_Default_ReturnsRawSocketTransport
- HttpTransportFactory_Default_CalledTwice_ReturnsSameInstance
- RequestBuilder_WithJsonBodyString_SetsContentTypeAndBody
- ClientOptions_SnapshotAtConstruction_MutationsDoNotAffectClient
- SendAsync_TransportThrowsUHttpException_NotDoubleWrapped (verify no re-wrapping — validates catch-order invariant)
- SendAsync_TransportThrowsIOException_WrappedInUHttpException (safety net path)
- Client_Dispose_DoesNotDisposeFactoryTransport (factory singleton survives client disposal)
- Client_Dispose_DisposesUserTransport_WhenDisposeTransportTrue
- Client_Dispose_DoesNotDisposeUserTransport_WhenDisposeTransportFalse (default)
- RequestBuilder_WithoutWithTimeout_UsesOptionsDefaultTimeout (not hardcoded 30s)
- SendAsync_RelativeUri_ThrowsUHttpException (absolute URI validation)
```

Uses a mock `IHttpTransport` to isolate Core from Transport.

---

## Step 2: HTTP/1.1 Serializer Tests

**File:** `Tests/Runtime/Transport/Http11SerializerTests.cs`

Uses `MemoryStream` to capture serialized output and verify wire format.

```
Test cases:
- SerializeGet_ProducesCorrectRequestLine
- SerializeGet_AutoAddsHostHeader
- SerializeGet_NonDefaultPort_IncludesPortInHost
- SerializeGet_UserSetHostHeader_Preserved
- SerializeGet_AutoAddsConnectionKeepAlive
- SerializeGet_UserSetConnectionHeader_NotOverridden
- SerializePost_AutoAddsContentLength
- SerializePost_WritesBody
- Serialize_MultiValueHeaders_EmitsSeparateLines
- Serialize_HostHeaderWithCRLF_ThrowsArgumentException
- Serialize_AnyHeaderValueWithCRLF_ThrowsArgumentException
- Serialize_HeaderNameWithColon_ThrowsArgumentException
- Serialize_HeaderNameWithCRLF_ThrowsArgumentException
- Serialize_NoBody_NoContentLength
- Serialize_UserSetContentLength_Mismatch_ThrowsArgumentException
- Serialize_UserSetContentLength_Correct_Preserved
- Serialize_AutoAddsUserAgent
- Serialize_UserSetUserAgent_NotOverridden
- Serialize_TransferEncodingSet_NoAutoContentLength (RFC 9110 §8.6 mutual exclusion)
- Serialize_TransferEncodingAny_WithBody_ThrowsArgumentException (any TE value, not just chunked)
- Serialize_EmptyHeaderName_ThrowsArgumentException
- Serialize_PathAndQuery_EmptyFallsBackToSlash
```

---

## Step 3: HTTP/1.1 Response Parser Tests

**File:** `Tests/Runtime/Transport/Http11ResponseParserTests.cs`

Uses `MemoryStream` with pre-built HTTP/1.1 response bytes.

```
Test cases:
- Parse_200OK_ContentLength_ReturnsBodyAndHeaders
- Parse_404NotFound_ReturnsStatusCode
- Parse_500InternalServerError_ReturnsStatusCode
- Parse_ChunkedBody_ReassemblesCorrectly
- Parse_ChunkedBody_WithTrailers_Completes (doesn't hang)
- Parse_ChunkedBody_InvalidHex_ThrowsFormatException
- Parse_ChunkedBody_EmptyChunks_Handled
- Parse_NoContentLength_NoChunked_ReadsToEnd
- Parse_HeadResponse_WithContentLength_SkipsBody (critical: no hang)
- Parse_204NoContent_SkipsBody
- Parse_304NotModified_SkipsBody
- Parse_100Continue_SkippedBeforeFinalResponse
- Parse_KeepAlive_HTTP11_Default_ReturnsTrue
- Parse_ConnectionClose_ReturnsFalse
- Parse_HTTP10_Default_ReturnsFalse
- Parse_MultipleSetCookieHeaders_AllPreserved (Add not Set)
- Parse_ReadLineAsync_ExceedsMaxLength_ThrowsFormatException
- Parse_TotalHeaderSize_ExceedsLimit_ThrowsFormatException
- Parse_TransferEncodingGzipChunked_ReturnsRawCompressedChunks
- Parse_TransferEncodingOnlyGzip_ThrowsNotSupportedException
- Parse_TransferEncoding_TakesPrecedenceOverContentLength
- Parse_MultipleContentLength_Conflicting_ThrowsFormatException
- Parse_MultipleContentLength_Same_Accepted
- Parse_EmptyBody_ReturnsEmptyArray
- Parse_TransferEncodingIdentity_TreatedAsAbsent
- Parse_StatusLine_NoReasonPhrase_Parses
- Parse_StatusLine_EmptyReasonPhrase_Parses
- Parse_StatusLine_MultiWordReasonPhrase_Parses
- Parse_ContentLength_ParsedAsLong (values > int.MaxValue rejected by MaxResponseBodySize, not by parse failure)
- Parse_ChunkedBody_LargeChunkSizeHex_ParsedAsLong
- Parse_1xxResponses_ExceedsMaxIterations_ThrowsFormatException
- Parse_ChunkedBody_WithExtensions_StripsExtensionsBeforeParsing
- Parse_ChunkedBody_ExceedsMaxBodySize_ThrowsIOException
- Parse_ContentLength_ExceedsMaxBodySize_ThrowsIOException
- Parse_ReadToEnd_ExceedsMaxBodySize_ThrowsIOException
- Parse_MultipleSetCookieHeaders_AllPreservedViaAdd
- Parse_ReadToEnd_ForcesKeepAliveFalse (no Content-Length, no chunked → KeepAlive must be false)
- Parse_ChunkedBody_SingleChunkExceedsMaxBodySize_ThrowsIOException (individual chunk > MaxResponseBodySize caught before int narrowing)
- Parse_ContentLength_NarrowedToInt_AfterMaxBodySizeCheck (long Content-Length safely narrowed to int after validation)
```

---

## Step 4: Connection Pool Tests

**File:** `Tests/Runtime/Transport/TcpConnectionPoolTests.cs`

```
Test cases:
- GetConnection_CreatesNewConnection (may need mock or test against localhost)
- ReturnConnection_ConnectionReused
- ReturnConnection_StaleConnection_Disposed
- Dispose_DrainsAllConnections
- MaxConnectionsPerHost_BlocksWhenAtLimit
- MaxConnectionsPerHost_WaitThenProceed (7th request waits, returns after 1 freed)
- DisposedPool_ReturnConnection_DisposesConnection
- CaseInsensitiveHostKey_SharesPool
- RawSocketTransport_StaleConnection_RetriesOnce (verify IsReused=true triggers retry)
- RawSocketTransport_FreshConnection_IOException_NoRetry (verify IsReused=false does not retry)
- PooledConnection_IsReused_FalseForNewConnection
- PooledConnection_IsReused_TrueAfterPoolDequeue
- ConnectionLease_Dispose_AlwaysReleasesSemaphore (even without ReturnToPool)
- ConnectionLease_ReturnToPool_ThenDispose_DoesNotDisposeConnection
- ConnectionLease_NoReturnToPool_ThenDispose_DisposesConnection
- ConnectionLease_ExceptionPath_SemaphoreReleased (simulate error after GetConnectionAsync)
- NonKeepAlive_Response_SemaphoreReleased (Connection: close does not leak permit)
- ConnectionLease_DoubleDispose_Idempotent (second Dispose is no-op)
- PooledConnection_IsAlive_AfterDispose_ReturnsFalse (guard against ObjectDisposedException)
- SemaphoreCapEviction_DrainsIdleConnections_BeforeRemoval (no socket leak)
- SemaphoreCapEviction_NeverEvictsCurrentKey
- ConnectionLease_Dispose_AfterPoolDispose_DoesNotThrow (ObjectDisposedException caught)
- ConnectionLease_ConcurrentReturnAndDispose_NoRace (lock prevents enqueue-of-disposed)
- GetConnectionAsync_AfterPoolDispose_ThrowsObjectDisposedException
- EnqueueConnection_AfterPoolDispose_DisposesConnection
- SemaphoreCapEviction_DoesNotDisposeSemaphores (only drains connections)
- PooledConnection_NegotiatedTlsVersion_SetAfterTlsHandshake
- RawSocketTransport_StaleConnection_NonIdempotent_NoRetry (POST on IsReused=true throws, does not retry)
- TlsStreamWrapper_WrapAsync_ReturnsTlsResult_WithNegotiatedProtocol
- TlsStreamWrapper_TlsBelowMinimum_ThrowsAuthenticationException (not SecurityException)
```

Note: Some tests may require a local TCP listener or mock. If testing against real sockets is impractical in Unity Test Runner, test the pool logic with mock streams and verify integration in Step 5.

**Note on httpbin.org dependency:** All integration tests hit `httpbin.org`. This service has had reliability issues and rate limits. Acceptable for Phase 3 manual integration tests, but Phase 9 (testing infrastructure) should stand up a local test server or Docker-based httpbin for CI.

---

## Step 5: Integration Test MonoBehaviour

**File:** `Tests/Runtime/TestHttpClient.cs`

Manual integration test — run in Unity Editor with a test scene.

```csharp
public class TestHttpClient : MonoBehaviour
{
    private UHttpClient _client;

    // Note: async void is unavoidable for MonoBehaviour Start().
    // Wrap entire body in try-catch to prevent silent exception swallowing.
    async void Start()
    {
        try
        {
            _client = new UHttpClient();

            await TestBasicGet();
            await TestPostJson();
            await TestPostJsonString();  // IL2CPP-safe overload
            await TestCustomHeaders();
            await TestTimeout();
            await TestConnectionReuse();
            await TestHeadRequest();
            await TestTlsVersion();
            await TestUnsupportedScheme();
            TestPlatformApiAvailability();  // Verify Latin1, SslStream overload, ModuleInitializer
            await MeasureLatencyBaseline();

            _client.Dispose();
            Debug.Log("=== All tests complete ===");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            Debug.LogError("=== TEST FAILURE ===");
        }
    }
}
```

### Test cases:

1. **TestBasicGet** — `GET https://httpbin.org/get`
   - Assert: status 200, body contains JSON, elapsed time logged

2. **TestPostJson** — `POST https://httpbin.org/post` with `WithJsonBody`
   - Assert: status 200, echoed body contains sent data

3. **TestCustomHeaders** — `GET https://httpbin.org/headers` with custom header + bearer token
   - Assert: status 200, response body shows custom headers

4. **TestTimeout** — `GET https://httpbin.org/delay/10` with `WithTimeout(TimeSpan.FromSeconds(2))`
   - Assert: throws `UHttpException` with `UHttpErrorType.Timeout`

5. **TestConnectionReuse** — 3 sequential GET requests to same host
   - Assert: all succeed; 2nd/3rd should be faster (no TCP+TLS handshake)

6. **TestHeadRequest** — `HEAD https://httpbin.org/get`
   - Assert: status 200, does NOT hang, body is null/empty

7. **TestTlsVersion** — Verify TLS 1.2+ was negotiated
   - Assert: negotiated protocol >= TLS 1.2 via `PooledConnection.NegotiatedTlsVersion` property (not just log). The post-handshake enforcement throws on TLS < 1.2, so a successful HTTPS request implicitly proves this. But explicit assertion via the stored property provides confidence.

8. **TestPostJsonString** — `POST https://httpbin.org/post` with `WithJsonBody("{\"key\":\"value\"}")`
   - Assert: status 200, echoed body contains sent data
   - Tests the IL2CPP-safe `WithJsonBody(string)` overload

9. **TestUnsupportedScheme** — Construct request with `ftp://` scheme
   - Assert: throws `UHttpException` with appropriate error message

10. **TestPlatformApiAvailability** — Synchronous verification of platform APIs:
    - Verify `Encoding.GetEncoding(28591)` succeeds (Latin-1 availability)
    - Log which SslStream overload is available (`SslClientAuthenticationOptions` or 4-arg fallback)
    - Verify `[ModuleInitializer]` fired: `HttpTransportFactory.Default` should return `RawSocketTransport` without explicit registration
    - Log results for each check. **On IL2CPP builds, failures here are critical blockers.**

11. **MeasureLatencyBaseline** — 5 sequential GET requests to `https://httpbin.org/get`
    - Log: average latency per request, total GC allocations (if measurable via `GC.GetTotalMemory`)
    - Purpose: establish Phase 3 baseline for Phase 10 comparison. No pass/fail threshold — informational only.

---

## Step 6: Update CLAUDE.md

Update:
- **Development Status:** Mark Phase 3 as COMPLETE, list all new files
- **Architecture:** Add notes about `UHttpClient`, `UHttpRequestBuilder`, `RawSocketTransport`, `TcpConnectionPool`, `TlsStreamWrapper`, `Http11RequestSerializer`, `Http11ResponseParser`
- **Key Technical Decisions:** Document error model, transport registration, IPv6 support, multi-value headers, timeout enforcement
- **Deferred Items:** List Phase 10 performance items, Phase 4 redirect middleware, Phase 3B HTTP/2

---

## Step 7: Post-Implementation Review

Per CLAUDE.md policy, run both specialist agents:

1. **unity-infrastructure-architect** — Review all new code for:
   - Assembly boundary compliance
   - Thread safety (pool, connection lifecycle)
   - Memory efficiency (allocations per request)
   - IL2CPP/AOT compatibility
   - Resource disposal chain

2. **unity-network-architect** — Review all new code for:
   - HTTP/1.1 protocol correctness (RFC 9110, RFC 9112)
   - TLS configuration correctness
   - Platform compatibility (iOS, Android, IL2CPP)
   - Connection pool keep-alive semantics
   - Error handling completeness

Fix any issues found, re-review until clean.

---

## Verification Checklist (End-to-End)

- [ ] All files compile in both assemblies (Core + Transport)
- [ ] No circular dependencies (Core does not reference Transport)
- [ ] `[ModuleInitializer]` in Transport auto-registers `RawSocketTransport` (no Unity bootstrap needed)
- [ ] Phase 2 tests still pass (no regressions)
- [ ] New unit tests pass (Core + Transport)
- [ ] Integration test: GET over HTTPS succeeds
- [ ] Integration test: POST with JSON body succeeds
- [ ] Integration test: Timeout actually fires
- [ ] Integration test: HEAD does not hang
- [ ] Integration test: Connection reuse works (keep-alive)
- [ ] Integration test: Non-keepalive response does NOT deadlock pool (semaphore released via ConnectionLease)
- [ ] TLS 1.2+ asserted via post-handshake check (SslProtocols.None + minimum enforcement)
- [ ] Header CRLF injection throws for all headers (not just Host)
- [ ] ReadToEnd forces KeepAlive=false
- [ ] Latin-1 encoding resolved via `Encoding.GetEncoding(28591)` with custom fallback (NOT `Encoding.Latin1`)
- [ ] SslStream overload probe works (logs which path is taken)
- [ ] Content-Length mismatch validated in serializer
- [ ] Transfer-Encoding takes precedence over Content-Length
- [ ] `gzip, chunked` accepted (returns raw compressed chunks)
- [ ] User-Agent auto-added unless user sets one
- [ ] ConnectionLease is a class with idempotent Dispose
- [ ] DNS timeout wrapper fires within ~5 seconds
- [ ] Semaphore cap eviction works when > 1000 entries (drains connections, does NOT dispose semaphores)
- [ ] Factory-provided transport NOT disposed by client Dispose()
- [ ] Absolute URI validation at transport entry
- [ ] Transfer-Encoding / Content-Length mutual exclusion in serializer
- [ ] Pool.GetConnectionAsync throws ObjectDisposedException after pool Dispose()
- [ ] Both specialist agent reviews pass
- [ ] CLAUDE.md updated
- [ ] `link.xml` present in `Runtime/Transport/` preserving SslStream, SslClientAuthenticationOptions, and codepage encodings
- [ ] `EncodingHelper.Latin1` shared between serializer and parser (no duplicate initialization)
- [ ] `ParsedResponse` is `internal class` (not `public`)
- [ ] `FormatException` from parser mapped to `UHttpException(NetworkError)` in transport
- [ ] `PooledConnection.IsReused` set correctly (false for new, true for dequeued)
- [ ] Retry-on-stale only fires when `IsReused == true` AND `request.Method.IsIdempotent()`
- [ ] Non-idempotent methods (POST, PATCH) on stale connections throw without retry
- [ ] `TlsStreamWrapper.WrapAsync` returns `TlsResult` struct (stream + negotiated protocol)
- [ ] TLS minimum enforcement throws `AuthenticationException` (not `SecurityException`)
- [ ] `PooledConnection.NegotiatedTlsVersion` set from `TlsResult.NegotiatedProtocol` in pool
- [ ] Chunk size validated against `MaxResponseBodySize` before `long`-to-`int` narrowing
- [ ] Content-Length validated against `MaxResponseBodySize` before `long`-to-`int` narrowing
- [ ] **MANDATORY** IL2CPP smoke test: build for iOS simulator or Android emulator, verify basic HTTPS GET completes. Also verify: `[ModuleInitializer]` fires, `Encoding.GetEncoding(28591)` works, SslStream handshake succeeds, **byte-by-byte SslStream reads work correctly with multi-header responses** (not just minimal responses — validate that SslStream internal buffering handles the 16KB-record-per-byte-read pattern under IL2CPP). The SslStream ALPN risk (CLAUDE.md Critical Risk Area #1) makes this a blocking requirement for Phase 3 completion. If device testing is not possible, document explicitly as deferred risk with rationale.
