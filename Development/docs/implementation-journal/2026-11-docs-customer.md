# 2026-11 docs-customer (#232, part of #98)

Wave run w1101, lane docs-customer. README, `Documentation~/` and `Samples~/` are rewritten against the interceptor API, and a harness compiles them.

## What

- **Doc-snippet harness (#232 AC2, #98 AC1).**
  - `Tools~/ci/doc_snippets.py` extracts every fenced `csharp` block of `README.md` and `Documentation~/**/*.md`. `shadow_build.py` writes one generated file per block into the project `TurboHTTP.DocSnippets` (all runtime modules referenced, editor variant and the ios/android variants) with `#line` directives, so a compile error names the Markdown file and line.
  - Blocks that declare a type are compiled as written (they carry their own `using` lines). Statement blocks are wrapped in `static async Task Run()` after a small ambient prelude (`System`, `System.Threading[.Tasks]`, `UnityEngine`, `TurboHTTP.Core`). Module namespaces are never ambient.
  - HTML comments before a fence control a block: `<!-- snippet: with <decls> -->` declares assumed locals, `<!-- snippet: skip <reason> -->` excludes it (the reason is mandatory; 4 blocks skip: the UnityWebRequest and BestHTTP comparisons and two UniTask blocks). `MyDto` and `MyData` are ambient types for fragments.
  - The harness is a shadow-build step, so `check.sh` and CI compile the docs; analyzers are off for snippets and their warnings never reach the baseline.
- **Samples compile in CI.** Each of the five samples got an asmdef (`TurboHTTP.Samples.<Name>`, references only the modules it uses). `shadow_build.py` now also walks `Samples~/`. `check_repo.py asmdef` lets `TurboHTTP.Samples.*` reference any runtime module but not tests or editor tooling.
- **Stale-name guard (#232 AC1, #98 AC3).** `check_repo.py docs` fails on `*Middleware` type names, `Middlewares`, the request-level `SendAsync()` and `SendAsync(request)` in `README.md`, `Documentation~/` and `Samples~/`. `TurboHTTP.Middleware` (the module/namespace that still exists) and a `(formerly ...)` history note are allowed. It also checks that every `package.json` sample folder has an asmdef and that snippet directives are well formed. `Tools~/ci/test_doc_snippets.py` tests the extractor and the guard (run by `check.sh --full`).
- **Docs rewritten.**
  - `Documentation~/Modules/Middleware.md` became `Interceptors.md` (git mv): module table, canonical order, redirects, Retry, Cache (no `DefaultTtl`, no rate-limit policy: `ConcurrencyInterceptor` is a concurrency limiter), logging/metrics, writing an interceptor. The `UHttpClientOptions.Interceptors` XML doc points at the new name.
  - `README.md`, `QuickStart.md`, `APIReference.md`, `MigrationGuide.md`, `Troubleshooting.md` and `Modules/{Core,Auth,Testing,Transport,Unity,UniTask,WebSocket}.md` use `SendBufferedAsync`/`SendStreamingAsync`, dispose responses, and describe the real error model (network failures throw `UHttpException`, 4xx/5xx are responses).
  - New or expanded sections (#98): buffered vs streaming and body ownership (Core.md), TLS backends with their security properties, proxies and HTTP/2 through CONNECT (Transport.md), WebSocket creation, send/receive and reconnection (WebSocket.md), NAT64 (PlatformNotes.md), OAuth with PKCE (Auth.md).
  - The README AssetBundle claim is gone; `Unity.md` says AssetBundles are not loaded (#122 tracks a handler). `GetAssetBundleAsync`, `ToCoroutine`, `RateLimitPolicy`, `CachePolicy.DefaultTtl`, `MockHttpServer.Setup`, `PlayerPrefsTokenStore` and `client.WebSockets.Create` never existed and are removed.
- **Samples rewritten.** `SendBufferedAsync` with `using var`, `AuthInterceptor` with `IAuthTokenProvider`, sample 05 uses Metrics/Redirect/Cache/Retry/Concurrency interceptors in canonical order (its WebSocket part, which used a removed API, is gone; WebSocket usage is in `Modules/WebSocket.md`). Sample 03 no longer needs UGUI and uses a service that exists.
- `check.sh` excludes `.build` from `dotnet format` (the generated snippet sources live there).

## Decisions

- **Harness shape: extract and compile (option a)**, not hand-maintained mirrors, so the Markdown is the single source and a stale snippet fails the build with its own line number.
- **`Middleware.md` renamed to `Interceptors.md`** rather than kept with a narrower scope: the page documents every interceptor and the ordering rules, and the old name made AC3's grep unsatisfiable. The module `TurboHTTP.Middleware` keeps its name (renaming an assembly is a breaking change, outside this lane).
- **AC3 and WebSocket `SendAsync`.** The literal `grep 'SendAsync('` still matches `IWebSocketClient.SendAsync(string|byte[])` in `WebSocket.md`; that API exists, so the guard flags only the zero-argument request form and `SendAsync(request)`.
- Docs describe BouncyCastle as an opt-in fallback with no bypass, device verification pending (#33), and HTTP/2 through CONNECT as unvalidated on devices with no switch yet (#103). Nothing claims device-validated behavior.

## Deferred

- Running the samples in Play mode and recording per-sample results: #268 (v1.0 GA). #98 stays open for that and for #103's switch.
- #7 stays open (device remainder); its AC2 import check is the sample asmdefs plus the shadow build.

## How verified

- `'./Tools~/ci/check.sh'`: pass (invariants incl. `docs`, format, shadow build with the 5 samples and 58 compiled doc blocks). `python3 'Tools~/ci/test_doc_snippets.py'`: pass. Before the rewrite the same checks reported 105 problems in `check_repo.py docs` and syntax errors in the snippet build; a deliberately wrong snippet (`new LoggingMiddleware()`) fails the build at its Markdown line.
- Unity 2021.3.45f2 (host editor, clean project `docs-customer`): the five `Samples~/0N-*` folders copied into `Assets/Samples/` compile to `TurboHTTP.Samples.*.dll`; EditMode `--filter ModuleAutoReference` 19/19 pass with them present.
- Not run: PlayMode, IL2CPP, device, and the samples themselves (they call public services; #268).
