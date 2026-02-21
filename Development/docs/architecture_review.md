# TurboHTTP Architecture Review Report

## 1. Package Structure & Constraints
- **Core Assembly**: Has zero external assembly references, but does reference `UnityEngine` (in `PlatformInfo.cs` and `PlatformConfig.cs`). Need to verify if `noEngineReferences: true` was intended for Core, or if it's acceptable for `PlatformInfo`.
- **Transport Assembly**: `TurboHTTP.Transport` does not reference `UnityEngine`, adhering to `noEngineReferences: true`.

## 2. Dependency Graph
- Optional assemblies (Auth, Cache, JSON, Middleware, Observability, Retry, Unity) correctly reference `TurboHTTP.Core`.
- No circular dependencies found between `TurboHTTP.Core` and `TurboHTTP.Transport`.

## 3. Subsystem Deep Dive
- **Threading and Anti-patterns**: 
  - No `async void` found, which is excellent.
  - Usage of `.Wait()` with timeouts found in `Dispose` paths (`UHttpClient`, `Http2Connection`, `TcpConnectionPool`). While acceptable in Dispose to guarantee cleanup, it may block the calling thread (often Main Thread in Unity) for up to 250ms.
  - Usage of `.Result` found in `CoroutineWrapper.cs`. Verification shows it only happens when `IsCompleted` is true, avoiding deadlocks.
  - Widespread use of `ConcurrentQueue` and `ConcurrentDictionary` found in `Testing`, `Observability`, `Auth`, `Performance`, and `Unity` modules. While `CLAUDE.md` advocates for `lock`-based sync for `ObjectPool` to avoid `ConcurrentBag` overhead, other concurrent collections are freely used. This is generally acceptable but might be an area for IL2CPP performance optimization.
- **Memory Management / Allocations**:
  - `RawSocketTransport.DrainProxyConnectBodyAsync` has been updated to use `ArrayPool<byte>.Rent`.
  - `RawSocketTransport.ReadAsciiLineAsync` has been updated to use `ArrayPool<byte>.Rent`.
  - `Http11RequestSerializer.cs` was upgraded to use `PooledHeaderWriter`, successfully replacing `StringBuilder` and `Encoding.GetBytes()`.
  - **Architectural Flaw Resolved**: `UHttpResponse.cs` has been updated to implement `IDisposable`. The underlying Transport pipelines now correctly use `ArrayPool` to manage response pooling with zero allocations upon disposal.
  - All `FileStream` creations in the codebase (`FileDownloader`, `UnityTempFileManager`, `PathSafety`) are correctly enclosed in `using` blocks. No file handle leaks detected.

## 4. Required Fixes (Resolved)

All findings have been successfully verified as resolved in the latest codebase:

1. **[RESOLVED] Implement `IDisposable` on `UHttpResponse`:**
   - `UHttpResponse` now implements `IDisposable` and manages a `bodyFromPool` lifecycle flag. 
   - Transport layers safely rent arrays from `ArrayPool<byte>.Shared` and release them upon disposal.

2. **[RESOLVED] Implement ArrayPool Buffering in Http11RequestSerializer:**
   - `Http11RequestSerializer` was upgraded to use `PooledHeaderWriter`, successfully replacing `StringBuilder` and `Encoding.GetBytes()`.

3. **[RESOLVED] Eliminate Proxy Connection Allocations:**
   - `RawSocketTransport` methods like `DrainProxyConnectBodyAsync` and `ReadAsciiLineAsync` have been updated to use `ArrayPool<byte>.Shared.Rent` instead of `new byte[]` allocations.

4. **[RESOLVED] Resolve Core Architecture Constraints ambiguity:**
   - `TurboHTTP.Core` intentionally permits `UnityEngine` references to provide `PlatformInfo` and `PlatformConfig` while `TurboHTTP.Transport` strictly maintains `noEngineReferences: true` for pure C# networking portability.
