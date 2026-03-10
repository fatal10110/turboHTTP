using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    internal sealed class TestInterceptorPipeline
    {
        private readonly InterceptorPipeline _pipeline;

        internal TestInterceptorPipeline(IReadOnlyList<IHttpInterceptor> interceptors, IHttpTransport transport)
        {
            _pipeline = new InterceptorPipeline(
                interceptors ?? throw new ArgumentNullException(nameof(interceptors)),
                transport ?? throw new ArgumentNullException(nameof(transport)));
        }

        internal Task<UHttpResponse> ExecuteAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            return TransportDispatchHelper.CollectResponseAsync(
                _pipeline.Pipeline,
                request,
                context,
                cancellationToken);
        }
    }
}
