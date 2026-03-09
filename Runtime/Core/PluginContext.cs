using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Capability-scoped context provided during plugin initialization.
    /// </summary>
    public sealed class PluginContext
    {
        private readonly string _pluginName;
        private readonly PluginCapabilities _capabilities;
        private readonly Action<IHttpInterceptor> _registerInterceptor;
        private readonly Action<string> _diagnostics;
        private readonly List<IHttpInterceptor> _registeredInterceptors = new List<IHttpInterceptor>();
        private readonly UHttpClientOptions _optionsSnapshot;

        internal PluginContext(
            UHttpClientOptions optionsSnapshot,
            string pluginName,
            PluginCapabilities capabilities,
            Action<IHttpInterceptor> registerInterceptor,
            Action<string> diagnostics)
        {
            _optionsSnapshot = optionsSnapshot?.Clone() ?? throw new ArgumentNullException(nameof(optionsSnapshot));
            _pluginName = pluginName ?? string.Empty;
            _capabilities = capabilities;
            _registerInterceptor = registerInterceptor ?? throw new ArgumentNullException(nameof(registerInterceptor));
            _diagnostics = diagnostics;
        }

        /// <summary>
        /// Mutable plugin-local snapshot captured during initialization.
        /// Mutating this instance does not affect the owning client.
        /// </summary>
        public UHttpClientOptions OptionsSnapshot => _optionsSnapshot;

        public PluginCapabilities Capabilities => _capabilities;

        internal IReadOnlyList<IHttpInterceptor> RegisteredInterceptors => _registeredInterceptors;

        public void RegisterInterceptor(IHttpInterceptor interceptor)
        {
            if (interceptor == null)
                throw new ArgumentNullException(nameof(interceptor));

            var canObserve = (_capabilities & PluginCapabilities.ObserveRequests) != 0
                || (_capabilities & PluginCapabilities.ReadOnlyMonitoring) != 0;
            var canMutateRequest = (_capabilities & PluginCapabilities.MutateRequests) != 0;
            var canMutateResponse = (_capabilities & PluginCapabilities.MutateResponses) != 0;
            var canHandleErrors = (_capabilities & PluginCapabilities.HandleErrors) != 0;
            var canRedispatch = (_capabilities & PluginCapabilities.AllowRedispatch) != 0;
            if (!canObserve && !canMutateRequest && !canMutateResponse && !canHandleErrors)
            {
                throw new PluginException(
                    _pluginName,
                    "initialize",
                    "Plugin does not have interceptor capabilities.");
            }

            if (canMutateRequest && canMutateResponse && canHandleErrors && canRedispatch)
            {
                _registerInterceptor(interceptor);
                _registeredInterceptors.Add(interceptor);
                return;
            }

            var guarded = new CapabilityEnforcedInterceptor(
                _pluginName,
                interceptor,
                _capabilities,
                canMutateRequest,
                canMutateResponse,
                canHandleErrors);
            _registerInterceptor(guarded);
            _registeredInterceptors.Add(guarded);
        }

        public void LogDiagnostic(string message)
        {
            if ((_capabilities & PluginCapabilities.Diagnostics) == 0 &&
                (_capabilities & PluginCapabilities.ReadOnlyMonitoring) == 0)
            {
                throw new PluginException(
                    _pluginName,
                    "diagnostics",
                    "Plugin does not have diagnostics capability.");
            }

            _diagnostics?.Invoke(message ?? string.Empty);
        }

        private sealed class CapabilityEnforcedInterceptor : IHttpInterceptor
        {
            private readonly string _pluginName;
            private readonly IHttpInterceptor _inner;
            private readonly PluginCapabilities _capabilities;
            private readonly bool _canMutateRequest;
            private readonly bool _canMutateResponse;
            private readonly bool _canHandleErrors;

            public CapabilityEnforcedInterceptor(
                string pluginName,
                IHttpInterceptor inner,
                PluginCapabilities capabilities,
                bool canMutateRequest,
                bool canMutateResponse,
                bool canHandleErrors)
            {
                _pluginName = pluginName ?? string.Empty;
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _capabilities = capabilities;
                _canMutateRequest = canMutateRequest;
                _canMutateResponse = canMutateResponse;
                _canHandleErrors = canHandleErrors;
            }

            public DispatchFunc Wrap(DispatchFunc next)
            {
                if (next == null)
                    throw new ArgumentNullException(nameof(next));

                return (request, handler, context, cancellationToken) =>
                    new InvocationGuard(this, next, request, handler, context).InvokeAsync(cancellationToken);
            }

            private readonly struct RequestMutationSignature
            {
                public readonly HttpMethod Method;
                public readonly Uri Uri;
                public readonly ReadOnlyMemory<byte> Body;
                public readonly TimeSpan Timeout;
                public readonly int HeaderCount;
                public readonly int HeaderHash;
                public readonly int MetadataCount;
                public readonly int MetadataHash;

                private RequestMutationSignature(UHttpRequest request)
                {
                    Method = request.Method;
                    Uri = request.Uri;
                    Body = request.Body;
                    Timeout = request.Timeout;
                    HeaderCount = request.Headers?.Count ?? 0;
                    HeaderHash = ComputeHeadersHash(request.Headers);
                    MetadataCount = request.Metadata?.Count ?? 0;
                    MetadataHash = ComputeMetadataHash(request.Metadata);
                }

                public static RequestMutationSignature Capture(UHttpRequest request)
                {
                    return request != null
                        ? new RequestMutationSignature(request)
                        : default;
                }

                public static int ComputeHash(UHttpRequest request)
                {
                    return Capture(request).ComputeHash();
                }

                public bool Equals(RequestMutationSignature other)
                {
                    return Method == other.Method &&
                           Equals(Uri, other.Uri) &&
                           Body.Equals(other.Body) &&
                           Timeout == other.Timeout &&
                           HeaderCount == other.HeaderCount &&
                           HeaderHash == other.HeaderHash &&
                           MetadataCount == other.MetadataCount &&
                           MetadataHash == other.MetadataHash;
                }

                public int ComputeHash()
                {
                    unchecked
                    {
                        var hash = 17;
                        hash = (hash * 31) ^ (int)Method;
                        hash = (hash * 31) ^ (Uri?.GetHashCode() ?? 0);
                        hash = (hash * 31) ^ Body.GetHashCode();
                        hash = (hash * 31) ^ Timeout.GetHashCode();
                        hash = (hash * 31) ^ HeaderCount;
                        hash = (hash * 31) ^ HeaderHash;
                        hash = (hash * 31) ^ MetadataCount;
                        hash = (hash * 31) ^ MetadataHash;
                        return hash;
                    }
                }

                private static int ComputeHeadersHash(HttpHeaders headers)
                {
                    if (headers == null || headers.Count == 0)
                        return 0;

                    unchecked
                    {
                        var hash = 17;
                        foreach (var name in headers.Names)
                        {
                            hash = (hash * 31) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(name);
                            var values = headers.GetValues(name);
                            for (int i = 0; i < values.Count; i++)
                                hash = (hash * 31) ^ (values[i]?.GetHashCode() ?? 0);
                        }

                        return hash;
                    }
                }

                private static int ComputeMetadataHash(IReadOnlyDictionary<string, object> metadata)
                {
                    if (metadata == null || metadata.Count == 0)
                        return 0;

                    unchecked
                    {
                        var hash = 17;
                        foreach (var pair in metadata)
                        {
                            hash = (hash * 31) ^ (pair.Key?.GetHashCode() ?? 0);
                            hash = (hash * 31) ^ (pair.Value?.GetHashCode() ?? 0);
                        }

                        return hash;
                    }
                }
            }

            private sealed class InvocationGuard : IHttpHandler
            {
                private readonly CapabilityEnforcedInterceptor _owner;
                private readonly DispatchFunc _next;
                private readonly UHttpRequest _request;
                private readonly RequestContext _context;
                private readonly IHttpHandler _innerHandler;
                private readonly RequestMutationSignature _originalSignature;
                private readonly object _responseGate = new object();
                private readonly object _faultGate = new object();

                private List<Exception> _downstreamFaults;
                private ResponseEventSignature[] _pendingObserved;
                private int _pendingHead;
                private int _pendingCount;
                private int _activeDispatchCount;
                private int _dispatchCount;
                private bool _observedBufferReleased;

                public InvocationGuard(
                    CapabilityEnforcedInterceptor owner,
                    DispatchFunc next,
                    UHttpRequest request,
                    IHttpHandler handler,
                    RequestContext context)
                {
                    _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                    _next = next ?? throw new ArgumentNullException(nameof(next));
                    _request = request;
                    _context = context;
                    _innerHandler = handler ?? throw new ArgumentNullException(nameof(handler));
                    _originalSignature = RequestMutationSignature.Capture(request);
                }

                public async Task InvokeAsync(CancellationToken cancellationToken)
                {
                    var wrapped = _owner._inner.Wrap(GuardedNextAsync);

                    try
                    {
                        try
                        {
                            await wrapped(_request, this, _context, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (PluginException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            ThrowIfUnauthorizedRequestMutation();
                            ValidateObservedResponses();

                            if (!TryConsumePassThroughFault(ex))
                                ThrowIfUnauthorizedErrorHandling(ex);

                            throw;
                        }

                        ThrowIfUnauthorizedRequestMutation();
                        ThrowIfUnauthorizedResponseShortCircuit();
                        ThrowIfUnauthorizedErrorHandling();
                        ValidateObservedResponses();
                    }
                    finally
                    {
                        ReleaseObservedBuffer();
                    }
                }

                private async Task GuardedNextAsync(
                    UHttpRequest nextRequest,
                    IHttpHandler nextHandler,
                    RequestContext nextContext,
                    CancellationToken nextToken)
                {
                    if (nextHandler == null)
                        throw new ArgumentNullException(nameof(nextHandler));

                    ThrowIfUnauthorizedRequestReplacement(nextRequest);
                    ThrowIfUnauthorizedRequestMutation();

                    if (Interlocked.Increment(ref _dispatchCount) > 1 &&
                        (_owner._capabilities & PluginCapabilities.AllowRedispatch) == 0)
                    {
                        throw new PluginException(
                            _owner._pluginName,
                            "interceptor.request",
                            "Plugin interceptor attempted re-dispatch without AllowRedispatch capability.");
                    }

                    var observedHandler = WrapObservedHandler(nextHandler);
                    Interlocked.Increment(ref _activeDispatchCount);
                    try
                    {
                        await _next(nextRequest, observedHandler, nextContext, nextToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        RecordDownstreamFault(ex);
                        throw;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeDispatchCount);
                    }
                }

                public void OnRequestStart(UHttpRequest request, RequestContext context)
                {
                    ThrowIfUnauthorizedResponseInjection();
                    RecordForwarded(ResponseEventSignature.ForRequestStart(request));
                    _innerHandler.OnRequestStart(request, context);
                }

                public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
                {
                    ThrowIfUnauthorizedResponseInjection();
                    RecordForwarded(ResponseEventSignature.ForResponseStart(statusCode, headers));
                    _innerHandler.OnResponseStart(statusCode, headers, context);
                }

                public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
                {
                    ThrowIfUnauthorizedResponseInjection();
                    RecordForwarded(ResponseEventSignature.ForResponseData(chunk));
                    _innerHandler.OnResponseData(chunk, context);
                }

                public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
                {
                    ThrowIfUnauthorizedResponseInjection();
                    RecordForwarded(ResponseEventSignature.ForResponseEnd(trailers));
                    _innerHandler.OnResponseEnd(trailers, context);
                }

                public void OnResponseError(UHttpException error, RequestContext context)
                {
                    ThrowIfUnauthorizedResponseInjection();
                    RecordForwarded(ResponseEventSignature.ForResponseError(error));
                    _innerHandler.OnResponseError(error, context);
                }

                private void ThrowIfUnauthorizedRequestReplacement(UHttpRequest nextRequest)
                {
                    if (_owner._canMutateRequest || ReferenceEquals(nextRequest, _request))
                        return;

                    throw new PluginException(
                        _owner._pluginName,
                        "interceptor.request",
                        "Plugin interceptor attempted request replacement without MutateRequests capability.");
                }

                private void ThrowIfUnauthorizedRequestMutation()
                {
                    if (_owner._canMutateRequest || _request == null)
                        return;

                    if (RequestMutationSignature.Capture(_request).Equals(_originalSignature))
                        return;

                    throw new PluginException(
                        _owner._pluginName,
                        "interceptor.request",
                        "Plugin interceptor attempted in-place request mutation without MutateRequests capability.");
                }

                private void ThrowIfUnauthorizedResponseShortCircuit()
                {
                    if (_owner._canMutateResponse || Volatile.Read(ref _dispatchCount) != 0)
                        return;

                    throw new PluginException(
                        _owner._pluginName,
                        "interceptor.response",
                        "Plugin interceptor attempted response short-circuit without MutateResponses capability.");
                }

                private void ThrowIfUnauthorizedResponseInjection()
                {
                    if (_owner._canMutateResponse || Volatile.Read(ref _activeDispatchCount) > 0)
                        return;

                    throw new PluginException(
                        _owner._pluginName,
                        "interceptor.response",
                        "Plugin interceptor attempted response injection without MutateResponses capability.");
                }

                private void ThrowIfUnauthorizedErrorHandling(Exception innerException = null)
                {
                    if (_owner._canHandleErrors)
                        return;

                    var downstreamFault = GetPendingDownstreamFault();
                    if (downstreamFault == null)
                        return;

                    throw new PluginException(
                        _owner._pluginName,
                        "interceptor.error",
                        "Plugin interceptor attempted error handling without HandleErrors capability.",
                        innerException ?? downstreamFault);
                }

                private void RecordDownstreamFault(Exception ex)
                {
                    if (ex == null)
                        return;

                    lock (_faultGate)
                    {
                        if (_downstreamFaults == null)
                            _downstreamFaults = new List<Exception>(1);

                        _downstreamFaults.Add(ex);
                    }
                }

                private bool TryConsumePassThroughFault(Exception ex)
                {
                    if (ex == null)
                        return false;

                    lock (_faultGate)
                    {
                        if (_downstreamFaults == null)
                            return false;

                        for (int i = 0; i < _downstreamFaults.Count; i++)
                        {
                            if (!ReferenceEquals(_downstreamFaults[i], ex))
                                continue;

                            _downstreamFaults.RemoveAt(i);
                            return true;
                        }

                        return false;
                    }
                }

                private Exception GetPendingDownstreamFault()
                {
                    lock (_faultGate)
                    {
                        return _downstreamFaults != null && _downstreamFaults.Count > 0
                            ? _downstreamFaults[0]
                            : null;
                    }
                }
                private IHttpHandler WrapObservedHandler(IHttpHandler inner)
                {
                    if (inner == null)
                        throw new ArgumentNullException(nameof(inner));

                    return _owner._canMutateResponse
                        ? inner
                        : new ObservedHandler(this, inner);
                }

                private void ValidateObservedResponses()
                {
                    if (_owner._canMutateResponse)
                        return;

                    lock (_responseGate)
                    {
                        ThrowIfObservedBufferReleased_NoLock();
                        if (_pendingCount != 0)
                            throw CreateMutationException();
                    }
                }

                private void RecordObserved(ResponseEventSignature signature)
                {
                    if (_owner._canMutateResponse)
                        return;

                    lock (_responseGate)
                    {
                        ThrowIfObservedBufferReleased_NoLock();
                        EnsureObservedCapacity_NoLock();
                        var index = (_pendingHead + _pendingCount) % _pendingObserved.Length;
                        _pendingObserved[index] = signature;
                        _pendingCount++;
                    }
                }

                private void RecordForwarded(ResponseEventSignature signature)
                {
                    if (_owner._canMutateResponse)
                        return;

                    lock (_responseGate)
                    {
                        ThrowIfObservedBufferReleased_NoLock();
                        if (_pendingCount == 0)
                            throw CreateMutationException();

                        var expected = _pendingObserved[_pendingHead];
                        _pendingObserved[_pendingHead] = default;
                        _pendingHead = (_pendingHead + 1) % _pendingObserved.Length;
                        _pendingCount--;

                        if (!expected.Equals(signature))
                            throw CreateMutationException();
                    }
                }

                private void EnsureObservedCapacity_NoLock()
                {
                    ThrowIfObservedBufferReleased_NoLock();

                    if (_pendingObserved == null)
                    {
                        _pendingObserved = ArrayPool<ResponseEventSignature>.Shared.Rent(4);
                        _pendingHead = 0;
                        _pendingCount = 0;
                        return;
                    }

                    if (_pendingCount < _pendingObserved.Length)
                        return;

                    var expanded = ArrayPool<ResponseEventSignature>.Shared.Rent(_pendingObserved.Length * 2);
                    for (int i = 0; i < _pendingCount; i++)
                    {
                        expanded[i] = _pendingObserved[(_pendingHead + i) % _pendingObserved.Length];
                    }

                    // ResponseEventSignature currently contains only value-type fields, so
                    // returning without clearing is safe and avoids redundant zeroing.
                    ArrayPool<ResponseEventSignature>.Shared.Return(_pendingObserved, clearArray: false);
                    _pendingObserved = expanded;
                    _pendingHead = 0;
                }

                private void ReleaseObservedBuffer()
                {
                    lock (_responseGate)
                    {
                        if (_pendingObserved == null)
                        {
                            _observedBufferReleased = true;
                            return;
                        }

                        // ResponseEventSignature currently contains only value-type fields, so
                        // returning without clearing is safe and avoids redundant zeroing.
                        ArrayPool<ResponseEventSignature>.Shared.Return(_pendingObserved, clearArray: false);
                        _pendingObserved = null;
                        _pendingHead = 0;
                        _pendingCount = 0;
                        _observedBufferReleased = true;
                    }
                }

                private void ThrowIfObservedBufferReleased_NoLock()
                {
                    if (!_observedBufferReleased)
                        return;

                    throw new PluginException(
                        _owner._pluginName,
                        "interceptor.response",
                        "Plugin interceptor attempted response observation after the dispatch scope completed.");
                }

                private PluginException CreateMutationException()
                {
                    return new PluginException(
                        _owner._pluginName,
                        "interceptor.response",
                        "Plugin interceptor attempted response mutation without MutateResponses capability.");
                }

                private sealed class ObservedHandler : IHttpHandler
                {
                    private readonly InvocationGuard _owner;
                    private readonly IHttpHandler _inner;

                    public ObservedHandler(InvocationGuard owner, IHttpHandler inner)
                    {
                        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                    }

                    public void OnRequestStart(UHttpRequest request, RequestContext context)
                    {
                        _owner.RecordObserved(ResponseEventSignature.ForRequestStart(request));
                        _inner.OnRequestStart(request, context);
                    }

                    public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
                    {
                        _owner.RecordObserved(ResponseEventSignature.ForResponseStart(statusCode, headers));
                        _inner.OnResponseStart(statusCode, headers, context);
                    }

                    public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
                    {
                        _owner.RecordObserved(ResponseEventSignature.ForResponseData(chunk));
                        _inner.OnResponseData(chunk, context);
                    }

                    public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
                    {
                        _owner.RecordObserved(ResponseEventSignature.ForResponseEnd(trailers));
                        _inner.OnResponseEnd(trailers, context);
                    }

                    public void OnResponseError(UHttpException error, RequestContext context)
                    {
                        _owner.RecordObserved(ResponseEventSignature.ForResponseError(error));
                        _inner.OnResponseError(error, context);
                    }
                }

                private readonly struct ResponseEventSignature
                {
                    private readonly int _kind;
                    private readonly int _arg0;
                    private readonly int _arg1;
                    private readonly int _arg2;

                    private ResponseEventSignature(
                        ResponseEventKind kind,
                        int arg0,
                        int arg1,
                        int arg2)
                    {
                        _kind = (int)kind;
                        _arg0 = arg0;
                        _arg1 = arg1;
                        _arg2 = arg2;
                    }

                    public static ResponseEventSignature ForRequestStart(UHttpRequest request)
                    {
                        return new ResponseEventSignature(
                            ResponseEventKind.RequestStart,
                            RequestMutationSignature.ComputeHash(request),
                            0,
                            0);
                    }

                    public static ResponseEventSignature ForResponseStart(int statusCode, HttpHeaders headers)
                    {
                        return new ResponseEventSignature(
                            ResponseEventKind.ResponseStart,
                            statusCode,
                            headers?.Count ?? 0,
                            ComputeHeadersHash(headers));
                    }

                    public static ResponseEventSignature ForResponseData(ReadOnlySpan<byte> chunk)
                    {
                        return new ResponseEventSignature(
                            ResponseEventKind.ResponseData,
                            chunk.Length,
                            ComputeDataHash(chunk),
                            0);
                    }

                    public static ResponseEventSignature ForResponseEnd(HttpHeaders trailers)
                    {
                        return new ResponseEventSignature(
                            ResponseEventKind.ResponseEnd,
                            trailers?.Count ?? 0,
                            ComputeHeadersHash(trailers),
                            0);
                    }

                    public static ResponseEventSignature ForResponseError(UHttpException error)
                    {
                        var httpError = error?.HttpError;
                        return new ResponseEventSignature(
                            ResponseEventKind.ResponseError,
                            (int)(httpError?.Type ?? UHttpErrorType.Unknown),
                            StringComparer.Ordinal.GetHashCode(httpError?.Message ?? string.Empty),
                            0);
                    }

                    public bool Equals(ResponseEventSignature other)
                    {
                        return _kind == other._kind &&
                               _arg0 == other._arg0 &&
                               _arg1 == other._arg1 &&
                               _arg2 == other._arg2;
                    }

                    private static int ComputeHeadersHash(HttpHeaders headers)
                    {
                        if (headers == null || headers.Count == 0)
                            return 0;

                        unchecked
                        {
                            var hash = 17;
                            foreach (var name in headers.Names)
                            {
                                hash = (hash * 31) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(name);
                                var values = headers.GetValues(name);
                                for (int i = 0; i < values.Count; i++)
                                    hash = (hash * 31) ^ StringComparer.Ordinal.GetHashCode(values[i] ?? string.Empty);
                            }

                            return hash;
                        }
                    }

                    private static int ComputeDataHash(ReadOnlySpan<byte> chunk)
                    {
                        unchecked
                        {
                            var hash = 17;
                            hash = (hash * 31) ^ chunk.Length;

                            var sampleCount = Math.Min(8, chunk.Length);
                            for (int i = 0; i < sampleCount; i++)
                                hash = (hash * 31) ^ chunk[i];

                            var tailStart = Math.Max(sampleCount, chunk.Length - 8);
                            for (int i = tailStart; i < chunk.Length; i++)
                                hash = (hash * 31) ^ chunk[i];

                            return hash;
                        }
                    }
                }

                private enum ResponseEventKind
                {
                    RequestStart,
                    ResponseStart,
                    ResponseData,
                    ResponseEnd,
                    ResponseError
                }
            }
        }
    }
}
