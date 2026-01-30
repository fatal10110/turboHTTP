# Phase 3.5: Tests & Integration

**Depends on:** 3.4 (all production code complete)
**Files to create:** 5 new test files + update CLAUDE.md

---

## Step 1: Core Unit Tests

**File:** `Tests/Runtime/Core/UHttpClientTests.cs`

```
Test cases:
- Constructor_WithNullOptions_ThrowsArgumentNullException
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
- Serialize_NoBody_NoContentLength
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
- Parse_TransferEncodingGzipChunked_ThrowsNotSupportedException
- Parse_EmptyBody_ReturnsEmptyArray
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
- RawSocketTransport_StaleConnection_RetriesOnce
```

Note: Some tests may require a local TCP listener or mock. If testing against real sockets is impractical in Unity Test Runner, test the pool logic with mock streams and verify integration in Step 5.

---

## Step 5: Integration Test MonoBehaviour

**File:** `Tests/Runtime/TestHttpClient.cs`

Manual integration test — run in Unity Editor with a test scene.

```csharp
public class TestHttpClient : MonoBehaviour
{
    private UHttpClient _client;

    async void Start()
    {
        _client = new UHttpClient();

        await TestBasicGet();
        await TestPostJson();
        await TestCustomHeaders();
        await TestTimeout();
        await TestConnectionReuse();
        await TestHeadRequest();
        await TestTlsVersion();

        _client.Dispose();
        Debug.Log("=== All tests complete ===");
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
   - Assert: negotiated protocol >= TLS 1.2 (not just log)

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
- [ ] Phase 2 tests still pass (no regressions)
- [ ] New unit tests pass (Core + Transport)
- [ ] Integration test: GET over HTTPS succeeds
- [ ] Integration test: POST with JSON body succeeds
- [ ] Integration test: Timeout actually fires
- [ ] Integration test: HEAD does not hang
- [ ] Integration test: Connection reuse works (keep-alive)
- [ ] TLS 1.2+ asserted (not just logged)
- [ ] Both specialist agent reviews pass
- [ ] CLAUDE.md updated
- [ ] IL2CPP smoke test (if possible): build for iOS sim or Android, verify basic HTTPS GET
