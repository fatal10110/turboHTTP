using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Delegate representing the next step in the HTTP middleware pipeline.
    /// </summary>
    public delegate Task<UHttpResponse> HttpPipelineDelegate(
        UHttpRequest request, RequestContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Interface for HTTP middleware that can intercept and transform requests/responses.
    /// Stub for Phase 4 — required now because UHttpClientOptions.Middlewares references it.
    /// </summary>
    public interface IHttpMiddleware
    {
        Task<UHttpResponse> InvokeAsync(
            UHttpRequest request, RequestContext context,
            HttpPipelineDelegate next, CancellationToken cancellationToken);
    }
}
