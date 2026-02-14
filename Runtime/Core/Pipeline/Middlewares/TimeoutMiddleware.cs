using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Middleware that enforces request timeouts.
    /// Uses request.Timeout to determine the timeout duration.
    /// Returns a 408 response on timeout (does not throw).
    /// </summary>
    public class TimeoutMiddleware : IHttpMiddleware
    {
        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            using (var timeoutCts = new CancellationTokenSource(request.Timeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token))
            {
                try
                {
                    return await next(request, context, linkedCts.Token);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    // Timeout occurred (not user cancellation).
                    // Must check user token first: if both fired simultaneously,
                    // user intent takes precedence over a coincidental timeout.
                    var error = new UHttpError(
                        UHttpErrorType.Timeout,
                        $"Request timeout after {request.Timeout.TotalSeconds}s"
                    );

                    return new UHttpResponse(
                        System.Net.HttpStatusCode.RequestTimeout,
                        new HttpHeaders(),
                        null,
                        context.Elapsed,
                        request,
                        error
                    );
                }
            }
        }
    }
}
