# Phase 14.7: Mock Server for Testing

**Depends on:** Phase 7
**Assembly:** `TurboHTTP.Testing`, `TurboHTTP.Tests.Runtime`
**Files:** 4 new, 1 modified

---

## Step 1: Build In-Process Mock Server Core

**Files:**
- `Runtime/Testing/MockHttpServer.cs` (new)
- `Runtime/Testing/MockRoute.cs` (new)

### Technical Spec

Server design:

1. In-process transport-backed server (no OS port binding required for default mode).
2. Optional local-loopback mode for external client compatibility tests.
3. Deterministic request dispatcher using route table snapshot per request.

Route model:

```csharp
public sealed class MockRoute
{
    public HttpMethod Method { get; init; }
    public string PathPattern { get; init; }
    public Func<MockRequestContext, ValueTask<MockResponse>> Handler { get; init; }
    public int? RemainingInvocations { get; init; }
    public int Priority { get; init; } = 0;
}
```

Matching semantics:

1. Match by method first, then path.
2. Path supports exact, wildcard segment, and regex mode.
3. Highest priority route wins; ties resolve by registration order.
4. One-shot routes decrement atomically and retire when exhausted.

History capture:

1. Record inbound request snapshot with headers/body/timestamp/route id.
2. Capture response snapshot and duration.
3. Store bounded history ring buffer with configurable capacity.

### Implementation Constraints

1. Route registration and dispatch must be thread-safe under parallel tests.
2. No hidden global singleton state across fixtures.
3. Handler exceptions map to deterministic 5xx mock error responses unless configured otherwise.
4. Body buffering policy must support large payload tests with size caps.

---

## Step 2: Add Fluent Test Helper API

**Files:**
- `Runtime/Testing/MockResponseBuilder.cs` (new)
- `Tests/Runtime/Integration/IntegrationTests.cs` (modify)

### Technical Spec

Fluent API requirements:

```csharp
mockServer
    .When(HttpMethod.Get, "/api/users/{id}")
    .WithHeader("Authorization", value => value.StartsWith("Bearer "))
    .WithJsonBody<UserRequest>(predicate)
    .Respond(response => response
        .Status(200)
        .Json(new UserDto(...))
        .Header("X-Mock", "1"))
    .Times(1);
```

Capabilities:

1. Header matcher, query matcher, JSON body matcher, raw body matcher.
2. Latency injection (`Delay`), fault injection (`Abort`, `Timeout`), and sequence responses.
3. Stream-shaping controls for response body delivery:
   - fixed chunk size;
   - variable chunk size range;
   - inter-chunk delay/jitter;
   - forced boundary splits (for example inside CRLF delimiters and UTF-8 multibyte sequences).
4. Assertion helpers:
   - `AssertReceived(path, count)`;
   - `AssertLastRequest(...)`;
   - `AssertNoUnexpectedRequests()`.

### Implementation Constraints

1. Builder APIs must produce readable failure messages with diff-style details.
2. Default serialization for JSON matchers must align with runtime serializer settings.
3. Sequence and one-shot routes must be race-safe under concurrent calls.
4. Default test mode should avoid unrealistic "single full byte-array instant return"; chunked delivery must be first-class and easy to enable.

---

## Step 3: Add Deterministic Mock Server Tests

**File:** `Tests/Runtime/Testing/MockHttpServerTests.cs` (new)

### Required Test Matrix

| Case | Setup | Expected Result |
|---|---|---|
| `RoutePriority_HighestWins` | overlapping routes | highest priority route selected |
| `OneShotRoute_Expires` | `Times(1)` route hit twice | second call fails or falls through by policy |
| `SequenceResponses_Ordered` | sequence of 3 responses | call order preserved |
| `HeaderBodyMatchers_FilterCorrectly` | mixed requests | only matching requests handled |
| `InjectedDelay_RespectsCancellation` | response delay + cancel token | deterministic cancellation |
| `ChunkedDelivery_ReassemblesCorrectly` | chunk profile enabled | parser receives fragmented stream and reconstructs response correctly |
| `ChunkBoundary_SensitiveSplitCoverage` | split on protocol/text boundaries | no parser corruption on CRLF or multibyte boundary splits |
| `HistoryBounded_OldestEvicted` | capacity exceeded | oldest entries removed first |
| `ParallelRequests_NoStateCorruption` | concurrent requests | consistent history and route counters |

---

## Verification Criteria

1. Mock server delivers deterministic, composable test behavior without external network dependency.
2. Matcher and response-builder APIs cover common integration test needs.
3. Chunked stream simulation is sufficient to catch buffering and parser-boundary bugs, not just instant-buffer scenarios.
4. Concurrency and history behavior remain stable under stress.
5. Integration suite can replace ad-hoc live endpoint usage with mock server scenarios.
