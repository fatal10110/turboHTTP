## 19b.4: Purge Legacy Compatibility Flows

**Goal:** Remove all code paths and compiler `#if` directives maintained solely for backward compatibility.

Summary:
1. Delete `Http2MaxDecodedHeaderBytes` (currently marked `[Obsolete]`) from `UHttpClientOptions`.
2. Delete the legacy `Register` overload in `HttpTransportFactory` that takes an `int` parameter.
3. Review and remove older target framework `#if` switches (e.g. for `NETSTANDARD2_0`, obsolete .NET Core flags, or BouncyCastle legacy shims).
4. Remove any "legacy" behavior logic (e.g. `UseLegacyResumption`) discovered in TLS or Transport layers unless structurally required for current platforms.
