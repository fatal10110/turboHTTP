using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Main HTTP client for TurboHTTP.
    /// Provides fluent verb helpers and client-bound request creation.
    /// Thread-safe for concurrent use.
    /// </summary>
    public class UHttpClient : IDisposable
    {
        private readonly UHttpClientOptions _options;
        private readonly IHttpTransport _transport;
        private readonly bool _ownsTransport;
        private readonly IReadOnlyList<IHttpInterceptor> _baseInterceptors;

        // Written only under _pluginLock; read lock-free (volatile) by the send paths.
        private volatile DispatchFunc _pipeline;

        // Serializes RegisterPluginAsync/UnregisterPluginAsync. Never disposed: it has no wait handle
        // (AvailableWaitHandle is not used), and disposing it under waiters made their Release throw
        // ObjectDisposedException over the real result (#178).
        private readonly SemaphoreSlim _pluginLifecycleGate = new SemaphoreSlim(1, 1);
        // Guards _plugins and every write of _pipeline. Dispose clears both under it after setting
        // _disposed, so a registration that re-checks _disposed under it cannot resurrect either.
        private readonly object _pluginLock = new object();
        private readonly List<PluginRegistration> _plugins = new List<PluginRegistration>();

        // 0 = live, 1 = disposed. Set once by Interlocked.CompareExchange; read with Volatile.Read.
        private int _disposed;

        /// <summary>
        /// The snapshotted options for this client.
        /// </summary>
        internal UHttpClientOptions ClientOptions => _options;

        /// <summary>
        /// The transport instance used by this client.
        /// </summary>
        public IHttpTransport Transport => _transport;

        private sealed class PluginRegistration
        {
            public IHttpPlugin Plugin { get; }
            public string Name { get; }
            public string Version { get; }
            public PluginCapabilities Capabilities { get; }
            public IReadOnlyList<IHttpInterceptor> Interceptors { get; }
            public PluginLifecycleState State { get; set; }

            public PluginRegistration(
                IHttpPlugin plugin,
                IReadOnlyList<IHttpInterceptor> interceptors,
                PluginLifecycleState state)
            {
                Plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
                Name = string.IsNullOrWhiteSpace(plugin.Name) ? plugin.GetType().FullName : plugin.Name;
                Version = plugin.Version ?? string.Empty;
                Capabilities = plugin.Capabilities;
                Interceptors = interceptors ?? Array.Empty<IHttpInterceptor>();
                State = state;
            }

            public PluginDescriptor ToDescriptor()
            {
                return new PluginDescriptor(Name, Version, Capabilities, State);
            }
        }

        /// <summary>
        /// Returns a cloned snapshot of the options used to construct this client.
        /// </summary>
        public UHttpClientOptions GetOptionsSnapshot()
        {
            ThrowIfDisposed();
            return _options.Clone();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UHttpClient"/> class with the specified options.
        /// </summary>
        public UHttpClient(UHttpClientOptions options = null)
        {
            _options = options?.Clone() ?? new UHttpClientOptions();

            if (_options.Http2 == null)
                _options.Http2 = new Http2Options();

            if (_options.Streaming == null)
                _options.Streaming = new StreamingOptions();

            if (_options.Http2.MaxDecodedHeaderBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(_options.Http2.MaxDecodedHeaderBytes),
                    _options.Http2.MaxDecodedHeaderBytes,
                    "Must be greater than 0.");
            }

            // Validated before any transport is created, so a rejected configuration leaks nothing (#55).
            _baseInterceptors = BuildInterceptors(_options);
            var orderViolation = InterceptorOrdering.FindViolation(_baseInterceptors);
            if (orderViolation != null)
                throw new ArgumentException(orderViolation, nameof(options));

            WarnIfRedirectsAreNotFollowed(_options, _baseInterceptors);

            if (_options.Transport != null)
            {
                _transport = _options.Transport;
                _ownsTransport = _options.DisposeTransport;
            }
            else if (_options.TlsBackend != TlsBackend.Auto ||
                     !_options.ConnectionPool.IsDefault() ||
                     !_options.Http2.IsDefault() ||
                     !_options.Streaming.IsDefault())
            {
                _transport = HttpTransportFactory.CreateWithOptions(
                    _options.TlsBackend,
                    _options.ConnectionPool,
                    _options.Http2,
                    _options.Streaming);
                _ownsTransport = true;
            }
            else
            {
                _transport = HttpTransportFactory.Default;
                _ownsTransport = false;
            }

            _pipeline = new InterceptorPipeline(_baseInterceptors, _transport).Pipeline;
        }

        private static void WarnIfRedirectsAreNotFollowed(
            UHttpClientOptions options,
            IReadOnlyList<IHttpInterceptor> interceptors)
        {
            // FollowRedirects/MaxRedirects only reach the request metadata; the RedirectInterceptor
            // (Middleware module) acts on them. Without it a 3xx is returned as is (#64).
            if (!options.FollowRedirects || InterceptorOrdering.Contains(interceptors, InterceptorRole.Redirect))
                return;

            TurboDiagnostics.LogOnce(
                TurboDiagnostics.OnceFollowRedirectsWithoutInterceptor,
                "[TurboHTTP] UHttpClientOptions.FollowRedirects is true but no RedirectInterceptor is configured, " +
                "so redirects are returned to the caller instead of followed. Add " +
                "TurboHTTP.Middleware.RedirectInterceptor to UHttpClientOptions.Interceptors, or set " +
                "FollowRedirects = false. This warning is shown once.");
        }

        /// <summary>
        /// Creates a new mutable request bound to this client.
        /// The request is single-use: dispose it, or send it with <see cref="UHttpRequest.SendBufferedAsync"/> /
        /// <see cref="UHttpRequest.SendStreamingAsync"/> (which dispose it), to end its lease. A request is never
        /// reused after its lease ends, so a stale reference throws <see cref="ObjectDisposedException"/>.
        /// </summary>
        public UHttpRequest CreateRequest(HttpMethod method, string url)
        {
            if (url == null)
                throw new ArgumentNullException(nameof(url));

            ThrowIfDisposed();

            var uri = ResolveUri(url);
            // Not pooled: a recycled instance would let a stale reference mutate or send another caller's
            // request (#62). Each lease gets its own object, which is never handed out again.
            var request = new UHttpRequest(this, method, uri, _options.DefaultTimeout);
            try
            {
                request.ApplyDefaultHeaders(_options.DefaultHeaders);
                ApplyDefaultMetadata(request, uri);
                return request;
            }
            catch
            {
                request.Dispose();
                throw;
            }
        }

        /// <summary> Creates a new GET request. </summary>
        public UHttpRequest Get(string url) => CreateRequest(HttpMethod.GET, url);
        /// <summary> Creates a new POST request. </summary>
        public UHttpRequest Post(string url) => CreateRequest(HttpMethod.POST, url);
        /// <summary> Creates a new PUT request. </summary>
        public UHttpRequest Put(string url) => CreateRequest(HttpMethod.PUT, url);
        /// <summary> Creates a new DELETE request. </summary>
        public UHttpRequest Delete(string url) => CreateRequest(HttpMethod.DELETE, url);
        /// <summary> Creates a new PATCH request. </summary>
        public UHttpRequest Patch(string url) => CreateRequest(HttpMethod.PATCH, url);
        /// <summary> Creates a new HEAD request. </summary>
        public UHttpRequest Head(string url) => CreateRequest(HttpMethod.HEAD, url);
        /// <summary> Creates a new OPTIONS request. </summary>
        public UHttpRequest Options(string url) => CreateRequest(HttpMethod.OPTIONS, url);

        /// <summary>
        /// Registers a plugin and initializes it asynchronously.
        /// </summary>
        public async Task RegisterPluginAsync(IHttpPlugin plugin, CancellationToken ct = default)
        {
            if (plugin == null)
                throw new ArgumentNullException(nameof(plugin));

            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            var enteredLifecycleGate = false;
            try
            {
                await _pluginLifecycleGate.WaitAsync(ct).ConfigureAwait(false);
                enteredLifecycleGate = true;
                ThrowIfDisposed();

                var pluginName = string.IsNullOrWhiteSpace(plugin.Name) ? plugin.GetType().FullName : plugin.Name;
                if (FindPluginByName_NoLock(pluginName) != null)
                {
                    throw new PluginException(
                        pluginName,
                        "register",
                        $"Plugin '{pluginName}' is already registered.");
                }

                var contributedInterceptors = new List<IHttpInterceptor>();
                var context = new PluginContext(
                    _options.Clone(),
                    pluginName,
                    plugin.Capabilities,
                    interceptor => contributedInterceptors.Add(interceptor),
                    message => TurboDiagnostics.Log("[TurboHTTP][Plugin:" + pluginName + "] " + message));

                PluginRegistration registration;
                try
                {
                    await plugin.InitializeAsync(context, ct).ConfigureAwait(false);
                    registration = new PluginRegistration(
                        plugin,
                        contributedInterceptors,
                        PluginLifecycleState.Initialized);
                }
                catch (Exception ex)
                {
                    throw new PluginException(
                        pluginName,
                        "initialize",
                        $"Plugin '{pluginName}' failed during initialization.",
                        ex);
                }

                string orderViolation = null;
                lock (_pluginLock)
                {
                    // Re-checked under the lock Dispose clears _plugins under: a plugin added here is
                    // either seen (and shut down) by Dispose, or not added at all (#178).
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        var combined = BuildCombinedInterceptors_NoLock(registration);
                        orderViolation = InterceptorOrdering.FindViolation(combined);
                        if (orderViolation == null)
                        {
                            _plugins.Add(registration);
                            _pipeline = new InterceptorPipeline(combined, _transport).Pipeline;
                            return;
                        }
                    }
                }

                // Initialized but not registered: shut it down here, nobody else owns it.
                await ShutdownUnregisteredPluginAsync(registration).ConfigureAwait(false);

                if (orderViolation != null)
                    throw new PluginException(pluginName, "register", orderViolation);

                throw new ObjectDisposedException(nameof(UHttpClient));
            }
            finally
            {
                if (enteredLifecycleGate)
                    _pluginLifecycleGate.Release();
            }
        }

        /// <summary>
        /// Unregisters a plugin by name and shuts it down asynchronously.
        /// </summary>
        public async Task UnregisterPluginAsync(string pluginName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(pluginName))
                throw new ArgumentException("Plugin name is required.", nameof(pluginName));

            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            var enteredLifecycleGate = false;
            try
            {
                await _pluginLifecycleGate.WaitAsync(ct).ConfigureAwait(false);
                enteredLifecycleGate = true;
                ThrowIfDisposed();

                PluginRegistration registration;
                lock (_pluginLock)
                {
                    registration = FindPluginByName_NoLock(pluginName);
                    if (registration == null)
                        return;

                    registration.State = PluginLifecycleState.ShuttingDown;
                    _plugins.Remove(registration);
                    RebuildPipelineSnapshot_NoLock();
                }

                try
                {
                    using var timeoutCts = new CancellationTokenSource(_options.PluginShutdownTimeout);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    await registration.Plugin.ShutdownAsync(linked.Token).ConfigureAwait(false);
                    registration.State = PluginLifecycleState.Disposed;
                }
                catch (OperationCanceledException ex)
                {
                    registration.State = PluginLifecycleState.Faulted;
                    throw new PluginException(
                        registration.Name,
                        "shutdown",
                        $"Plugin shutdown timed out after {_options.PluginShutdownTimeout.TotalMilliseconds:F0}ms.",
                        ex);
                }
                catch (Exception ex)
                {
                    registration.State = PluginLifecycleState.Faulted;
                    throw new PluginException(
                        registration.Name,
                        "shutdown",
                        $"Plugin '{registration.Name}' failed during shutdown.",
                        ex);
                }
            }
            finally
            {
                if (enteredLifecycleGate)
                    _pluginLifecycleGate.Release();
            }
        }

        /// <summary>
        /// Gets a list of currently registered plugins.
        /// </summary>
        public IReadOnlyList<PluginDescriptor> GetRegisteredPlugins()
        {
            lock (_pluginLock)
            {
                if (_plugins.Count == 0)
                    return Array.Empty<PluginDescriptor>();

                var descriptors = new PluginDescriptor[_plugins.Count];
                for (int i = 0; i < _plugins.Count; i++)
                    descriptors[i] = _plugins[i].ToDescriptor();
                return descriptors;
            }
        }

        /// <summary>
        /// Send an HTTP request and collect the full buffered response.
        /// </summary>
        public async Task<UHttpResponse> SendBufferedAsync(
            UHttpRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            ThrowIfDisposed();

            if (request.IsClientOwned && !request.IsOwnedBy(this))
            {
                throw new InvalidOperationException(
                    "Cannot send a client-created request through a different client than the one that created it. " +
                    "Use the client that created this request, or create a new request from this client.");
            }

            request.BeginSend();

            var context = new RequestContext(request);
            context.RecordEvent("RequestStart");
            UHttpResponse response = null;

            try
            {
                var pipelineSnapshot = _pipeline ?? _transport.DispatchAsync;
                response = await BufferedDispatchBridge
                    .CollectResponseAsync(
                        pipelineSnapshot,
                        request,
                        context,
                        cancellationToken,
                        _options.MaxBufferedResponseBodySize)
                    .ConfigureAwait(false);

                context.RecordEvent("RequestComplete");
                context.Stop();
                return response;
            }
            catch (UHttpException)
            {
                response?.Dispose();
                context.RecordEvent("RequestFailed");
                context.Stop();
                throw;
            }
            catch (OperationCanceledException)
            {
                response?.Dispose();
                context.RecordEvent("RequestCancelled");
                context.Stop();
                throw;
            }
            catch (Exception ex)
            {
                response?.Dispose();
                context.RecordEvent("RequestFailed");
                context.Stop();
                throw new UHttpException(new UHttpError(UHttpErrorType.Unknown, ex.Message, ex));
            }
            finally
            {
                request.EndSend();
                context.Clear();
            }
        }

        /// <summary>
        /// Send an HTTP request and return a streaming response wrapper.
        /// </summary>
        public async Task<UHttpStreamingResponse> SendStreamingAsync(
            UHttpRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            ThrowIfDisposed();

            if (request.IsClientOwned && !request.IsOwnedBy(this))
            {
                throw new InvalidOperationException(
                    "Cannot send a client-created request through a different client than the one that created it. " +
                    "Use the client that created this request, or create a new request from this client.");
            }

            request.BeginSend();

            var context = new RequestContext(request);
            context.RecordEvent("RequestStart");
            UHttpStreamingResponse response = null;

            try
            {
                var pipelineSnapshot = _pipeline ?? _transport.DispatchAsync;
                response = await StreamingDispatchBridge
                    .CollectResponseAsync(pipelineSnapshot, request, context, cancellationToken)
                    .ConfigureAwait(false);

                context.RecordEvent("RequestComplete");
                context.Stop();
                return response;
            }
            catch (UHttpException)
            {
                response?.Dispose();
                context.RecordEvent("RequestFailed");
                context.Stop();
                throw;
            }
            catch (OperationCanceledException)
            {
                response?.Dispose();
                context.RecordEvent("RequestCancelled");
                context.Stop();
                throw;
            }
            catch (Exception ex)
            {
                response?.Dispose();
                context.RecordEvent("RequestFailed");
                context.Stop();
                throw new UHttpException(new UHttpError(UHttpErrorType.Unknown, ex.Message, ex));
            }
            finally
            {
                request.EndSend();
                context.Clear();
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            GC.SuppressFinalize(this);

            PluginRegistration[] pluginsToShutdown;
            lock (_pluginLock)
            {
                pluginsToShutdown = _plugins.ToArray();
                _plugins.Clear();
                _pipeline = null;
            }

            for (int i = pluginsToShutdown.Length - 1; i >= 0; i--)
            {
                SchedulePluginShutdownOnDispose(pluginsToShutdown[i]);
            }

            DisposeConfiguredInterceptors();

            if (_ownsTransport)
            {
                try
                {
                    _transport.Dispose();
                }
                catch (Exception ex)
                {
                    TurboDiagnostics.Log("[TurboHTTP] Transport dispose failed", ex);
                }
            }

            // _pluginLifecycleGate is deliberately not disposed (see its declaration, #178).
        }

        private void SchedulePluginShutdownOnDispose(PluginRegistration plugin)
        {
            if (plugin == null)
                return;

            plugin.State = PluginLifecycleState.ShuttingDown;
            _ = ShutdownPluginOnDisposeAsync(plugin);
        }

        private async Task ShutdownPluginOnDisposeAsync(PluginRegistration plugin)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(_options.PluginShutdownTimeout);
                await plugin.Plugin.ShutdownAsync(timeoutCts.Token).ConfigureAwait(false);
                plugin.State = PluginLifecycleState.Disposed;
            }
            catch (OperationCanceledException ex)
            {
                plugin.State = PluginLifecycleState.Faulted;
                TurboDiagnostics.Log(
                    $"[TurboHTTP] Plugin dispose timed out ({plugin.Name}) after {_options.PluginShutdownTimeout.TotalMilliseconds:F0}ms",
                    ex);
            }
            catch (Exception ex)
            {
                plugin.State = PluginLifecycleState.Faulted;
                TurboDiagnostics.Log($"[TurboHTTP] Plugin dispose failed ({plugin.Name})", ex);
            }
        }

        private Task ShutdownUnregisteredPluginAsync(PluginRegistration plugin)
        {
            plugin.State = PluginLifecycleState.ShuttingDown;
            return ShutdownPluginOnDisposeAsync(plugin);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(UHttpClient));
        }

        private PluginRegistration FindPluginByName_NoLock(string pluginName)
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                if (string.Equals(_plugins[i].Name, pluginName, StringComparison.OrdinalIgnoreCase))
                    return _plugins[i];
            }

            return null;
        }

        private void RebuildPipelineSnapshot_NoLock()
        {
            // Never resurrect a pipeline Dispose has cleared.
            if (Volatile.Read(ref _disposed) != 0)
            {
                _pipeline = null;
                return;
            }

            _pipeline = new InterceptorPipeline(BuildCombinedInterceptors_NoLock(null), _transport).Pipeline;
        }

        private List<IHttpInterceptor> BuildCombinedInterceptors_NoLock(PluginRegistration candidate)
        {
            var combined = new List<IHttpInterceptor>(_baseInterceptors.Count + 8);
            combined.AddRange(_baseInterceptors);

            for (int i = 0; i < _plugins.Count; i++)
                AddContributions(combined, _plugins[i].Interceptors);

            if (candidate != null)
                AddContributions(combined, candidate.Interceptors);

            return combined;
        }

        private static void AddContributions(List<IHttpInterceptor> combined, IReadOnlyList<IHttpInterceptor> contributions)
        {
            if (contributions == null)
                return;

            for (int j = 0; j < contributions.Count; j++)
            {
                if (contributions[j] != null)
                    combined.Add(contributions[j]);
            }
        }

        private Uri ResolveUri(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri))
                return absoluteUri;

            var baseUrl = _options.BaseUrl;
            if (string.IsNullOrEmpty(baseUrl))
            {
                throw new InvalidOperationException(
                    $"Cannot resolve relative URL '{url}' without a BaseUrl configured in UHttpClientOptions.");
            }

            var baseUri = new Uri(baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
            return new Uri(baseUri, url);
        }

        private void ApplyDefaultMetadata(UHttpRequest request, Uri uri)
        {
            request.WithMetadata(RequestMetadataKeys.FollowRedirects, _options.FollowRedirects);
            request.WithMetadata(RequestMetadataKeys.MaxRedirects, _options.MaxRedirects);
            request.WithMetadata(RequestMetadataKeys.AllowCleartext, _options.AllowCleartext);

            if (!request.Metadata.ContainsKey(RequestMetadataKeys.ProxyDisabled) &&
                !request.Metadata.ContainsKey(RequestMetadataKeys.ProxySettings))
            {
                var resolvedProxy = ProxyEnvironmentResolver.Resolve(uri, _options.Proxy);
                if (resolvedProxy != null)
                    request.WithMetadata(RequestMetadataKeys.ProxySettings, resolvedProxy);
            }
        }

        private void DisposeConfiguredInterceptors()
        {
            if (_options.Interceptors != null)
            {
                for (int i = _options.Interceptors.Count - 1; i >= 0; i--)
                {
                    DisposeComponent(_options.Interceptors[i], "Interceptor");
                }
            }
        }

        private static void DisposeComponent(object component, string label)
        {
            if (!(component is IDisposable disposable))
                return;

            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                TurboDiagnostics.Log($"[TurboHTTP] {label} dispose failed", ex);
            }
        }

        private static IReadOnlyList<IHttpInterceptor> BuildInterceptors(UHttpClientOptions options)
        {
            var interceptors = options?.Interceptors != null
                ? new List<IHttpInterceptor>(options.Interceptors)
                : new List<IHttpInterceptor>();

#if UNITY_EDITOR
            TryAppendEditorMonitorInterceptor(interceptors);
#endif

            return interceptors;
        }

#if UNITY_EDITOR
        private static void TryAppendEditorMonitorInterceptor(List<IHttpInterceptor> interceptors)
        {
            if (interceptors == null)
                return;

            bool isPlaying;
            try
            {
                isPlaying = UnityEngine.Application.isPlaying;
            }
            catch
            {
                return;
            }

            if (!isPlaying)
                return;

            // Observability registers the monitor through EditorMonitorHook (no reflection, #186);
            // without that module this is a no-op.
            EditorMonitorHook.TryAppend(interceptors);
        }
#endif
    }
}
