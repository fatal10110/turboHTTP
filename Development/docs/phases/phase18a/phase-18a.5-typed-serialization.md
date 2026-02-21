# Phase 18a.5: Typed Message Serialization

**Depends on:** Phase 18
**Assembly:** `TurboHTTP.WebSocket`
**Files:** 3 new, 1 modified
**Estimated Effort:** 3-4 days

---

## Motivation

Phase 18 overview §19 states "sub-protocol dispatch is out of scope — message handling is the application's responsibility." In practice, every WebSocket consumer needs to serialize/deserialize messages. Providing a lightweight typed layer eliminates boilerplate while remaining unopinionated about serialization format.

---

## Step 1: Define Typed Message Interface

**File:** `Runtime/WebSocket/IWebSocketMessageSerializer.cs` (new)

Required behavior:

1. Define `IWebSocketMessageSerializer<T>` interface:
   - `Serialize(T message)` → `ReadOnlyMemory<byte>` — always produce UTF-8 bytes directly (for text sub-protocols this avoids the double-copy of string → UTF-8 encode → compress; the serializer writes UTF-8 bytes, the send path uses them directly as a binary-opcode or text-opcode frame payload).
   - `Deserialize(WebSocketMessage raw)` → `T`.
   - `MessageType` — `WebSocketOpcode.Text` or `WebSocketOpcode.Binary` (determines which opcode is used for `SendAsync`).
2. Built-in implementations:
   - `JsonWebSocketSerializer<T>` — uses the existing JSON infrastructure.
   - `RawStringSerializer` — passthrough for plain text messages.

Implementation constraints:

1. `Serialize` returns `ReadOnlyMemory<byte>` (UTF-8 bytes), not `string`. This eliminates the double payload copy (serialize → string → UTF-8 encode) that would occur if the serializer returned `string`. The send path can write these bytes directly as frame payload.

---

## Step 2: Add Typed Send/Receive Extensions

**File:** `Runtime/WebSocket/WebSocketClientExtensions.cs` (new)

Required behavior:

1. Extension methods on `IWebSocketClient`:
   - `SendAsync<T>(T message, IWebSocketMessageSerializer<T> serializer, CancellationToken ct)`.
   - `ReceiveAsync<T>(IWebSocketMessageSerializer<T> serializer, CancellationToken ct)` → `T`.
   - `ReceiveAllAsync<T>(IWebSocketMessageSerializer<T> serializer, CancellationToken ct)` → `IAsyncEnumerable<T>` (depends on 18a.2).
2. These are convenience extensions — they don't add new protocol capabilities, just reduce boilerplate.

---

## Step 3: Add JSON Serializer Implementation

**File:** `Runtime/WebSocket/JsonWebSocketSerializer.cs` (new)

Required behavior:

1. Implement `IWebSocketMessageSerializer<T>` using the project's existing JSON infrastructure.
2. Configurable: custom serializer settings, naming policy, etc.
3. Error handling: deserialization failures throw `WebSocketException` with `SerializationFailed` error code (not `ProtocolViolation` — deserialization is an application-layer concern, not a protocol violation). Add `SerializationFailed` to `WebSocketError` enum.
4. **IL2CPP constraint:** generic types parameterized with user types need `link.xml` entries or `[Preserve]` attributes to prevent stripping. Document: constrain to `where T : class` for v1, or add a `[Preserve]` annotation requirement in the usage documentation. Include explicit `link.xml` guidance for common serialization target types.

---

## Verification Criteria

1. JSON round-trip: typed send → typed receive with complex object.
2. Serializer always produces `ReadOnlyMemory<byte>` (UTF-8 bytes), not `string`.
3. Deserialization error wraps as `WebSocketException` with `SerializationFailed` (not `ProtocolViolation`).
4. Raw string serializer passthrough.
5. `where T : class` constraint enforced by compiler.
