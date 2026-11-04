using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// A request failure that <see cref="RawSocketTransport"/> may replay on another HTTP/2 connection.
    /// Raised only before the response started and never for a handler fault, so a replay can never
    /// deliver a second <see cref="IHttpHandler.OnResponseStart"/>. When not replayed it surfaces as the
    /// <see cref="UHttpException"/> it is, with the same <see cref="UHttpError"/>.
    /// </summary>
    internal sealed class Http2RetryableRequestException : UHttpException
    {
        internal Http2RetryableRequestException(UHttpError error, bool requestNotProcessed)
            : base(error)
        {
            RequestNotProcessed = requestNotProcessed;
        }

        /// <summary>
        /// True when the peer cannot have processed the request: a stream above a GOAWAY last-stream-id,
        /// RST_STREAM(REFUSED_STREAM), or a stream that was never opened because the connection stopped
        /// accepting new streams (RFC 9113 Section 8.7). Safe to replay for any method. False when the
        /// connection failed before the response started; only idempotent requests may be replayed.
        /// </summary>
        internal bool RequestNotProcessed { get; }
    }
}
