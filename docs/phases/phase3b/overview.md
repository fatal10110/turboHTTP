# Phase 3B Implementation Plan — Overview

Phase 3B implements HTTP/2 protocol support on top of the existing raw socket + TLS infrastructure from Phase 3. All new code goes under `Runtime/Transport/Http2/`. Two existing files are modified.

## Step Index

| Step | Name | Files | Depends On |
|---|---|---|---|
| [3B.1](step-3b.1-http2-frame.md) | Frame Types & Constants | 1 new | — |
| [3B.2](step-3b.2-http2-frame-codec.md) | Frame Reader/Writer | 1 new | 3B.1 |
| [3B.3](step-3b.3-hpack-static-table.md) | HPACK Static Table | 1 new | — |
| [3B.4](step-3b.4-hpack-huffman.md) | HPACK Huffman Coding | 1 new | — |
| [3B.5](step-3b.5-hpack-integer-codec.md) | HPACK Integer Encoding | 1 new | — |
| [3B.6](step-3b.6-hpack-dynamic-table.md) | HPACK Dynamic Table | 1 new | 3B.3 |
| [3B.7](step-3b.7-hpack-encoder.md) | HPACK Encoder | 1 new | 3B.3, 3B.4, 3B.5, 3B.6 |
| [3B.8](step-3b.8-hpack-decoder.md) | HPACK Decoder | 1 new | 3B.3, 3B.4, 3B.5, 3B.6 |
| [3B.9](step-3b.9-http2-settings.md) | Connection Settings | 1 new | 3B.1 |
| [3B.10](step-3b.10-http2-stream.md) | Stream State Machine | 1 new | 3B.1, 3B.9 |
| [3B.11](step-3b.11-pooled-connection-changes.md) | PooledConnection & ConnectionLease Changes | 1 modified | — |
| [3B.12](step-3b.12-http2-connection.md) | HTTP/2 Connection (Critical Path) | 1 new | 3B.1–3B.10 |
| [3B.13](step-3b.13-http2-connection-manager.md) | Per-Host Connection Cache | 1 new | 3B.12 |
| [3B.14](step-3b.14-raw-socket-transport-routing.md) | RawSocketTransport Protocol Routing | 1 modified | 3B.11, 3B.12, 3B.13 |
| [3B.15](step-3b.15-tests.md) | Unit & Integration Tests | 8 new | 3B.1–3B.14 |

## Dependency Graph

```
No dependencies (parallel):
    ├── 3B.1 Http2Frame
    ├── 3B.3 HpackStaticTable
    ├── 3B.4 HpackHuffman
    ├── 3B.5 HpackIntegerCodec
    └── 3B.11 PooledConnection changes

Layer 2:
    ├── 3B.2 Http2FrameCodec       ← 3B.1
    ├── 3B.6 HpackDynamicTable     ← 3B.3
    └── 3B.9 Http2Settings         ← 3B.1

Layer 3:
    ├── 3B.7 HpackEncoder          ← 3B.3, 3B.4, 3B.5, 3B.6
    ├── 3B.8 HpackDecoder          ← 3B.3, 3B.4, 3B.5, 3B.6
    └── 3B.10 Http2Stream          ← 3B.1, 3B.9

Layer 4 (Critical Path):
    └── 3B.12 Http2Connection      ← ALL above

Layer 5:
    └── 3B.13 Http2ConnectionManager ← 3B.12

Layer 6:
    └── 3B.14 RawSocketTransport   ← 3B.11, 3B.12, 3B.13

Layer 7:
    └── 3B.15 Tests                ← ALL above
```

Steps in Layer 1 have no inter-dependencies and can be implemented in parallel. Layer 2 and 3 similarly have parallelism opportunities. Step 3B.12 (Http2Connection) is the critical path bottleneck — it depends on everything and is the largest file.

## New Directory Structure

```
Runtime/Transport/Http2/
    Http2Frame.cs              — Step 3B.1
    Http2FrameCodec.cs         — Step 3B.2
    HpackStaticTable.cs        — Step 3B.3
    HpackHuffman.cs            — Step 3B.4
    HpackIntegerCodec.cs       — Step 3B.5
    HpackDynamicTable.cs       — Step 3B.6
    HpackEncoder.cs            — Step 3B.7
    HpackDecoder.cs            — Step 3B.8
    Http2Settings.cs           — Step 3B.9
    Http2Stream.cs             — Step 3B.10
    Http2Connection.cs         — Step 3B.12
    Http2ConnectionManager.cs  — Step 3B.13
```

## Modified Files

| File | Step | Changes |
|------|------|---------|
| `Runtime/Transport/Tcp/TcpConnectionPool.cs` | 3B.11 | Add `NegotiatedAlpnProtocol` to `PooledConnection`, `TransferOwnership()` to `ConnectionLease`, pass ALPN protocols to TLS |
| `Runtime/Transport/RawSocketTransport.cs` | 3B.14 | ALPN-based protocol routing, h2 connection reuse, fallback to HTTP/1.1 |

## Exclusions (NOT Implemented in Phase 3B)

- **PUSH_PROMISE (Server Push):** Reject with RST_STREAM(REFUSED_STREAM). Send ENABLE_PUSH=0.
- **h2c (Cleartext HTTP/2):** No upgrade mechanism. h2 only via TLS+ALPN.
- **Priority/Weight (RFC 7540 Section 5.3):** Ignore PRIORITY frames. Deprecated by RFC 9113.
- **Padding generation:** Never generate padded frames. Handle incoming padded frames correctly.
- **Trailing headers:** Decode but don't expose in UHttpResponse (deferred).
- **CONNECT method tunneling:** Not needed.
