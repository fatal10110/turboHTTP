using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// #892: what an HTTP/2 negative test checks on the <see cref="UHttpException"/> a failed request
    /// throws, instead of accepting any exception of that type. Each method asserts the error type and the
    /// HTTP/2 detail a regression would change: the protocol error code behind the failure, and whether
    /// the request is marked as not processed (RFC 9113 Section 8.7) and so replayable for any method.
    /// </summary>
    internal static class Http2FailureAssert
    {
        /// <summary>
        /// A protocol error (stream or connection, RFC 9113 Section 5.4) the client detected: a
        /// <see cref="UHttpErrorType.NetworkError"/> whose cause is the <see cref="Http2ProtocolException"/>
        /// carrying <paramref name="code"/>, the code the client sent in its RST_STREAM or GOAWAY. The
        /// peer may have processed the request, so it is never marked as not processed.
        /// </summary>
        internal static void IsProtocolError(UHttpException ex, Http2ErrorCode code)
        {
            Assert.IsNotNull(ex, "Expected a UHttpException.");
            Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type, ex.ToString());

            var protocol = ex.HttpError.InnerException as Http2ProtocolException;
            Assert.IsNotNull(protocol,
                "Expected the Http2ProtocolException that caused the failure, got " +
                (ex.HttpError.InnerException?.GetType().Name ?? "no cause") + ": " + ex);
            Assert.AreEqual(code, protocol.ErrorCode, ex.ToString());
            IsNotMarkedUnprocessed(ex);
        }

        /// <summary>
        /// A request the peer cannot have processed: a stream above a GOAWAY last-stream-id, a
        /// RST_STREAM(REFUSED_STREAM) before the response, or a request never sent because the
        /// connection stopped accepting streams (RFC 9113 Sections 6.8 and 8.7). It is a retryable
        /// <see cref="UHttpErrorType.NetworkError"/> marked so the transport may replay it on another
        /// connection for any method.
        /// </summary>
        internal static void IsNotProcessed(UHttpException ex, string reason)
        {
            Assert.IsNotNull(ex, "Expected a UHttpException.");
            Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type, ex.ToString());
            Assert.IsTrue(ex.HttpError.IsRetryable(), ex.ToString());

            var retryable = ex as Http2RetryableRequestException;
            Assert.IsNotNull(retryable,
                "Expected the replay marker (Http2RetryableRequestException), got " + ex.GetType().Name + ": " + ex);
            Assert.IsTrue(retryable.RequestNotProcessed, "The request must be marked as not processed: " + ex);
            StringAssert.Contains(reason, ex.HttpError.Message);
        }

        /// <summary>
        /// The request may have been processed, so nothing marks it as safe to replay for any method.
        /// </summary>
        internal static void IsNotMarkedUnprocessed(UHttpException ex)
        {
            Assert.IsFalse(ex is Http2RetryableRequestException retryable && retryable.RequestNotProcessed,
                "A request the peer may have processed must not be marked as not processed: " + ex);
        }
    }
}
