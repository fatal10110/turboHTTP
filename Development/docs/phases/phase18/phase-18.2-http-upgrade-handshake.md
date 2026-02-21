# Phase 18.2: HTTP Upgrade Handshake

**Depends on:** Phase 18.1 (WebSocket constants and key computation)
**Assembly:** `TurboHTTP.WebSocket`
**Files:** 2 new

---

## Step 1: Implement Client Handshake Request Builder

**File:** `Runtime/WebSocket/WebSocketHandshake.cs` (new)

Required behavior:

1. Construct a valid HTTP/1.1 upgrade request per RFC 6455 Section 4.1:
   - Method: `GET`
   - `Host` header (from URI, with port inclusion rules — see constraint 6)
   - `Upgrade: websocket`
   - `Connection: Upgrade`
   - `Sec-WebSocket-Key: <base64-encoded 16-byte nonce>`
   - `Sec-WebSocket-Version: 13`
2. Support optional `Sec-WebSocket-Protocol` header for sub-protocol negotiation (comma-separated list of requested protocols).
3. Support optional `Sec-WebSocket-Extensions` header for extension negotiation (extensibility hook — no built-in extension implementations in Phase 18).
4. Support custom headers for authentication or other application needs (e.g., `Authorization`, `Cookie`).
5. Support both `ws://` and `wss://` URI schemes — derive host, port, and resource name from URI.
6. Default port: 80 for `ws://`, 443 for `wss://`. **`Host` header port rules:** omit port for default ports (ws:80, wss:443), include port for non-default ports (RFC 6455 Section 4.1).
7. **Request-URI construction:** resource name is path + query only. **URI fragments MUST be stripped** — they are never sent to the server (RFC 6455 Section 3).
8. Generate and store the `Sec-WebSocket-Key` for server response validation.
9. **Handshake timeout** is separate from data transfer timeouts. Default: 10s. Exposed as `WebSocketConnectionOptions.HandshakeTimeout`.

Implementation constraints:

1. Handshake request must be serialized as raw HTTP/1.1 wire format (reuse patterns from `Http11RequestSerializer` but do not depend on it directly).
2. URI parsing must handle standard WebSocket URIs including path and query string. **Fragment identifiers must be stripped before constructing the Request-URI.**
3. Key generation must use `WebSocketConstants.GenerateClientKey()`.
4. Header values must be validated for CRLF injection (consistent with existing HTTP/1.1 header validation).
5. Request must be written to stream in a single buffered write where possible.
6. **WSS connections must pass empty/null ALPN protocols** to `ITlsProvider.WrapAsync` to prevent accidental HTTP/2 negotiation on the TLS layer. WebSocket uses HTTP/1.1 Upgrade, not ALPN.
7. Certificate validation for WSS uses the same `ITlsProvider` certificate validation callbacks as HTTP connections. Certificate pinning is configurable via `WebSocketConnectionOptions.TlsProvider`.

---

## Step 2: Implement Server Response Validator

**File:** `Runtime/WebSocket/WebSocketHandshakeValidator.cs` (new)

Required behavior:

1. Read the HTTP/1.1 response from the server stream after sending the upgrade request.
2. Validate the response status is `101 Switching Protocols`.
3. Validate required response headers using **token-based parsing** (not full string equality):
   - `Upgrade` header: must contain `websocket` as a case-insensitive token (value may be comma-separated token list).
   - `Connection` header: must contain `Upgrade` as a case-insensitive token (server may respond with `Connection: keep-alive, Upgrade`).
   - `Sec-WebSocket-Accept: <expected-value>` — computed from the client key using `WebSocketConstants.ComputeAcceptKey()`.
4. Parse optional `Sec-WebSocket-Protocol` response header — server must select exactly one of the client's requested protocols (or omit if no protocol was requested).
5. Parse optional `Sec-WebSocket-Extensions` response header — store negotiated extensions for connection metadata.
6. Reject the handshake with specific error information if:
   - Status code is not 101 (include actual status code and reason in error).
   - `Sec-WebSocket-Accept` value does not match expected value (potential MITM or misconfigured proxy).
   - Required headers are missing or malformed.
   - Server selected a sub-protocol not in the client's requested list.
7. Return a `WebSocketHandshakeResult` containing: success/failure, negotiated sub-protocol, negotiated extensions, server response headers.

Implementation constraints:

1. Response parsing must handle partial reads (stream may deliver headers incrementally).
2. Response parsing must enforce a maximum header size limit (default 8KB) to prevent memory exhaustion from malicious servers. Enforce **before** allocation using bounded read pattern: `Stream.ReadAsync(buffer, 0, Math.Min(remaining, bufferSize))`.
3. Header parsing must be case-insensitive for header names per HTTP/1.1 semantics.
4. Accept key validation: the `Sec-WebSocket-Accept` value is derived from public values (client nonce + fixed GUID) — constant-time comparison is not security-critical here. Use `CryptographicOperations.FixedTimeEquals` if available for defense-in-depth, but standard `SequenceEqual` is acceptable.
5. Non-101 responses must preserve the response body (if any) for error diagnostics — bounded read (max 4KB), enforced before allocation.
6. Handshake timeout must be configurable (default 10s) and enforced via `CancellationToken`.

---

## Verification Criteria

1. Handshake request wire format matches RFC 6455 Section 4.1 examples exactly.
2. Successful handshake with a compliant server produces a valid `WebSocketHandshakeResult`.
3. Accept key validation rejects incorrect values (e.g., tampered key, wrong GUID).
4. Non-101 responses are rejected with descriptive error including status code.
5. Missing or malformed required headers are rejected with specific error details.
6. Sub-protocol negotiation works: client requests ["chat", "json"], server selects "json".
7. Custom headers (Authorization, Cookie) are included in the upgrade request.
8. Both `ws://` and `wss://` URI schemes produce correct Host header and port handling.
9. URI fragments are stripped from Request-URI (e.g., `ws://host/path#frag` sends `/path`).
10. Token-based header validation: `Connection: keep-alive, Upgrade` is accepted.
11. WSS connections pass empty ALPN protocols to TLS provider.
12. Non-default ports are included in Host header (e.g., `Host: example.com:8080`).
