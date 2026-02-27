using System;
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
        private readonly Action<IHttpMiddleware> _registerMiddleware;
        private readonly Action<string> _diagnostics;
        private readonly List<IHttpMiddleware> _registeredMiddlewares = new List<IHttpMiddleware>();
        private readonly UHttpClientOptions _optionsSnapshot;

        internal PluginContext(
            UHttpClientOptions optionsSnapshot,
            string pluginName,
            PluginCapabilities capabilities,
            Action<IHttpMiddleware> registerMiddleware,
            Action<string> diagnostics)
        {
            _optionsSnapshot = optionsSnapshot?.Clone() ?? throw new ArgumentNullException(nameof(optionsSnapshot));
            _pluginName = pluginName ?? string.Empty;
            _capabilities = capabilities;
            _registerMiddleware = registerMiddleware ?? throw new ArgumentNullException(nameof(registerMiddleware));
            _diagnostics = diagnostics;
        }

        public UHttpClientOptions OptionsSnapshot => _optionsSnapshot.Clone();

        public PluginCapabilities Capabilities => _capabilities;

        internal IReadOnlyList<IHttpMiddleware> RegisteredMiddlewares => _registeredMiddlewares;

        public void RegisterMiddleware(IHttpMiddleware middleware)
        {
            if (middleware == null)
                throw new ArgumentNullException(nameof(middleware));

            var canObserve = (_capabilities & PluginCapabilities.ObserveRequests) != 0
                || (_capabilities & PluginCapabilities.ReadOnlyMonitoring) != 0;
            var canMutateRequest = (_capabilities & PluginCapabilities.MutateRequests) != 0;
            var canMutateResponse = (_capabilities & PluginCapabilities.MutateResponses) != 0;
            var canHandleErrors = (_capabilities & PluginCapabilities.HandleErrors) != 0;
            if (!canObserve && !canMutateRequest && !canMutateResponse && !canHandleErrors)
            {
                throw new PluginException(
                    _pluginName,
                    "initialize",
                    "Plugin does not have middleware capabilities.");
            }

            var guarded = new CapabilityEnforcedMiddleware(
                _pluginName,
                middleware,
                canMutateRequest,
                canMutateResponse,
                canHandleErrors);
            _registerMiddleware(guarded);
            _registeredMiddlewares.Add(guarded);
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

        private sealed class CapabilityEnforcedMiddleware : IHttpMiddleware
        {
            private readonly string _pluginName;
            private readonly IHttpMiddleware _inner;
            private readonly bool _canMutateRequest;
            private readonly bool _canMutateResponse;
            private readonly bool _canHandleErrors;

            public CapabilityEnforcedMiddleware(
                string pluginName,
                IHttpMiddleware inner,
                bool canMutateRequest,
                bool canMutateResponse,
                bool canHandleErrors)
            {
                _pluginName = pluginName ?? string.Empty;
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _canMutateRequest = canMutateRequest;
                _canMutateResponse = canMutateResponse;
                _canHandleErrors = canHandleErrors;
            }

            public async ValueTask<UHttpResponse> InvokeAsync(
                UHttpRequest request,
                RequestContext context,
                HttpPipelineDelegate next,
                CancellationToken cancellationToken)
            {
                var originalSignature = request != null
                    ? new RequestMutationSignature(request)
                    : default;

                var nextCalled = false;
                UHttpResponse downstreamResponse = null;
                var signatureAfterNext = default(RequestMutationSignature);
                var capturedSignatureAfterNext = false;

                async ValueTask<UHttpResponse> GuardedNext(
                    UHttpRequest nextRequest,
                    RequestContext nextContext,
                    CancellationToken nextToken)
                {
                    if (!_canMutateRequest && !ReferenceEquals(nextRequest, request))
                    {
                        throw new PluginException(
                            _pluginName,
                            "middleware.request",
                            "Plugin middleware attempted request replacement without MutateRequests capability.");
                    }

                    if (!_canMutateRequest && request != null && !originalSignature.Matches(request))
                    {
                        throw new PluginException(
                            _pluginName,
                            "middleware.request",
                            "Plugin middleware attempted request mutation without MutateRequests capability.");
                    }

                    if (nextCalled)
                    {
                        throw new InvalidOperationException("Plugin middleware called next more than once.");
                    }

                    nextCalled = true;
                    downstreamResponse = await next(nextRequest, nextContext, nextToken).ConfigureAwait(false);
                    if (!_canMutateRequest && request != null)
                    {
                        signatureAfterNext = new RequestMutationSignature(request);
                        capturedSignatureAfterNext = true;
                    }
                    return downstreamResponse;
                }

                UHttpResponse response;
                try
                {
                    response = await _inner.InvokeAsync(request, context, GuardedNext, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (PluginException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (!_canHandleErrors)
                    {
                        throw new PluginException(
                            _pluginName,
                            "middleware.error",
                            "Plugin middleware attempted error handling without HandleErrors capability.",
                            ex);
                    }

                    throw;
                }

                if (!_canMutateRequest && request != null)
                {
                    var mutated = !nextCalled
                        ? !originalSignature.Matches(request)
                        : capturedSignatureAfterNext && !signatureAfterNext.Matches(request);
                    if (mutated)
                    {
                        throw new PluginException(
                            _pluginName,
                            "middleware.request",
                            "Plugin middleware attempted request mutation without MutateRequests capability.");
                    }
                }

                if (!_canMutateResponse)
                {
                    if (!nextCalled)
                    {
                        throw new PluginException(
                            _pluginName,
                            "middleware.response",
                            "Plugin middleware attempted response short-circuit without MutateResponses capability.");
                    }

                    if (!ReferenceEquals(response, downstreamResponse))
                    {
                        throw new PluginException(
                            _pluginName,
                            "middleware.response",
                            "Plugin middleware attempted response mutation without MutateResponses capability.");
                    }
                }

                return response;
            }

            private readonly struct RequestMutationSignature
            {
                public readonly HttpMethod Method;
                public readonly Uri Uri;
                public readonly byte[] Body;
                public readonly TimeSpan Timeout;
                public readonly int HeaderCount;
                public readonly int HeaderHash;
                public readonly int MetadataCount;
                public readonly int MetadataHash;

                public RequestMutationSignature(UHttpRequest request)
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

                public bool Matches(UHttpRequest request)
                {
                    if (request == null)
                        return false;

                    if (request.Method != Method)
                        return false;
                    if (!Equals(request.Uri, Uri))
                        return false;
                    if (!ReferenceEquals(request.Body, Body))
                        return false;
                    if (request.Timeout != Timeout)
                        return false;
                    if ((request.Headers?.Count ?? 0) != HeaderCount)
                        return false;
                    if (ComputeHeadersHash(request.Headers) != HeaderHash)
                        return false;
                    if ((request.Metadata?.Count ?? 0) != MetadataCount)
                        return false;
                    if (ComputeMetadataHash(request.Metadata) != MetadataHash)
                        return false;

                    return true;
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
        }
    }
}
