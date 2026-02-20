# Phase 21: gRPC Client

**Milestone:** v2.0
**Dependencies:** Phase 3B (HTTP/2), Phase 3C (TLS), Phase 19 (Async Runtime Refactor), Phase 20 Task 20.5 (Protobuf Handler)
**Estimated Complexity:** Very High
**Estimated Effort:** 5-7 weeks
**Critical:** No - v2.0 future feature

## Overview

Add gRPC protocol support over the existing HTTP/2 transport. gRPC is a binary RPC framework using Protocol Buffers for serialization and HTTP/2 for transport. It supports four call patterns: unary, server-streaming, client-streaming, and bidirectional streaming. This is the largest single feature in the roadmap and is positioned as a v2.0 differentiator.

## Challenges

- Protobuf compiler integration and IL2CPP compatibility for generated code
- HTTP/2 trailer-based status propagation (gRPC uses trailers extensively)
- Streaming lifecycle management (client-streaming, bi-directional)
- Deadline propagation and cancellation across frames
- Interceptor/middleware pattern for gRPC-specific concerns

## Architecture

```
gRPC Layer
├── GrpcChannel              ← Connection management, load balancing
├── GrpcCall<TReq, TRes>     ← Single RPC call abstraction
├── GrpcInterceptor          ← Middleware for gRPC (auth, logging, retry)
└── GrpcProtobufMarshaller   ← Serialization bridge (reuses Phase 20.5)

HTTP/2 Transport (Phase 3B)
├── Http2Connection           ← Existing multiplexed streams
└── Http2Stream               ← Per-RPC stream with trailer support
```

## Tasks

### Task 21.1: gRPC Framing & Message Encoding

**Goal:** Implement the gRPC wire format (length-prefixed protobuf messages over HTTP/2 DATA frames).

**Deliverables:**
- gRPC message framing: `[compressed-flag (1 byte)] [message-length (4 bytes)] [message]`
- Message encoder/decoder using `Google.Protobuf` marshallers
- Compression support flag (identity, gzip)
- IL2CPP AOT-safe: no runtime reflection in serialization path

**Estimated Effort:** 1 week

---

### Task 21.2: gRPC Channel & Call Abstraction

**Goal:** Core abstractions for making gRPC calls.

**Deliverables:**
- `GrpcChannel` — wraps an HTTP/2 connection (or pool), manages lifecycle
- `GrpcMethodDescriptor<TReq, TRes>` — describes a single RPC method (service name, method name, marshaller)
- `GrpcCall<TReq, TRes>` — represents an in-flight call; handles headers, trailers, status
- Factory: `channel.CreateCall(method)` returns a configured call object

**Estimated Effort:** 1 week

---

### Task 21.3: Unary & Server-Streaming RPCs

**Goal:** Support the two most common gRPC call patterns.

**Deliverables:**
- **Unary:** `var response = await call.UnaryAsync(request)`
- **Server-streaming:** `await foreach (var item in call.ServerStreamingAsync(request))`
- Proper gRPC status code extraction from HTTP/2 trailers (`grpc-status`, `grpc-message`)
- Deadline propagation via `grpc-timeout` header
- Cancellation support

**Estimated Effort:** 1 week

---

### Task 21.4: Client-Streaming & Bidirectional RPCs

**Goal:** Support the two advanced gRPC call patterns.

**Deliverables:**
- **Client-streaming:** `await call.ClientStreamingAsync(requestStream)`
- **Bidirectional:** full-duplex send/receive over a single HTTP/2 stream
- Back-pressure handling (flow control integration with HTTP/2 WINDOW_UPDATE)
- Graceful stream completion (half-close)

**Estimated Effort:** 1-2 weeks

---

### Task 21.5: gRPC Interceptors

**Goal:** Middleware pattern for cross-cutting gRPC concerns.

**Deliverables:**
- `IGrpcInterceptor` interface
- Built-in interceptors:
  - `GrpcAuthInterceptor` — injects bearer tokens
  - `GrpcLoggingInterceptor` — logs RPC calls, status, and latency
  - `GrpcRetryInterceptor` — retries on transient gRPC status codes
- Interceptor chain runs before/after each RPC

**Estimated Effort:** 1 week

---

### Task 21.6: gRPC Metadata, Status Codes & Error Handling

**Goal:** Full gRPC status/metadata support.

**Deliverables:**
- `GrpcStatus` enum mapping all standard gRPC codes (OK, Cancelled, InvalidArgument, etc.)
- `GrpcMetadata` — typed key/value pairs for request/response metadata (headers + trailers)
- `GrpcException` with status code, message, and trailing metadata
- Mapping from HTTP/2 errors to gRPC status codes

**Estimated Effort:** 3-4 days

---

### Task 21.7: Test Suite

**Goal:** Comprehensive tests covering protocol correctness and IL2CPP safety.

**Deliverables:**
- Unit tests for message framing and encoding
- Integration tests against a local gRPC test server (e.g., grpc-dotnet interop)
- All four call patterns tested (unary, server-streaming, client-streaming, bidi)
- Interceptor chain tests
- Deadline and cancellation tests
- IL2CPP AOT build validation

**Estimated Effort:** 1 week

---

## Prioritization Matrix

| Task | Priority | Effort | Dependencies |
|------|----------|--------|--------------|
| 21.1 Framing | Highest | 1w | Phase 20.5 (Protobuf) |
| 21.2 Channel/Call | Highest | 1w | 21.1, Phase 3B |
| 21.3 Unary/Server-Stream | Highest | 1w | 21.2 |
| 21.4 Client/Bidi-Stream | High | 1-2w | 21.2 |
| 21.5 Interceptors | Medium | 1w | 21.2 |
| 21.6 Metadata/Status | High | 3-4d | 21.3 |
| 21.7 Test Suite | High | 1w | All above |

## Verification Plan

1. Unary RPC roundtrip against a standard gRPC server (grpc-dotnet or Go).
2. Server-streaming: receive 1000+ messages without data loss.
3. Bidirectional: ping-pong echo test with concurrent send/receive.
4. Deadline: verify `DEADLINE_EXCEEDED` status after configured timeout.
5. IL2CPP AOT build and execution on iOS and Android.
6. Interceptor chain ordering verified.

## Notes

- This phase should only begin after Phase 19 (Async Refactor) is complete to benefit from `ValueTask`-first paths.
- Phase 20 Task 20.5 (Protobuf Handler) is a direct prerequisite — the protobuf marshalling layer is shared.
- Consider whether to support `grpc-web` for browser compatibility (could be a Task 21.8 if needed).
- gRPC reflection and health checking are out of scope for v2.0; they can be added later.
