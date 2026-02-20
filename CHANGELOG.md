# Changelog

All notable changes to TurboHTTP will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Phase 14 Features**:
  - Happy Eyeballs support for fast IPv4/IPv6 dual-stack connections.
  - Proxy Support with authenticated HTTP/HTTPS proxies.
  - Background Networking for native iOS/Android seamless connection persistence.
  - Adaptive Networking for dynamic timeouts based on network quality.
  - OAuth 2.0 & OIDC integration with automated token refresh and PKCE flow.
  - Interceptor Middleware architecture for request/response mutation.
  - Mock Server capability for robust offline unit and integration testing.
  - Plugin System for zero-overhead, modular extensions.
- Initial project structure
- Modular assembly definitions
- **Documentation Rework**: Restructured documentation, added contributing guidelines, and polished user guides.

## [1.0.0] - TBD

### Added
- Core HTTP client with fluent API
- Retry middleware with idempotency awareness
- Cache middleware with ETag support
- Authentication middleware
- Rate limiting middleware
- Observability module with timeline tracing
- File downloader with resume support
- Unity content handlers (Texture2D, AudioClip)
- Testing module with record/replay
- Performance module with memory pooling
- Editor HTTP Monitor window
- Comprehensive samples and documentation
