using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Main HTTP client for TurboHTTP. Provides fluent verb methods for building
    /// and sending HTTP requests. Thread-safe for concurrent use.
    /// </summary>
    public class UHttpClient : IDisposable
    {
        private const string MonitorMiddlewareTypeName = "TurboHTTP.Observability.MonitorMiddleware, TurboHTTP.Observability";

        private readonly UHttpClientOptions _options;
        private readonly IHttpTransport _transport;
        private readonly bool _ownsTransport;
        private readonly HttpPipeline _pipeline;
        private IReadOnlyList<IHttpInterceptor> _interceptors;
        private readonly SemaphoreSlim _pluginLifecycleGate = new SemaphoreSlim(1, 1);
        private readonly object _pluginLock = new object();
        private readonly List<PluginRegistration> _plugins = new List<PluginRegistration>();
        private int _disposed; // 0 = not disposed, 1 = disposed (for Interlocked)

        /// <summary>
        /// The snapshotted options for this client (read-only access for builder).
        /// </summary>
        internal UHttpClientOptions ClientOptions => _options;

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
        /// Create a new HTTP client with optional configuration.
        /// Options are snapshotted at construction — mutations after this call
        /// have no effect. Transport is a shared reference (not cloned).
        /// </summary>
        public UHttpClient(UHttpClientOptions options = null)
        {
            _options = options?.Clone() ?? new UHttpClientOptions();

            if (_options.Http2MaxDecodedHeaderBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(_options.Http2MaxDecodedHeaderBytes),
                    _options.Http2MaxDecodedHeaderBytes,
                    "Must be greater than 0.");
            }

            if (_options.Transport != null)
            {
                _transport = _options.Transport;
                _ownsTransport = _options.DisposeTransport;
            }
            else if (_options.TlsBackend != TlsBackend.Auto ||
                _options.Http2MaxDecodedHeaderBytes != UHttpClientOptions.DefaultHttp2MaxDecodedHeaderBytes)
            {
                // Non-default TLS backend or custom transport hardening options require
                // a dedicated transport instance because the shared default singleton
                // uses default configuration.
                _transport = HttpTransportFactory.CreateWithOptions(
                    _options.TlsBackend,
                    _options.Http2MaxDecodedHeaderBytes);
                _ownsTransport = true;
            }
            else
            {
                _transport = HttpTransportFactory.Default;
                _ownsTransport = false;
            }

            _pipeline = new HttpPipeline(BuildPipelineMiddlewares(_options), _transport);
            _interceptors = BuildInterceptors(_options.Interceptors);
        }

        public UHttpRequestBuilder Get(string url)
        {
            ThrowIfDisposed();
            return new UHttpRequestBuilder(this, HttpMethod.GET, url);
        }

        public UHttpRequestBuilder Post(string url)
        {
            ThrowIfDisposed();
            return new UHttpRequestBuilder(this, HttpMethod.POST, url);
        }

        public UHttpRequestBuilder Put(string url)
        {
            ThrowIfDisposed();
            return new UHttpRequestBuilder(this, HttpMethod.PUT, url);
        }

        public UHttpRequestBuilder Delete(string url)
        {
            ThrowIfDisposed();
            return new UHttpRequestBuilder(this, HttpMethod.DELETE, url);
        }

        public UHttpRequestBuilder Patch(string url)
        {
            ThrowIfDisposed();
            return new UHttpRequestBuilder(this, HttpMethod.PATCH, url);
        }

        public UHttpRequestBuilder Head(string url)
        {
            ThrowIfDisposed();
            return new UHttpRequestBuilder(this, HttpMethod.HEAD, url);
        }

        public UHttpRequestBuilder Options(string url)
        {
            ThrowIfDisposed();
            return new UHttpRequestBuilder(this, HttpMethod.OPTIONS, url);
        }

        public async Task RegisterPluginAsync(IHttpPlugin plugin, CancellationToken ct = default)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            await _pluginLifecycleGate.WaitAsync(ct);
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

                PluginRegistration registration = null;
                try
                {
                    await plugin.InitializeAsync(context, ct);
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
                    RebuildInterceptorSnapshot_NoLock();
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

            await _pluginLifecycleGate.WaitAsync(ct);
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
                    RebuildInterceptorSnapshot_NoLock();
                }

                try
                {
                    using var timeoutCts = new CancellationTokenSource(_options.PluginShutdownTimeout);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    await registration.Plugin.ShutdownAsync(linked.Token);
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
        /// Does NOT use ConfigureAwait(false) — continuations return to the
        /// caller's SynchronizationContext (typically Unity main thread).
        /// </summary>
        public async Task<UHttpResponse> SendAsync(
            UHttpRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            ThrowIfDisposed();

            var context = new RequestContext(request);
            context.RecordEvent("RequestStart");
            var interceptorSnapshot = _interceptors;

            try
            {
                var response = interceptorSnapshot.Count == 0
                    ? await _pipeline.ExecuteAsync(request, context, cancellationToken)
                    : await ExecuteWithInterceptorsAsync(interceptorSnapshot, request, context, cancellationToken);

                context.RecordEvent("RequestComplete");
                context.Stop();

                return response;
            }
            catch (UHttpException)
            {
                context.RecordEvent("RequestFailed");
                context.Stop();
                throw;
            }
            catch (OperationCanceledException)
            {
                context.RecordEvent("RequestCancelled");
                context.Stop();
                throw;
            }
            catch (Exception ex)
            {
                context.RecordEvent("RequestFailed");
                context.Stop();
                throw new UHttpException(
                    new UHttpError(UHttpErrorType.Unknown, ex.Message, ex));
            }
            finally
            {
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
                _interceptors = BuildInterceptors(_options.Interceptors);
            }

            for (int i = pluginsToShutdown.Length - 1; i >= 0; i--)
            {
                var plugin = pluginsToShutdown[i];
                try
                {
                    plugin.State = PluginLifecycleState.ShuttingDown;
                    var shutdownTask = plugin.Plugin.ShutdownAsync(CancellationToken.None).AsTask();
                    if (!shutdownTask.Wait(_options.PluginShutdownTimeout))
                    {
                        throw new TimeoutException(
                            $"Plugin '{plugin.Name}' shutdown timed out after {_options.PluginShutdownTimeout.TotalMilliseconds:F0}ms.");
                    }

                    plugin.State = PluginLifecycleState.Disposed;
                }
                catch (Exception ex)
                {
                    plugin.State = PluginLifecycleState.Faulted;
                    Debug.WriteLine($"[TurboHTTP] Plugin dispose failed ({plugin.Name}): {ex}");
                }
            }

            if (_options.Middlewares != null)
            {
                // Dispose in reverse registration order (LIFO).
                for (int i = _options.Middlewares.Count - 1; i >= 0; i--)
                {
                    if (!(_options.Middlewares[i] is IDisposable disposable))
                        continue;

                    try
                    {
                        disposable.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[TurboHTTP] Middleware dispose failed: {ex}");
                    }
                }
            }

            // Transport is disposed last.
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

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(UHttpClient));
        }

        private async Task<UHttpResponse> ExecuteWithInterceptorsAsync(
            IReadOnlyList<IHttpInterceptor> interceptors,
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            var requestForPipeline = request;
            var enteredInterceptors = new List<IHttpInterceptor>(interceptors.Count);
            UHttpResponse response = null;

            for (int i = 0; i < interceptors.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var interceptor = interceptors[i];
                enteredInterceptors.Add(interceptor);
                context.RecordEvent("interceptor.request.enter", CreateInterceptorEventData(interceptor, i));

                InterceptorRequestResult requestResult;
                try
                {
                    requestResult = await interceptor.OnRequestAsync(
                        requestForPipeline, context, cancellationToken);
                }
                catch (Exception ex)
                {
                    requestResult = HandleRequestInterceptorException(
                        interceptor, requestForPipeline, context, ex);
                }

                context.RecordEvent("interceptor.request.exit", CreateInterceptorEventData(interceptor, i));

                if (requestResult.Action == InterceptorRequestAction.Continue)
                {
                    if (requestResult.Request != null)
                    {
                        requestForPipeline = requestResult.Request;
                        context.UpdateRequest(requestForPipeline);
                    }

                    continue;
                }

                if (requestResult.Action == InterceptorRequestAction.ShortCircuit)
                {
                    context.RecordEvent("interceptor.shortcircuit", CreateInterceptorEventData(interceptor, i));
                    response = requestResult.Response
                        ?? throw new InvalidOperationException(
                            "Interceptor returned ShortCircuit without a response.");
                    break;
                }

                throw requestResult.Exception
                    ?? new InvalidOperationException("Interceptor returned Fail without exception.");
            }

            if (response == null)
            {
                response = await _pipeline.ExecuteAsync(requestForPipeline, context, cancellationToken);
            }

            for (int i = enteredInterceptors.Count - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var interceptor = enteredInterceptors[i];
                context.RecordEvent("interceptor.response.enter", CreateInterceptorEventData(interceptor, i));

                InterceptorResponseResult responseResult;
                try
                {
                    responseResult = await interceptor.OnResponseAsync(
                        requestForPipeline, response, context, cancellationToken);
                }
                catch (Exception ex)
                {
                    responseResult = HandleResponseInterceptorException(
                        interceptor, requestForPipeline, response, context, ex);
                }

                context.RecordEvent("interceptor.response.exit", CreateInterceptorEventData(interceptor, i));

                if (responseResult.Action == InterceptorResponseAction.Continue)
                {
                    continue;
                }

                if (responseResult.Action == InterceptorResponseAction.Replace)
                {
                    response = responseResult.Response
                        ?? throw new InvalidOperationException(
                            "Interceptor returned Replace without a response.");
                    continue;
                }

                throw responseResult.Exception
                    ?? new InvalidOperationException("Interceptor returned Fail without exception.");
            }

            return response;
        }

        private InterceptorRequestResult HandleRequestInterceptorException(
            IHttpInterceptor interceptor,
            UHttpRequest request,
            RequestContext context,
            Exception exception)
        {
            context.RecordEvent("interceptor.failure", CreateFailureEventData(interceptor, "request", exception));

            switch (_options.InterceptorFailurePolicy)
            {
                case InterceptorFailurePolicy.IgnoreAndContinue:
                    return InterceptorRequestResult.Continue();
                case InterceptorFailurePolicy.ConvertToResponse:
                    return InterceptorRequestResult.ShortCircuit(
                        CreateInterceptorErrorResponse(request, context, "request", interceptor, exception));
                case InterceptorFailurePolicy.Propagate:
                default:
                    return InterceptorRequestResult.Fail(exception);
            }
        }

        private InterceptorResponseResult HandleResponseInterceptorException(
            IHttpInterceptor interceptor,
            UHttpRequest request,
            UHttpResponse currentResponse,
            RequestContext context,
            Exception exception)
        {
            context.RecordEvent("interceptor.failure", CreateFailureEventData(interceptor, "response", exception));

            switch (_options.InterceptorFailurePolicy)
            {
                case InterceptorFailurePolicy.IgnoreAndContinue:
                    return InterceptorResponseResult.Continue();
                case InterceptorFailurePolicy.ConvertToResponse:
                    return InterceptorResponseResult.Replace(
                        CreateInterceptorErrorResponse(request, context, "response", interceptor, exception));
                case InterceptorFailurePolicy.Propagate:
                default:
                    return InterceptorResponseResult.Fail(exception);
            }
        }

        private static IReadOnlyList<IHttpInterceptor> BuildInterceptors(
            IReadOnlyList<IHttpInterceptor> configuredInterceptors)
        {
            if (configuredInterceptors == null || configuredInterceptors.Count == 0)
                return Array.Empty<IHttpInterceptor>();

            var interceptors = new List<IHttpInterceptor>(configuredInterceptors.Count);
            for (int i = 0; i < configuredInterceptors.Count; i++)
            {
                if (configuredInterceptors[i] != null)
                    interceptors.Add(configuredInterceptors[i]);
            }

            return interceptors;
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

        private void RebuildInterceptorSnapshot_NoLock()
        {
            var combined = new List<IHttpInterceptor>();
            if (_options.Interceptors != null)
            {
                for (int i = 0; i < _options.Interceptors.Count; i++)
                {
                    if (_options.Interceptors[i] != null)
                        combined.Add(_options.Interceptors[i]);
                }
            }

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

            _interceptors = combined.Count == 0
                ? Array.Empty<IHttpInterceptor>()
                : combined;
        }

        private static Dictionary<string, object> CreateInterceptorEventData(IHttpInterceptor interceptor, int index)
        {
            return new Dictionary<string, object>
            {
                ["id"] = GetInterceptorId(interceptor),
                ["index"] = index
            };
        }

        private static Dictionary<string, object> CreateFailureEventData(
            IHttpInterceptor interceptor,
            string phase,
            Exception exception)
        {
            return new Dictionary<string, object>
            {
                ["id"] = GetInterceptorId(interceptor),
                ["phase"] = phase,
                ["exceptionType"] = exception.GetType().Name
            };
        }

        private static string GetInterceptorId(IHttpInterceptor interceptor)
        {
            return interceptor?.GetType().FullName ?? "unknown";
        }

        private static UHttpResponse CreateInterceptorErrorResponse(
            UHttpRequest request,
            RequestContext context,
            string phase,
            IHttpInterceptor interceptor,
            Exception exception)
        {
            var message = $"Interceptor failure during {phase} phase ({GetInterceptorId(interceptor)}): {exception.Message}";
            var error = new UHttpError(UHttpErrorType.Unknown, message, exception, statusCode: System.Net.HttpStatusCode.InternalServerError);
            return new UHttpResponse(
                statusCode: System.Net.HttpStatusCode.InternalServerError,
                headers: new HttpHeaders(),
                body: Array.Empty<byte>(),
                elapsedTime: context.Elapsed,
                request: request,
                error: error);
        }

        private static IReadOnlyList<IHttpMiddleware> BuildPipelineMiddlewares(UHttpClientOptions options)
        {
            var middlewares = options?.Middlewares != null
                ? new List<IHttpMiddleware>(options.Middlewares)
                : new List<IHttpMiddleware>();

            TryAppendBackgroundNetworkingMiddleware(options, middlewares);
            TryAppendAdaptiveMiddleware(options, middlewares);

#if UNITY_EDITOR
            TryAppendEditorMonitorMiddleware(middlewares);
#endif

            return middlewares;
        }

        private static void TryAppendAdaptiveMiddleware(
            UHttpClientOptions options,
            List<IHttpMiddleware> middlewares)
        {
            if (options?.AdaptivePolicy == null || !options.AdaptivePolicy.Enable || middlewares == null)
                return;

            for (int i = 0; i < middlewares.Count; i++)
            {
                if (middlewares[i] is AdaptiveMiddleware)
                    return;
            }

            var detector = options.NetworkQualityDetector ?? new NetworkQualityDetector();
            options.NetworkQualityDetector = detector;
            middlewares.Insert(0, new AdaptiveMiddleware(options.AdaptivePolicy.Clone(), detector));
        }

        private static void TryAppendBackgroundNetworkingMiddleware(
            UHttpClientOptions options,
            List<IHttpMiddleware> middlewares)
        {
            if (options?.BackgroundNetworkingPolicy == null ||
                !options.BackgroundNetworkingPolicy.Enable ||
                middlewares == null)
            {
                return;
            }

            for (int i = 0; i < middlewares.Count; i++)
            {
                if (middlewares[i] is BackgroundNetworkingMiddleware)
                    return;
            }

            middlewares.Insert(0, new BackgroundNetworkingMiddleware(
                options.BackgroundNetworkingPolicy.Clone(),
                options.BackgroundExecutionBridge));
        }

#if UNITY_EDITOR
        private static void TryAppendEditorMonitorMiddleware(List<IHttpMiddleware> middlewares)
        {
            if (middlewares == null)
            {
                return;
            }

            bool isPlaying;
            try
            {
                // Some test paths construct clients on worker threads; Unity API calls
                // are main-thread only in the editor. Skip monitor auto-wiring there.
                isPlaying = UnityEngine.Application.isPlaying;
            }
            catch
            {
                return;
            }

            if (!isPlaying)
            {
                return;
            }

            try
            {
                var monitorType = Type.GetType(MonitorMiddlewareTypeName, throwOnError: false);
                if (monitorType == null || !typeof(IHttpMiddleware).IsAssignableFrom(monitorType))
                {
                    return;
                }

                if (!IsMonitorCaptureEnabled(monitorType))
                {
                    return;
                }

                for (int i = 0; i < middlewares.Count; i++)
                {
                    var middleware = middlewares[i];
                    if (middleware != null && monitorType.IsInstanceOfType(middleware))
                    {
                        return;
                    }
                }

                if (Activator.CreateInstance(monitorType) is IHttpMiddleware monitorMiddleware)
                {
                    // Append to capture final request/response payload that reaches transport.
                    middlewares.Add(monitorMiddleware);
                }
            }
            catch
            {
                // Monitor auto-wiring is optional and must never break client construction.
            }
        }

        private static bool IsMonitorCaptureEnabled(Type monitorType)
        {
            var captureEnabledProperty = monitorType.GetProperty(
                "CaptureEnabled",
                BindingFlags.Public | BindingFlags.Static);
            if (captureEnabledProperty == null || captureEnabledProperty.PropertyType != typeof(bool))
            {
                return false;
            }

            var value = captureEnabledProperty.GetValue(null, null);
            return value is bool enabled && enabled;
        }
#endif
    }
}
