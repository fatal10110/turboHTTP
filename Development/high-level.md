# TurboHTTP High-Level Architecture

**Status:** Active Implementation (Phases 1-11 Complete)
**Version:** 0.8 (Pre-release)

This document outlines the architecture of TurboHTTP, a production-grade modular HTTP client for Unity. It reflects the current implementation status as of Feb 2026.

---

## 1. Product Definition

### 1.1 Core Value Proposition
*   **Modern Transport:** Raw TCP/TLS socket implementation (no `UnityWebRequest` dependency for core transport).
*   **HTTP/2:** Native support for multiplexing, HPACK compression, and flow control.
*   **Observability:** First-class timeline tracing (DNS, Connect, TLS, TTFB).
*   **Unity-First:** Zero-allocation design patterns, native asset handlers (`Texture2D`, `AudioClip`).
*   **Reliability:** Idempotency-aware retry logic and connection pooling.

### 1.2 Supported Platforms
*   **Editor/Standalone (Win/Mac/Linux):** Fully supported (Mono/IL2CPP).
*   **Mobile (iOS/Android):** Fully supported (IL2CPP).
    *   *Note:* iOS ATS and Android Cleartext configs required for non-HTTPS.
*   **WebGL:** Planned (via Fetch API adapter).
*   **Consoles:** Experimental.

---

## 2. Architecture Overview

### 2.1 Layered Design

1.  **Public API Layer (`TurboHTTP.Core`)**
    *   `UHttpClient`: Main entry point, manages connection pool and pipeline.
    *   `UHttpRequestBuilder`: Fluent API for constructing requests.
    *   `UHttpResponse`: Standardized response with typed helpers (`.AsJson<T>()`).

2.  **Pipeline Layer (`TurboHTTP.Core` / Middleware Modules)**
    *   **Architecture:** Delegate-based chain (`Func<RequestContext, Func<Task>, Task>`).
    *   **Implemented Middlewares:**
        *   `RetryMiddleware`: Exponential backoff, idempotency checks.
        *   `AuthMiddleware`: Bearer token injection.
        *   `CacheMiddleware`: ETag/Last-Modified, in-memory/disk storage, revalidation.
        *   `RateLimitMiddleware`: Token bucket per host.
        *   `ObservabilityMiddleware`: Logging and metrics.
        *   `CookieMiddleware`: Session state management.
        *   `RedirectMiddleware`: Automatic 3xx handling.

3.  **Transport Layer (`TurboHTTP.Transport`)**
    *   **RawSocketTransport:** Custom implementation over `System.Net.Sockets`.
        *   **Connection Pool:** Host-sharded, keep-alive, smart disposal.
        *   **HTTP/1.1:** Custom serializer/parser, chunked transfer support.
        *   **HTTP/2:** Binary framing, HEADERS/DATA/RST_STREAM handling, HPACK (static/dynamic tables), flow control (connection/stream windows).
    *   **TLS Abstraction:**
        *   `SslStream`: Default for Desktop/Editor.
        *   `BouncyCastle`: Fallback for platforms with poor ALPN support (older Android/iOS).
    *   **MockTransport:** For unit testing.
    *   **RecordReplayTransport:** For deterministic integration testing.

4.  **Content Handlers (`TurboHTTP.Content`)**
    *   **JSON:** `System.Text.Json` optimization (UTF-8 bytes).
    *   **Files:** `FileDownloader` with resume, progress, and checksum validation.
    *   **Multipart:** `MultipartFormDataBuilder` for uploads.
    *   **Unity:** `Texture2DHandler`, `AudioClipHandler` (main-thread dispatched).

---

## 3. Key Concepts

### 3.1 Observability
Every request generates a `TimelineTrace` containing:
*   **Steps:** RequestQueued, DnsResolved, Connected, TlsHandshake, RequestSent, HeadersReceived, ResponseReceived.
*   **Metrics:** Duration, Bytes Sent/Received.
*   **Integration:** Visible in the **HTTP Monitor Window** (Unity Editor).

### 3.2 Threading Model
*   **Transport/Parsing:** Runs on background threads (ThreadPool) to avoid blocking the Unity Main Thread.
*   **Unity Interaction:** `MainThreadDispatcher` marshals asset creators (Textures/Audio) back to the main thread.
*   **Async/Await:** Fully task-based API.

### 3.3 Zero-Allocation Focus
*   **Pooling:** `ObjectPool<T>` and `ArrayPool<byte>` used extensively.
*   **Spans:** Parsing logic uses `Span<byte>` and `Memory<byte>` to minimize string allocations.

---

## 4. Current Status (Feb 2026)

### Implemented Features
*   [x] **Core Client:** Fluent API, Request/Response model.
*   [x] **Transport:** HTTP/1.1 and HTTP/2 (Multiplexing/HPACK).
*   [x] **TLS:** ALPN negotiation (SslStream + BouncyCastle fallback).
*   [x] **Pipeline:** Full middleware stack (Retry, Auth, Cache, etc.).
*   [x] **Content:** JSON, File Downloads, Multipart.
*   [x] **Unity Integration:** Textures, Audio, Main Thread dispatch.
*   [x] **Tools:** Editor Monitor Window, Record/Replay Testing.
*   [x] **Testing:** Comprehensive Unit and Integration suites.

### Upcoming
*   [ ] **Phase 12:** Additional Editor Tools.
*   [ ] **Phase 14:** Background Networking (iOS/Android).
*   [ ] **Phase 15:** Advanced Unity Asset Pipeline hardening.
*   [ ] **Phase 16:** WebGL (Fetch) and WebSockets.

---

## 5. Development Workflow

For detailed implementation plans and daily journals, refer to:
*   `Development/docs/00-overview.md`: Master roadmap.
*   `Development/docs/phases/`: Specific phase requirements.
*   `Development/docs/implementation-journal/`: Daily logs of changes.
