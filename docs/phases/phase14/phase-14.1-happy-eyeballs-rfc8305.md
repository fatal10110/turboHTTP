# Phase 14.1: Happy Eyeballs (RFC 8305)

**Depends on:** Phase 12
**Assembly:** `TurboHTTP.Transport`, `TurboHTTP.Tests.Runtime`
**Files:** 3 new, 1 modified

---

## Step 1: Implement Dual-Stack Connection Racing

**Files:**
- `Runtime/Transport/Connection/HappyEyeballsConnector.cs` (new)
- `Runtime/Transport/Connection/HappyEyeballsOptions.cs` (new)
- `Runtime/Transport/RawSocketTransport.cs` (modify)

### Technical Spec

Primary entry point:

```csharp
public static Task<Socket> ConnectAsync(
    string host,
    int port,
    AddressFamily? requestedFamily,
    TimeSpan connectTimeout,
    HappyEyeballsOptions options,
    CancellationToken cancellationToken);
```

`HappyEyeballsOptions` required fields:

1. `TimeSpan FamilyStaggerDelay` default `250ms`.
2. `TimeSpan AttemptSpacingDelay` default `125ms` between attempts within same family queue.
3. `int MaxConcurrentAttempts` default `2`.
4. `bool PreferIpv6` default `true`.
5. `bool Enable` default `true`.

Deterministic family partitioning:

1. Resolve addresses once at start.
2. Partition into `ipv6[]` then `ipv4[]`.
3. Preserve resolver order inside each partition.
4. Build a scheduled attempt queue that interleaves families based on `FamilyStaggerDelay`.

Connection race algorithm:

1. Start attempt `A0` at `t0` from preferred family.
2. Start first attempt from alternate family at `t0 + FamilyStaggerDelay` if no winner yet.
3. Continue attempts in each family with `AttemptSpacingDelay`.
4. Stop scheduling new attempts after first successful `Socket.ConnectAsync`.
5. Cancel all outstanding attempts via linked CTS.
6. Dispose all non-winning sockets, including sockets that complete after cancellation.

Timeout and cancellation rules:

1. External cancellation token cancels all attempts immediately.
2. `connectTimeout` applies to total race wall-clock, not per-attempt.
3. Per-attempt timeout is bounded by remaining global timeout.
4. If timeout/cancellation fires after a winner is selected, winner is still returned.

Failure aggregation:

1. Track per-attempt error record: endpoint, family, start time, end time, exception type, socket error.
2. If all attempts fail, throw one `ConnectionException` with:
   - summary reason;
   - winning family preference;
   - flattened inner errors in attempt order.
3. Preserve first meaningful socket error code for telemetry compatibility.

### Implementation Constraints

1. No unbounded task creation for large DNS answer sets.
2. No blocking waits (`Wait`, `Result`, `Thread.Sleep`) in async path.
3. Attempt scheduler must be monotonic-time based (`Stopwatch`) rather than `DateTime.UtcNow`.
4. Ensure socket options and TLS preconditions match existing `RawSocketTransport` behavior.
5. Keep the existing single-family fast path when only one address family is available.

### Edge Cases

1. Host resolves to IPv6-only: run sequential attempts in IPv6 list.
2. Host resolves to IPv4-only: run sequential attempts in IPv4 list.
3. DNS returns duplicate endpoints: deduplicate exact `(Address,Port,Family)` tuples before dialing.
4. Immediate hard failure on first IPv6 endpoint must not suppress scheduled IPv4 fallback.
5. Partial success where socket connects but subsequent setup fails must trigger continued race unless failure is terminal by policy.

---

## Step 2: Add Coverage for Broken IPv6 Scenarios

**File:** `Tests/Runtime/Transport/HappyEyeballsTests.cs` (new)

### Required Test Matrix

| Case | Setup | Expected Result |
|---|---|---|
| `IPv6Healthy_WinsWhenFaster` | v6 success at 40ms, v4 success at 90ms | v6 socket selected, v4 cancelled/disposed |
| `IPv6Stalled_IPv4Fallback` | v6 timeout stall, v4 success | v4 socket selected before global timeout |
| `BothFamiliesFail_AggregatesErrors` | all attempts fail with mixed socket errors | aggregated exception with ordered attempt records |
| `Cancellation_StopsAllAttempts` | cancel token mid-race | all attempts cancelled, no leaked sockets |
| `SingleFamily_NoRaceRegression` | IPv4-only host | behavior equivalent to pre-HE connector |
| `LargeDnsSet_BoundedConcurrency` | many A/AAAA records | active attempts never exceed `MaxConcurrentAttempts` |

Determinism requirements:

1. Use fake connector endpoints and virtual time controls.
2. Assert attempt start order, not only final winner.
3. Assert disposal/cancellation for every losing attempt.

---

## Verification Criteria

1. Broken IPv6 paths complete using IPv4 fallback without waiting full legacy sequential timeout.
2. Winner selection is deterministic for fixed endpoint timing inputs.
3. No socket leaks under success, failure, or cancellation.
4. Error reporting contains sufficient per-attempt detail for troubleshooting without breaking existing telemetry parsing.
