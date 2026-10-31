# Testing guide

Load when writing or running tests, or when a Unity test run misbehaves. Gate commands and CI are
in `tooling.md`.

## Layout

- `Tests/Runtime/` (`TurboHTTP.Tests.Runtime.asmdef`, references every runtime module) and
  `Tests/Editor/` (`TurboHTTP.Tests.Editor.asmdef`). Both keep `defineConstraints:
  ["UNITY_INCLUDE_TESTS"]`, `overrideReferences: true` and `precompiledReferences:
  ["nunit.framework.dll"]`.
- One folder per area: `Core`, `Pipeline`, `Transport` (`Http1`, `Http2`, `Tls`), `Proxy`, `Middleware`,
  `Retry`, `Auth`, `Cache`, `Observability`, `Files`, `JSON`, `WebSocket`, `Unity`, `Mobile`,
  `Platform`, `Performance`, `Integration`, `Compatibility`, `Extensibility`, `Testing`, `Manual`.
- Shared helpers: `Tests/Runtime/AssertAsync.cs`, `Tests/Runtime/Support/`, `MockTransport` and
  `RecordReplayTransport` (TurboHTTP.Testing), the HTTP/2 duplex-stream harness used by
  `Tests/Runtime/Transport/Http2/Http2ConnectionTests*.cs`.
- Allocation gates: `Tests/Runtime/Performance/StreamingAllocationGateTests.cs`,
  `Phase19AllocationGateTests.cs` with baselines in `Tests/Benchmarks/`.

## Writing tests

- A behavior change comes with a test that fails without it. Protocol, concurrency and
  cancellation fixes drive the hostile byte sequence or interleaving (cancel mid-frame, CONTINUATION
  flood, window decrease, EOF mid-headers) through the real code path. Do not re-implement the logic
  under test inside the test and assert on your own copy.
- Deterministic only: no wall-clock sleeps as synchronization, tolerances on scheduler-dependent
  counts (a storm test asserting ≥250 saw 249). Use `CancelAfter`, completion sources, virtual time.
- Unity 2021.3's NUnit profile:
  - no `async Task` test methods ("Method has non-void return value"): write `void` tests that wrap
    the body in `AssertAsync.Run(...)`;
  - no `Assert.ThrowsAsync`: use `AssertAsync.ThrowsAsync`; it matches the exact exception type, so
    catch base types by hand;
  - run async helpers through `Task.Run`; Unity's synchronization context does not pump in tests and
    deadlocks;
  - never call another test method from a test (it skips SetUp/TearDown).
- Code must compile against Unity's .NET Standard 2.1 profile, which lacks `Encoding.Latin1`,
  `ReadOnlySequence<T>.ToArray()`, span-based `Encoding.GetBytes`, `Environment.TickCount64`,
  `ValueTask.FromException<T>` (use `new ValueTask<T>(Task.FromException<T>(…))`),
  `Assert.IsNotSame`, `out` parameters in async methods, and other .NET 5+ APIs. The shadow build
  catches most of these before CI.
- Unity Mono behaves differently from .NET 8, so a green .NET harness proves logic only:
  `GZipStream` does not throw on truncated input, `NetworkStream.ReadAsync` ignores cancellation
  tokens, duplex test streams complete synchronously, `CertificateRequest` is unavailable. Tests that
  depend on these run in the Unity jobs.
- When the transport is right and a test is wrong (e.g. an over-sending flow-control peer), fix the
  test to model a compliant peer; never relax timeouts to make a test pass.
- Tests that need the public internet carry `[Category("ExternalNetwork")]`; very slow or
  >100 MB tests carry `[Explicit]` plus `Integration` or `Performance`. CI excludes `ExternalNetwork`.
- Tests are not exempt from ownership rules: dispose clients and responses (`using var`), and say so
  in a comment when a test deliberately disposes early.
- Do not encode a known bug as expected behavior; if a fix lands later, the test must change with it
  (e.g. retry tests currently assert `Retry-After` may exceed `MaxDelay`, #54).
- Renaming or removing a public type: grep `Runtime/`, `Editor/` and `Tests/` in the same change; the
  Editor monitor and tests broke on the 22.x clean break.

## Running Unity tests

- `'./Tools~/unity/run-tests.sh' --platform EditMode|PlayMode --filter <regex>` syncs the package
  into a cached temp project and runs Unity in batch mode. EditMode runs with graphics (editor-window
  tests need them).
- "Scripts have compiler errors" aborts the whole run and hides every test result. Fix compile errors
  first (typical after a rewrite: missing `using System.Threading`/`.Tasks`/`.Collections.Generic`,
  `Task`/`ValueTask` overload ambiguity, new constructor parameters), then re-run: hidden failures
  show up only after the compile fix.
- Runtime tests run in PlayMode; an EditMode run against `TurboHTTP.Tests.Runtime` discovers 0 tests.
- Read the skipped count in the result XML: a skip is not a pass (CONNECT tunnel tests skip today, #151).
- A journal or PR that says "Unity not run" or "isolated .NET harness only" is unverified. Record
  exactly which lane ran: EditMode, PlayMode, IL2CPP player, device.

## Reviews of test changes

- A phase is not done until every test file its plan lists exists; check the plan's file list.
- Re-run both specialist rubrics after each fix round; fixes have repeatedly introduced sibling bugs.
- Verify reviewer claims against the code before acting on them; earlier reviews contained false
  positives.
