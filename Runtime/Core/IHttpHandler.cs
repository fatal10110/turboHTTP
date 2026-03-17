using System;
using System.Threading.Tasks;

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

        /// <summary>
        /// Fires when the response status line, headers, and body source are available.
        /// Implementations may read, wrap, replace, or suppress the supplied body source.
        /// Once this returns successfully, the handler owns the body source. Any subsequent
        /// body or trailer failures must surface from <see cref="IResponseBodySource"/>
        /// operations, not from <see cref="OnResponseError"/>.
        /// </summary>
        ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context);

        /// <summary>
        /// Called only if dispatch fails before <see cref="OnResponseStartAsync"/> completes
        /// successfully. After a successful response-start callback, no further handler
        /// callbacks are delivered.
        /// </summary>
        void OnResponseError(UHttpException error, RequestContext context);
    }
}
