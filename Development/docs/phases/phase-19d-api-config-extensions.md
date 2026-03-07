# Phase 19d: API Configuration & Extension Review

## Objective
To extend the configuration points of the TurboHTTP library natively via `UHttpClientOptions` and improve the builder developer experience on `UHttpRequest` using extension methods without polluting the core data structures.

## Motivation
While reviewing the `UHttpClientOptions`, `UHttpRequest` APIs, and JSON/Unity extension methods, we identified several gaps in configuration granularity and request-building ergonomics. Addressing these ensures the library works gracefully across various networking environments (like custom DNS or explicit socket tuning) and improves the fluidity of the development experience.

## Planned Changes

### 1. Global Configurations (`UHttpClientOptions`)
We will add new properties to `UHttpClientOptions` to expose lower-level HTTP transport behaviors natively, bridging the gap with modern standard HTTP clients:

- **Custom DNS Resolution**: Expose an `IDnsResolver` interface and `DnsResolver` property.
- **Automatic Decompression**: Support native handling algorithms (GZip, Deflate, Brotli) via standard `DecompressionMethods`.
- **Cookie Management**: Provide an `ICookieManager` and a `UseCookies` toggle to handle stateful server responses automatically.
- **TCP / Socket Tuning**: Surface a `SocketOptions` class (e.g., `TcpNoDelay`, `KeepAliveInterval`, buffers) down to the socket transport layer.
- **TLS Validation**: Expose a standard `ServerCertificateCustomValidationCallback` for dev-environment/self-signed certificate testing or pinning.

### 2. Request Level Ergonomics (`UHttpRequest`)
To keep `UHttpRequest` purely a rental data container and enforce the single responsibility principle, these fluid URL/metadata augmentations will be implemented as **static extension methods** under a new `RequestBuilderExtensions` class in `TurboHTTP.Core`.

- `WithQueryParam(key, value)`: Cleanly appends or adds query parameters to the request URI.
- `WithCancellationToken(token)`: Overrides standard client-level timeout controls organically.
- `WithFormUrlEncoded(collection)`: Seamless `application/x-www-form-urlencoded` payloads generation.
- `WithCookie(name, value)`: Attaches one-off manual cookies.
- `WithDownloadProgress(Action)` & `WithUploadProgress(Action)`: Stores callbacks inside request headers/metadata natively parsed by progress-tracking middlewares.
- Expose `WithUri(Uri)` natively inside `UHttpRequest` to manage URI replacements effectively.

## Migration & Compatibility
- These features strictly add to the public surface area, acting as non-breaking enhancements.
- All new objects like `SocketOptions` will be gracefully initialized and handled inside the clone paths correctly.
- Extension methods prevent core object bloat while providing equivalent chainable APIs.

## Completion Criteria
- [ ] Implement `IDnsResolver`, `ICookieManager`, and `SocketOptions` contracts.
- [ ] Add properties to `UHttpClientOptions` and integrate into the deep-clone strategy.
- [ ] Provide unified `WithUri(Uri)` on `UHttpRequest`.
- [ ] Introduce `RequestBuilderExtensions` static class with query, cancellation, cookie, forms, and progress methods.
- [ ] Register new metadata keys into `RequestMetadataKeys`.
