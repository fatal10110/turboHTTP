using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Main HTTP client for TurboHTTP.
    /// Provides fluent verb helpers and pooled request creation.
    /// Thread-safe for concurrent use.
    /// </summary>
    public class UHttpClient : IDisposable
    {
        private const string MonitorInterceptorTypeName = "TurboHTTP.Observability.MonitorInterceptor, TurboHTTP.Observability";
        private readonly UHttpClientOptions _options;
        private readonly IHttpTransport _transport;
        private readonly bool _ownsTransport;
        private readonly IReadOnlyList<IHttpInterceptor> _baseInterceptors;
        private readonly ObjectPool<UHttpRequest> _requestPool;

        private volatile DispatchFunc _pipeline;

        private readonly SemaphoreSlim _pluginLifecycleGate = new SemaphoreSlim(1, 1);
        private readonly object _pluginLock = new object();
        private readonly List<PluginRegistration> _plugins = new List<PluginRegistration>();

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

        public UHttpClient(UHttpClientOptions options = null)
        {
            _options = options?.Clone() ?? new UHttpClientOptions();

            if (_options.Http2 == null)
                _options.Http2 = new Http2Options();

            if (_options.Http2.MaxDecodedHeaderBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(_options.Http2.MaxDecodedHeaderBytes),
                    _options.Http2.MaxDecodedHeaderBytes,
                    "Must be greater than 0.");
            }

            if (_options.Transport != null)
            {
                _transport = _options.Transport;
                _ownsTransport = _options.DisposeTransport;
            }
            else if (_options.TlsBackend != TlsBackend.Auto ||
                     !_options.ConnectionPool.IsDefault() ||
                     !_options.Http2.IsDefault())
            {
                _transport = HttpTransportFactory.CreateWithOptions(
                    _options.TlsBackend,
                    _options.ConnectionPool,
                    _options.Http2);
                _ownsTransport = true;
            }
            else
            {
                _transport = HttpTransportFactory.Default;
                _ownsTransport = false;
            }

            _baseInterceptors = BuildInterceptors(_options);
            _pipeline = new InterceptorPipeline(_baseInterceptors, _transport).Pipeline;

            var poolCapacity = Math.Max(
                16,
                (_options.ConnectionPool?.MaxConnectionsPerHost ?? PlatformConfig.RecommendedMaxConcurrency) * 4);
            _requestPool = new ObjectPool<UHttpRequest>(
                () => new UHttpRequest(this),
                poolCapacity);
        }

        /// <summary>
        /// Rent a mutable request object from the pool.
        /// Dispose the returned request to return it to the pool.
        /// </summary>
        public UHttpRequest CreateRequest(HttpMethod method, string url)
        {
            if (url == null)
                throw new ArgumentNullException(nameof(url));

            ThrowIfDisposed();

            var uri = ResolveUri(url);
            var request = _requestPool.Rent();
            try
            {
                request.ActivateLease(method, uri, _options.DefaultTimeout);
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

        public UHttpRequest Get(string url) => CreateRequest(HttpMethod.GET, url);
        public UHttpRequest Post(string url) => CreateRequest(HttpMethod.POST, url);
        public UHttpRequest Put(string url) => CreateRequest(HttpMethod.PUT, url);
        public UHttpRequest Delete(string url) => CreateRequest(HttpMethod.DELETE, url);
        public UHttpRequest Patch(string url) => CreateRequest(HttpMethod.PATCH, url);
        public UHttpRequest Head(string url) => CreateRequest(HttpMethod.HEAD, url);
        public UHttpRequest Options(string url) => CreateRequest(HttpMethod.OPTIONS, url);

        public async Task RegisterPluginAsync(IHttpPlugin plugin, CancellationToken ct = default)
        {
            if (plugin == null)
                throw new ArgumentNullException(nameof(plugin));

            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            await _pluginLifecycleGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
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
                    message => Debug.WriteLine("[TurboHTTP][Plugin:" + pluginName + "] " + message));

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

                lock (_pluginLock)
                {
                    _plugins.Add(registration);
                    RebuildPipelineSnapshot_NoLock();
                }
            }
            finally
            {
                _pluginLifecycleGate.Release();
            }
        }

        public async Task UnregisterPluginAsync(string pluginName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(pluginName))
                throw new ArgumentException("Plugin name is required.", nameof(pluginName));

            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            await _pluginLifecycleGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
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
                _pluginLifecycleGate.Release();
            }
        }

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
        /// Send an HTTP request and return the response.
        /// </summary>
        public async Task<UHttpResponse> SendAsync(
            UHttpRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            ThrowIfDisposed();

            if (request.IsPooled && !request.IsOwnedBy(this))
            {
                throw new InvalidOperationException(
                    "Cannot send a pooled request through a different client than the one that created it. " +
                    "Use the client that created this request, or create a new request from this client.");
            }

            request.BeginSend();

            var context = new RequestContext(request);
            context.RecordEvent("RequestStart");
            UHttpResponse response = null;

            try
            {
                var pipelineSnapshot = _pipeline ?? _transport.DispatchAsync;
                response = await DispatchBridge
                    .CollectResponseAsync(pipelineSnapshot, request, context, cancellationToken)
                    .ConfigureAwait(false);

                if (request.IsPooled)
                {
                    request.RetainForResponse();
                    response.AttachRequestRelease(request.ReleaseResponseHold);
                }

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
                    Debug.WriteLine($"[TurboHTTP] Transport dispose failed: {ex}");
                }
            }

            _pluginLifecycleGate.Dispose();
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
                Debug.WriteLine(
                    $"[TurboHTTP] Plugin dispose timed out ({plugin.Name}) after {_options.PluginShutdownTimeout.TotalMilliseconds:F0}ms: {ex}");
            }
            catch (Exception ex)
            {
                plugin.State = PluginLifecycleState.Faulted;
                Debug.WriteLine($"[TurboHTTP] Plugin dispose failed ({plugin.Name}): {ex}");
            }
        }

        internal void ReturnRequestToPool(UHttpRequest request)
        {
            if (request == null)
                return;

            if (Volatile.Read(ref _disposed) != 0)
                return;

            _requestPool.Return(request);
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
            var combined = new List<IHttpInterceptor>(_baseInterceptors.Count + 8);
            combined.AddRange(_baseInterceptors);

            for (int i = 0; i < _plugins.Count; i++)
            {
                var contributions = _plugins[i].Interceptors;
                if (contributions == null)
                    continue;

                for (int j = 0; j < contributions.Count; j++)
                {
                    if (contributions[j] != null)
                        combined.Add(contributions[j]);
                }
            }

            _pipeline = new InterceptorPipeline(combined, _transport).Pipeline;
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
                Debug.WriteLine($"[TurboHTTP] {label} dispose failed: {ex}");
            }
        }

        private static IReadOnlyList<IHttpInterceptor> BuildInterceptors(UHttpClientOptions options)
        {
            var interceptors = options?.Interceptors != null
                ? new List<IHttpInterceptor>(options.Interceptors)
                : new List<IHttpInterceptor>();

            TryAppendBackgroundNetworkingInterceptor(options, interceptors);
            TryAppendAdaptiveInterceptor(options, interceptors);

#if UNITY_EDITOR
            TryAppendEditorMonitorInterceptor(interceptors);
#endif

            return interceptors;
        }

        private static void TryAppendAdaptiveInterceptor(
            UHttpClientOptions options,
            List<IHttpInterceptor> interceptors)
        {
            if (options?.AdaptivePolicy == null || !options.AdaptivePolicy.Enable || interceptors == null)
                return;

            if (ContainsPipelineComponent<AdaptiveInterceptor>(interceptors))
                return;

            var detector = options.NetworkQualityDetector ?? new NetworkQualityDetector();
            options.NetworkQualityDetector = detector;
            interceptors.Insert(0, new AdaptiveInterceptor(options.AdaptivePolicy.Clone(), detector));
        }

        private static void TryAppendBackgroundNetworkingInterceptor(
            UHttpClientOptions options,
            List<IHttpInterceptor> interceptors)
        {
            if (options?.BackgroundNetworkingPolicy == null ||
                !options.BackgroundNetworkingPolicy.Enable ||
                interceptors == null)
            {
                return;
            }

            if (ContainsPipelineComponent<BackgroundNetworkingInterceptor>(interceptors))
                return;

            interceptors.Insert(0, new BackgroundNetworkingInterceptor(
                options.BackgroundNetworkingPolicy.Clone(),
                options.BackgroundExecutionBridge));
        }

        private static bool ContainsPipelineComponent<T>(IReadOnlyList<IHttpInterceptor> interceptors)
        {
            return ContainsPipelineComponent(interceptors, typeof(T));
        }

        private static bool ContainsPipelineComponent(
            IReadOnlyList<IHttpInterceptor> interceptors,
            Type componentType)
        {
            if (interceptors == null || componentType == null)
                return false;

            for (int i = 0; i < interceptors.Count; i++)
            {
                var interceptor = interceptors[i];
                if (interceptor == null)
                    continue;

                if (componentType.IsInstanceOfType(interceptor))
                    return true;

            }

            return false;
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

            try
            {
                TryAppendEditorMonitorInterceptor(interceptors, MonitorInterceptorTypeName);
            }
            catch
            {
                // Monitor auto-wiring is optional and must never break client construction.
            }
        }

        private static bool TryAppendEditorMonitorInterceptor(
            List<IHttpInterceptor> interceptors,
            string typeName)
        {
            var monitorType = Type.GetType(typeName, throwOnError: false);
            if (monitorType == null || !typeof(IHttpInterceptor).IsAssignableFrom(monitorType))
                return false;

            if (!IsMonitorCaptureEnabled(monitorType) ||
                ContainsPipelineComponent(interceptors, monitorType))
            {
                return true;
            }

            if (Activator.CreateInstance(monitorType) is IHttpInterceptor monitorInterceptor)
                interceptors.Add(monitorInterceptor);

            return true;
        }

        private static bool IsMonitorCaptureEnabled(Type monitorType)
        {
            var captureEnabledProperty = monitorType.GetProperty(
                "CaptureEnabled",
                BindingFlags.Public | BindingFlags.Static);
            if (captureEnabledProperty == null || captureEnabledProperty.PropertyType != typeof(bool))
                return false;

            var value = captureEnabledProperty.GetValue(null, null);
            return value is bool enabled && enabled;
        }
#endif
    }
}
