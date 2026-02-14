using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Executes a chain of middleware followed by the transport layer.
    /// The delegate chain is built once at construction and reused across requests.
    /// </summary>
    public class HttpPipeline
    {
        private readonly IReadOnlyList<IHttpMiddleware> _middlewares;
        private readonly IHttpTransport _transport;
        private readonly HttpPipelineDelegate _pipeline;

        public HttpPipeline(IEnumerable<IHttpMiddleware> middlewares, IHttpTransport transport)
        {
            _middlewares = middlewares?.ToList() ?? new List<IHttpMiddleware>();
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _pipeline = BuildPipeline();
        }

        /// <summary>
        /// Execute the pipeline for a given request.
        /// </summary>
        public Task<UHttpResponse> ExecuteAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            return _pipeline(request, context, cancellationToken);
        }

        private HttpPipelineDelegate BuildPipeline()
        {
            // Start with the transport as the final step
            HttpPipelineDelegate pipeline = (req, ctx, ct) =>
                _transport.SendAsync(req, ctx, ct);

            // Wrap each middleware in reverse order so they execute in list order:
            // Request flow:  M[0] -> M[1] -> M[2] -> Transport
            // Response flow: Transport -> M[2] -> M[1] -> M[0]
            for (int i = _middlewares.Count - 1; i >= 0; i--)
            {
                var middleware = _middlewares[i];
                var next = pipeline;

                pipeline = (req, ctx, ct) =>
                    middleware.InvokeAsync(req, ctx, next, ct);
            }

            return pipeline;
        }
    }
}
