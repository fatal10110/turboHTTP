# 2026-10 WebSocket protocol limits (#80, #81, #82)

Lane ws-protocol-limits (waves run w1030). Builds on ws-receive-send (#78/#79) and keeps its
receive-loop parking / keep-alive contract.

## What

- **#80 message limits.** `WebSocketFrameReader` takes `maxMessageSize` and rejects any data frame whose
  header announces more than MaxMessageSize (MessageTooLarge, close 1009) before the payload is rented
  or read. `MessageAssembler` also checks the single-frame FIN path (defence in depth).
  `DefaultMaxFrameSize` is now 4 MB (= `DefaultMaxMessageSize`, was 16 MB) and `Validate` requires
  `MaxFrameSize <= MaxMessageSize`.
- **#80 byte-bounded queues.** `BoundedAsyncQueue<T>` (AsyncBoundedQueue.cs) was rewritten around one
  lock: count and optional byte budget (size accessor, sizes stored per entry so a disposed message
  cannot skew the books). An empty queue admits any single item, so a message larger than the budget
  is still delivered (no deadlock) and the bytes held stay <= max(budget, MaxMessageSize).
  `EnqueueDropOldest` now drops as many oldest items as needed and hands each to a callback.
  `Complete` also wakes blocked writers (they used to wait for their token). New option
  `WebSocketConnectionOptions.ReceiveQueueMaxBytes` (default 8 MB), applied to the connection,
  `WebSocketClient` and `ResilientWebSocketClient` queues via `WebSocketMessageQueue.Create`.
- **#81 extension strictness.** The negotiator is always built (also with no configured extensions),
  and any negotiation failure (unoffered, duplicate, unparseable, rejected parameters) fails the
  connection with `WebSocketError.ExtensionNegotiationFailed` and close 1002, even when extensions are
  optional; the silent fallback is gone. "Required but none negotiated" keeps close 1010. Raw
  `options.Extensions` strings are no longer offered and `Validate` rejects non-blank entries.
- **#82 permessage-deflate.** Always offers `server_no_context_takeover` and requires it in the
  response; never offers `client_max_window_bits` and rejects a response containing it;
  `server_max_window_bits` up to the offered value is accepted (the 15-bit inflater handles any smaller
  window). `PerMessageDeflateOptions` throws for `serverNoContextTakeover: false` or
  `clientMaxWindowBits != 15`. The frame reader rejects RSV bits on control frames (1002) regardless of
  negotiated extensions.

## Files

Runtime/WebSocket: AsyncBoundedQueue.cs, WebSocketFrameReader.cs, MessageAssembler.cs,
WebSocketConstants.cs, WebSocketConnectionOptions.cs, WebSocketConnection.cs,
WebSocketConnection.Extensions.cs, WebSocketClient.cs, ResilientWebSocketClient.cs,
PerMessageDeflateExtension.cs, PerMessageDeflateOptions.cs.
Tests/Runtime/WebSocket: WebSocketMessageLimitTests.cs (new), WebSocketExtensionStrictnessTests.cs (new),
InMemoryWebSocketPeer.cs (`HandshakeExtensionsHeader`, `SendRawAsync`), WebSocketConnectionTests.cs
(optional-extension fallback test now expects failure).
Docs: Documentation~/Modules/WebSocket.md, Development/docs/phases/phase18/overview.md (limits table).

## Decisions

- Close code for a rejected server extension response is 1002 (the response violates RFC 6455 4.1/9.1);
  1010 stays reserved for "client required an extension the server did not negotiate" (RFC 6455 7.4.1).
- Byte budget is per queue (connection and client queue each), default two max-size messages.
- Raw `Extensions` strings are rejected in `Validate` instead of removing the public property (source
  compatibility; no implementation could honour a server accepting them).
- `ServerNoContextTakeover`/`ClientMaxWindowBits` properties stay but only accept the supported value,
  documented in XML docs.

## Deferred

- Autobahn 12.x/13.x interop (last #82 criterion): #217 (v1.0 GA). #82 is "Part of" in the PR.

## Verification

- `./Tools~/ci/check.sh`: OK.
- Out-of-Unity NUnit harness (scratch net8 project compiling the WebSocket test files against the shadow
  build DLLs, not part of the repo): 95/97 pass, including every new test. The two failures
  (`WebSocketReconnectionTests.ReceiveAllAsync_ReturnsFalse_WhenReconnectExhausted`,
  `WebSocketIntegrationTests.ServerInitiatedClose_RaisesOnClosed_AndReceiveFails`) fail identically
  when the same harness is built from origin/main sources, so they are pre-existing / harness-related.
- Not run: Unity EditMode/PlayMode (wave rule; host editor runs periodically), IL2CPP, devices.
  Scoped filter for the owner:
  `./Tools~/unity/run-tests.sh --platform EditMode --filter 'TurboHTTP\.Tests\.WebSocket\.(WebSocketFramingTests|WebSocketExtensionTests|WebSocketConnectionTests|WebSocketReceiveBackpressureTests|WebSocketMessageLimitTests|WebSocketExtensionStrictnessTests)'`
