# 2026-11: Unity 2021.3 IL2CPP players never call GC.GetAllocatedBytesForCurrentThread (#414)

## What

The #414 crash happens because Unity 2021.3's libil2cpp never implemented the internal call behind `GC.GetAllocatedBytesForCurrentThread`. `icalls/mscorlib/System/GC.cpp:34-39` reads `IL2CPP_NOT_IMPLEMENTED_ICALL(...); IL2CPP_UNREACHABLE; return 0;`. In a release build `IL2CPP_ASSERT` is empty, so the body is `__builtin_unreachable()`:
- The macOS arm64 IL2CPP player compiles it to a single `brk #0x1`, at GameAssembly.dylib offset 0x11062c (llvm-objdump; nm names the symbol `il2cpp::icalls::mscorlib::System::GC::GetAllocatedBytesForCurrentThread`). It died with EXC_BREAKPOINT/SIGTRAP in `AllocationMeterTests.Calibrated_CountsAKnownAllocation`, right after the log line `TURBOHTTP_ALLOCATION_COUNTER reading GC.GetAllocatedBytesForCurrentThread`.
- The x86_64 iOS simulator player gets SIGILL (#414).
- Where the compiler emits no trap, the call returns whatever the following code leaves. That likely explains #438's 4 KiB, all-thread readings on Android IL2CPP 2021.3; it is a hypothesis until Android is re-run.

`ManagedAllocationCounter.TryGetThreadCounter` now returns null on `ENABLE_IL2CPP && !UNITY_EDITOR && !UNITY_2022_1_OR_NEWER`. Every reader goes through it (`TryGetCountingThreadCounter`, used by `AllocationMeter.Calibrate` and `StreamingAllocationGateTests.ManagedBytesCounter.Create`). Those players measure with the profiler's "GC Allocated In Frame" (the #440 profiles) or `GC.GetTotalMemory`. The iOS-only `UnsafeRuntimeReason` guard is unchanged.

## Files

- `Tests/Runtime/Support/ManagedAllocationCounter.cs`

## Decisions

- Test-only. `Runtime/`, `Editor/` and `Samples~/` never call `GetAllocatedBytesForCurrentThread` or `GetTotalAllocatedBytes`. `Tools~/bench` calls `GC.GetTotalAllocatedBytes`, but it is a .NET host tool, not a Unity player.
- The guard covers 2021.3 only, the version whose source was read. 2022.x and 2023.x are untested; Unity 6 iOS players read the counter (#414).
- The iOS `UnsafeRuntimeReason` (which also blocks the profiler counter) is left for a separate decision. Nothing so far shows the profiler counter crashing.

## Verification

- `Tools~/ci/check.sh` passed.
- macOS IL2CPP arm64 standalone player, Unity 2021.3.45f2, Stripping High, full PlayMode suite, macOS 26.7.1 (Apple M2), 2 runs:
  - Before the fix: SIGTRAP at test 8, in both runs.
  - After the fix, run 1: 3011 tests, 3000 passed, 0 failed, 11 skipped.
  - After the fix, run 2: 2998 passed, 1 failed, 12 skipped. The failure is the known #169 clamped-pool stall.
  - AllocationMeterTests 9/9, StreamingAllocationGateTests 11/11 and DecompressionLimitAndAllocationTests 7/7 all passed, every read through `ProfilerRecorder(GC Allocated In Frame)`.
- Not run: Android IL2CPP (emulator and phone) to confirm the fallback calibrates and the #440 budgets hold; the iOS players; Windows IL2CPP.
