using System;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Receives synchronous lifecycle callbacks for a single HTTP dispatch.
    /// </summary>
    public interface IHttpHandler
    {
        /// <summary>
        /// Fires before any network I/O. Always the first callback for a dispatch attempt.
        /// Interceptors that transparently re-dispatch (for example redirect/retry) may invoke this
        /// multiple times before a single terminal response path completes.
        /// </summary>
        void OnRequestStart(UHttpRequest request, RequestContext context);

        /// <summary>Fires when the response status line and headers are available.</summary>
        void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context);

        /// <summary>
        /// Fires for each chunk of response body data.
        /// The span is valid only for the duration of this call.
        /// Callers must copy data they wish to retain beyond this invocation.
        /// </summary>
        void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context);

        /// <summary>Fires when the response is fully received. Trailers may be empty.</summary>
        void OnResponseEnd(HttpHeaders trailers, RequestContext context);

        /// <summary>
        /// May be called at any point after <c>OnRequestStart</c>, including after
        /// <c>OnResponseStart</c> and after partial <c>OnResponseData</c> delivery
        /// (partial response error mid-transfer).
        /// Implementations must handle all callback orderings.
        /// After this fires, no further callbacks will be delivered.
        /// </summary>
        void OnResponseError(UHttpException error, RequestContext context);
    }
}
