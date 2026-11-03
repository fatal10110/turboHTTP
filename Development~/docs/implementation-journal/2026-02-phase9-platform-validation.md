# Phase 9: Platform Validation and Support — 2026-02-15

## What Was Implemented

Phase 9 (Platform Validation) established the infrastructure for platform-aware behavior in TurboHTTP. This includes runtime detection of OS and scripting backends (Mono vs IL2CPP), platform-specific configuration defaults (timeouts/concurrency), and a suite of compatibility checks to ensure AOT safety.

## Files Created

### Runtime/Core/
- **PlatformInfo.cs** — Static utility for platform detection:
  - Exposes `IsMobile`, `IsEditor`, `IsStandalone` helpers.
  - Uses `ENABLE_IL2CPP` define for backend detection.
  - specific `GetPlatformDescription()` for diagnostics.
- **PlatformConfig.cs** — Configuration defaults repository:
  - Provides `RecommendedTimeout` (45s mobile / 30s desktop).
  - Provides `RecommendedMaxConcurrency` (8 mobile / 16 desktop).
  - Capability flags reflect actual exposed behavior (`SupportsCustomCertValidation` is `false` until public callback APIs are added; `SupportsHttp2` is capability-detected, not hardcoded).
- **IL2CPPCompatibility.cs** — Runtime validator for AOT safety:
  - Checks async/await state-machine execution via a real awaited flow.
  - Validates generic virtual method tables.
  - Verifies `CancellationToken` and reflection sanity.
  - Returns actionable reports for startup diagnostics.

### Tests/Runtime/Platform/
- **PlatformTests.cs** — New test suite:
  - Validates `PlatformInfo` consistency with Unity APIs.
  - Verifies `PlatformConfig` defaults and that defaults are actually applied by runtime entry points (`UHttpClientOptions` timeout default and `RawSocketTransport` default pool concurrency).
  - Runs full `IL2CPPCompatibility` suite.
  - Includes `[Category("ExternalNetwork")]` manual smoke test that performs a real HTTPS request (`https://www.google.com/generate_204`).

### Documentation~/
- **PlatformNotes.md** — Comprehensive platform guide:
  - Supported platforms matrix (Windows, Mac, Linux, iOS, Android).
  - Platform-specific manifest requirements (iOS ATS, Android Cleartext/Permissions).
  - Troubleshooting guide for TLS and stripping issues.
  - IL2CPP `link.xml` guidance.

## Decisions Made

1.  **Core-Based Detection:** Platform logic sits in `TurboHTTP.Core` to allow low-level components (Transport, Http Client) to adapt without upward dependencies.
2.  **Allocation-Free Hot Paths:** `PlatformInfo` properties are designed to be zero-allocation accessors for frequent checks.
3.  **Explicit IL2CPP Validation:** Instead of relying solely on unit tests, `IL2CPPCompatibility` is a runtime utility that can be invoked in production builds to diagnose stripping issues in the wild.
4.  **Conservative Mobile Defaults:** Mobile concurrency is capped at 8 to prevent thread pool starvation on lower-end devices and reduce battery impact; these defaults are wired into runtime constructors.
5.  **Documentation as Infrastructure:** `PlatformNotes.md` is treated as a deliverable artifact to guide users through the complex landscape of Unity platform constraints (permissions, IPv6, AOT).

## Validation Notes

- **Editor Verification:** All checks verified in macOS Editor environment.
- **IL2CPP Simulation:** Verified `ENABLE_IL2CPP` logic via code inspection (since Editor runs Mono).
- **Network Smoke Test:** External network probe now executes an actual request path through `UHttpClient` and transport/TLS stack (`GET /generate_204`).

## Post-Review Corrections (2026-02-15)

After review findings, the following Phase 9 gaps were corrected:

1. Platform defaults are now consumed by runtime defaults:
   - `UHttpClientOptions.DefaultTimeout` now initializes from `PlatformConfig.RecommendedTimeout`.
   - `RawSocketTransport` now initializes `TcpConnectionPool` with `PlatformConfig.RecommendedMaxConcurrency`.
2. Platform capability flags now report realistic behavior instead of hardcoded `true`.
3. IL2CPP async compatibility check now validates a real async/await flow.
4. External network smoke test now performs real I/O rather than option-only construction.
5. `Documentation~/PlatformNotes.md` was updated to match actual transport/provider behavior and current API limitations.
