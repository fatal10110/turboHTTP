# Modules and manual registration

This page covers what the `TurboHTTP.Complete` assembly does at startup, what to call yourself when you do not use it, the scripting defines, and terse reference tables for features that have no page of their own. Member names were checked against the code; see the module pages and the XML docs for behavior.

## What `TurboHTTP.Complete` does

`TurboHTTP.Complete` (`Runtime/TurboHTTP.Complete.asmdef`) is auto-referenced and excluded on WebGL. It references Core, Transport, WebSocket, WebSocket.Transport, Unity.WebSocket, Retry, Cache, Auth, Observability, Files, Unity, Testing, RateLimit, JSON and the redirect, cookie and decompression module. Its bootstrap runs at `RuntimeInitializeLoadType.SubsystemRegistration`, before any scene loads, and calls:

| Call | Effect |
| --- | --- |
| `RawSocketTransport.EnsureRegistered()` | Registers the default HTTP transport with `HttpTransportFactory`, so `new UHttpClient()` works. |
| `RawSocketWebSocketTransport.EnsureRegistered()` | Registers the default WebSocket transport. |
| `JsonModuleRegistration.EnsureRegistered()` | Registers the `JsonUtility` based JSON bridge used by `AsJson<T>()` and friends. |
| `AndroidCleartextTrafficPolicy.EnsureRegistered()` | Registers the Android cleartext policy (a no-op off Android players). |

`ObservabilityModuleRegistration.EnsureRegistered()` also runs at `SubsystemRegistration` from its own assembly (it lets clients append the HTTP Monitor interceptor in the Editor).

If a request fails with "No transport factory configured", the transport registration did not run. That happens when your project references only individual module assemblies and none of them pulls in `TurboHTTP.Complete`. Either reference `TurboHTTP.Complete` from your assembly definition, or call the methods above yourself at startup, before the first client is created (for example from a `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]` method). `JsonModuleRegistration.EnsureRegistered()` replaces a JSON bridge registered earlier (see [API Reference](../APIReference.md), "JSON serializers").

## Scripting defines

| Define | Effect |
| --- | --- |
| `TURBOHTTP_BOUNCYCASTLE` | Compiles the bundled BouncyCastle TLS provider (needed for ALPN and HTTP/2 on Unity's runtime). Opt-in. |
| `TURBOHTTP_BACKGROUND_NETWORKING` | Enables the experimental background networking bridges (see [Unity](Unity.md), "Mobile networking"). |
| `TURBOHTTP_UNITASK` | Set automatically by the UniTask modules when UniTask is installed. |
| `TURBOHTTP_USE_SYSTEM_TEXT_JSON` | Adds the `WithJsonBody<T>(value, System.Text.Json.JsonSerializerOptions)` overload (the project must provide `System.Text.Json`). |
| `TURBOHTTP_POOL_DIAGNOSTICS` | Adds allocation stack traces and double-return detection to `PooledBuffer<T>` (already on in `DEBUG` builds). |
| `TURBOHTTP_VERBOSE_DIAGNOSTICS` | Logs the selected socket I/O mode to the diagnostic sink (success-path information, compiled out by default). |
| `TURBOHTTP_STBIMAGESHARP` | Enables the StbImageSharp image decoder adapter. |
| `TURBOHTTP_NVORBIS` | Enables the NVorbis audio decoder adapter. |
| `TURBOHTTP_NLAYER` | Enables the NLayer (MP3) audio decoder adapter. |

The managed decoder adapters behind the last three defines are stubs today: without a working provider `CanDecode` is false, and the Unity built-in decode path is used on the main thread (tracked in #158). To decode a format yourself, register an `IImageDecoder` or `IAudioDecoder` with `DecoderRegistry.RegisterImageDecoder` / `RegisterAudioDecoder`.

## Reference tables

### Core

| Type | Members worth knowing |
| --- | --- |
| `HttpTransportFactory` | `Register`, `RegisterCapabilities`, `Capabilities` (`TransportCapabilities`: `None`, `Http2`), `Default`, `CreateWithBackend`, `CreateWithOptions`. |
| `IHttpHandler`, `IResponseBodySource` | The callback and the pull-based body contracts custom transports and interceptors implement; `IResponseBodySource.GetTrailersAsync` returns response trailers. |
| `IHttpPlugin`, `PluginCapabilities` | `UHttpClient.RegisterPluginAsync`, `GetRegisteredPlugins`. Capabilities (flags): `ObserveRequests`, `ReadOnlyMonitoring`, `MutateRequests`, `MutateResponses`, `HandleErrors`, `Diagnostics`, `AllowRedispatch`. Lifecycle (`PluginLifecycleState`): `Created`, `Initializing`, `Initialized`, `Faulted`, `ShuttingDown`. `UHttpClientOptions.PluginShutdownTimeout` bounds shutdown. |
| `ConnectionPoolOptions` (`options.ConnectionPool`) | `MaxConnectionsPerHost`, `ConnectionIdleTimeout`, `DnsTimeoutMs`, `ConnectTimeout`, `ReusedConnectionFirstByteTimeout`, `HappyEyeballs`, `SocketIoMode`. |
| `SocketIoMode` | `NetworkStream` (default, most compatible), `Saea` (lower allocation socket I/O), `PollSelect`. |
| `HappyEyeballsOptions` (`ConnectionPool.HappyEyeballs`) | `Enable`, `PreferIpv6`, `FamilyStaggerDelay`, `AttemptSpacingDelay`, `MaxConcurrentAttempts`. |
| `StreamingOptions` (`options.Streaming`) | `SmallBufferedRequestThresholdBytes`, `DefaultStreamingSendBufferBytes`, `DefaultStreamingReceiveBufferBytes`, `DefaultHttp2PerStreamReceiveBufferBytes`, `MaxConnectionBufferedBytes`, `Http2StallTimeoutSeconds`, `ExpectContinueTimeoutMs`, `AutoExpectContinueThresholdBytes`. |
| `Http2Options` (`options.Http2`) | `MaxConcurrentStreams`, `MaxResponseBodySize`, `InitialWindowSize`, `MaxFrameSize`, `MaxHeaderListSize`, `MaxDecodedHeaderBytes`, `PingInterval`, `PingTimeout`, `EnableHttp2ThroughConnectTunnels`, `MaxConnectionsPerOrigin`, `ReuseProbeIdleThreshold`, `EnablePush`. |
| `ProxyRequestBuilderExtensions` | `request.WithProxy(ProxySettings)` overrides the client proxy for one request; `request.WithoutProxy()` sends it directly. |
| `UHttpRequest` | `WithRequestTrailers(declaredNames, provider)` sends request trailers (the request is sent chunked); `WithExpectContinue(bool)` sends `Expect: 100-continue`. Responses expose `UHttpResponse.Trailers`. |

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
options.ConnectionPool.SocketIoMode = SocketIoMode.Saea;
options.Http2.PingInterval = TimeSpan.FromSeconds(20);
options.ConnectionPool.HappyEyeballs.PreferIpv6 = false;
```

### WebSocket

| Type | Members worth knowing |
| --- | --- |
| `WebSocketConnectionOptions` | `SubProtocols`, `CustomHeaders`, `PingInterval` (default 25 s), `IdleTimeout` (zero disables), `MetricsUpdateMessageInterval`, `MetricsUpdateInterval`. |
| `WebSocketClient`, `ResilientWebSocketClient` | `SubProtocol` (negotiated), `Metrics` (`WebSocketMetrics`), `OnMetricsUpdated`. |
| `WebSocketHealthMonitor` | `OnQualityChanged` (`ConnectionQuality`), `GetSnapshot()` (`WebSocketHealthSnapshot`). |
| `JsonWebSocketSerializer<T>` | An `IWebSocketMessageSerializer<T>` over the JSON module; `T` is a `[Serializable]` class with public fields unless you register another serializer. |
| `UnityWebSocketClient` | A `MonoBehaviour` with `Connect`/`ConnectAsync`, `Disconnect`/`DisconnectAsync`, `Send`/`SendAsync`, `IsConnected`, `ConnectionState`, `AllowCleartext` and UnityEvents (`OnConnectedEvent`, `OnMessageReceivedEvent`, `OnBinaryReceivedEvent`, `OnDisconnectedEvent`, `OnErrorEvent`). |

See [WebSocket](WebSocket.md) for usage.

### Unity and observability

| Type | Members worth knowing |
| --- | --- |
| `DecoderRegistry` | `Initialize`, `BootstrapDefaults`, `RegisterImageDecoder`, `RegisterAudioDecoder`, `GetRoutingPolicy` (`DecoderRoutingPolicy`: `EnableManagedImageDecode`, `EnableManagedAudioDecode`, concurrency limits), `TryResolveImageDecoder`, `TryResolveAudioDecoder`, `WarmupImageDecodersAsync`, `WarmupAudioDecodersAsync`. |
| `RedactionPolicy` | `Default`, `None`, `DefaultSensitiveHeaderNames`, `WithSensitiveHeaders`, `WithoutSensitiveHeaders`, `WithQueryValueRedaction`, `IsSensitiveHeader`, `RedactHeaderValue`, `RedactUrl`. `LoggingInterceptor` and `MonitorInterceptor` take one. |

## HTTP Monitor

The Editor-only monitor shows requests sent by clients created in Play Mode. Open it from **Window > TurboHTTP > HTTP Monitor**. It needs no scripting define. Create your `UHttpClient` in `Awake` or `Start` (see [Troubleshooting](../Troubleshooting.md)).

Settings live under **Edit > Preferences > TurboHTTP**:

| Setting | Meaning |
| --- | --- |
| Enable HTTP Monitor | Installs the monitor interceptor on new clients. |
| History Capacity | Number of requests kept. |
| Max Capture Size (MB) | Per-body capture cap. |
| Binary Preview (KB) | Size of the preview shown for binary bodies. |
| Total Capture Budget (MB) | Memory budget across all captured bodies. |
| Redact Captured Secrets | Masks sensitive headers and query values in what the monitor stores. |
| Capture While Monitor Closed | Keeps recording when the window is closed. |
| Mask Confidential Headers (Default) | Masks header values in the window even when redaction is off; exports are always masked. |
| Include Bodies In Export | Writes bodies into JSON exports. Bodies are not redacted, so this is off by default. |

## Request timelines

Every request has a `RequestContext.Timeline` (a list of `TimelineEvent`: `Name`, `Timestamp` relative to request start, optional `Data`). Interceptors can read it and add events with `context.RecordEvent`. The HTTP Monitor shows a snapshot of it (`HttpMonitorTimelineEvent`). Events recorded by the library:

| Source | Events |
| --- | --- |
| Client | `RequestStart`, `RequestComplete`, `RequestFailed`, `RequestCancelled` |
| Transport | `TransportStart`, `TransportConnecting`, `TransportSending`, `TransportReceiving`, `TransportComplete`; HTTP/2 and proxy paths also `TransportH2Reuse`, `TransportH2AwaitDial`, `TransportH2Init`, `TransportH2RequestSent`, `TransportProxyConnecting`, `TransportProxyH2Reuse`, `TransportProxyAuthRetry` |
| Redirects and cookies | `CookieAttached`, `CookieStored`, `Redirect` |
| Retry | `RetryAttempt`, `RetryScheduled`, `RetryAfterExceedsMaxDelay` |
| Rate limit | `ConcurrencyAcquire`, `ConcurrencyAcquired`, `ConcurrencyReleased` |
| Unity mobile | `adaptive.applied`; with background networking `mobile.bg.acquire.start`, `mobile.bg.acquire.success`, `mobile.bg.expired`, `mobile.bg.deferred.enqueued`, `mobile.bg.release` |

There are no separate DNS, TLS handshake or time-to-first-byte events. `UHttpResponse.ElapsedTime` is the only total duration it exposes.
