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
        private readonly Action<IHttpInterceptor> _registerInterceptor;
        private readonly Action<string> _diagnostics;
        private readonly List<IHttpInterceptor> _registeredInterceptors = new List<IHttpInterceptor>();

        internal PluginContext(
            UHttpClientOptions optionsSnapshot,
            string pluginName,
            PluginCapabilities capabilities,
            Action<IHttpInterceptor> registerInterceptor,
            Action<string> diagnostics)
        {
            OptionsSnapshot = optionsSnapshot ?? throw new ArgumentNullException(nameof(optionsSnapshot));
            _pluginName = pluginName ?? string.Empty;
            _capabilities = capabilities;
            _registerInterceptor = registerInterceptor ?? throw new ArgumentNullException(nameof(registerInterceptor));
            _diagnostics = diagnostics;
        }

        public UHttpClientOptions OptionsSnapshot { get; }

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
            if (!canObserve && !canMutateRequest && !canMutateResponse && !canHandleErrors)
            {
                throw new PluginException(
                    _pluginName,
                    "initialize",
                    "Plugin does not have interceptor capabilities.");
            }

            var guarded = new CapabilityEnforcedInterceptor(
                _pluginName,
                interceptor,
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
            private readonly bool _canMutateRequest;
            private readonly bool _canMutateResponse;
            private readonly bool _canHandleErrors;

            public CapabilityEnforcedInterceptor(
                string pluginName,
                IHttpInterceptor inner,
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

            public async ValueTask<InterceptorRequestResult> OnRequestAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                var result = await _inner.OnRequestAsync(request, context, cancellationToken).ConfigureAwait(false);

                if (result.Action == InterceptorRequestAction.Continue)
                {
                    if (result.Request != null &&
                        !ReferenceEquals(result.Request, request) &&
                        !_canMutateRequest)
                    {
                        throw new PluginException(
                            _pluginName,
                            "interceptor.request",
                            "Plugin interceptor attempted request mutation without MutateRequests capability.");
                    }

                    return result;
                }

                if (result.Action == InterceptorRequestAction.ShortCircuit && !_canMutateResponse)
                {
                    throw new PluginException(
                        _pluginName,
                        "interceptor.request",
                        "Plugin interceptor attempted response short-circuit without MutateResponses capability.");
                }

                if (result.Action == InterceptorRequestAction.Fail && !_canHandleErrors)
                {
                    throw new PluginException(
                        _pluginName,
                        "interceptor.request",
                        "Plugin interceptor attempted failure handling without HandleErrors capability.");
                }

                return result;
            }

            public async ValueTask<InterceptorResponseResult> OnResponseAsync(
                UHttpRequest request,
                UHttpResponse response,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                var result = await _inner.OnResponseAsync(request, response, context, cancellationToken).ConfigureAwait(false);

                if (result.Action == InterceptorResponseAction.Continue)
                {
                    return result;
                }

                if (result.Action == InterceptorResponseAction.Replace)
                {
                    if (!ReferenceEquals(result.Response, response) && !_canMutateResponse)
                    {
                        throw new PluginException(
                            _pluginName,
                            "interceptor.response",
                            "Plugin interceptor attempted response mutation without MutateResponses capability.");
                    }

                    return result;
                }

                if (result.Action == InterceptorResponseAction.Fail && !_canHandleErrors)
                {
                    throw new PluginException(
                        _pluginName,
                        "interceptor.response",
                        "Plugin interceptor attempted failure handling without HandleErrors capability.");
                }

                return result;
            }
        }
    }
}
