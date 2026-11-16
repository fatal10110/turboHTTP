using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    /// <summary>
    /// Interceptor that injects <c>Accept-Encoding</c> and transparently decompresses
    /// gzip/deflate response bodies.
    /// </summary>
    /// <remarks>
    /// <c>deflate</c> is decoded as the zlib format (RFC 9110 section 8.4.1.2), with raw DEFLATE accepted when
    /// the body has no zlib header; a <c>gzip</c> body may consist of several members, each one checked.
    /// A decoded body larger than the limit (default 32 MB, <see cref="DecompressionInterceptor(bool, long)"/>)
    /// fails with a <see cref="UHttpException"/>; the same limit caps the compressed input, whether its length is
    /// known or not.
    /// <para>
    /// The codings of every <c>Content-Encoding</c> field line are combined in order (RFC 9110 section 5.3) and
    /// decoded in reverse; a response naming any other coding (for example <c>br</c>) is passed through
    /// undecoded. At most 3 stacked codings are decoded (<c>identity</c> does not count): a longer chain fails
    /// with a <see cref="UHttpException"/> before any decoder is created. The inner layers of a stacked chain may
    /// produce at most 3 times the limit in total, so nested codings cannot inflate unbounded intermediate data,
    /// and cancellation is observed while a body decodes.
    /// </para>
    /// </remarks>
    public sealed class DecompressionInterceptor : IHttpInterceptor
    {
        internal const long DefaultMaxDecompressedBodySizeBytes = 32L * 1024 * 1024;

        private readonly bool _automaticDecompression;
        private readonly long _maxDecompressedBodySizeBytes;

        /// <summary>Creates the interceptor.</summary>
        /// <param name="automaticDecompression">
        /// When false the interceptor passes requests and responses through unchanged.
        /// </param>
        /// <param name="maxDecompressedBodySizeBytes">
        /// Maximum decoded body size in bytes (default 32 MB, sized for mobile memory budgets); must be &gt; 0.
        /// A larger decoded body fails with a <see cref="UHttpException"/> once the limit is passed, and a
        /// compressed body whose known length exceeds it is rejected before decoding.
        /// </param>
        public DecompressionInterceptor(
            bool automaticDecompression = true,
            long maxDecompressedBodySizeBytes = DefaultMaxDecompressedBodySizeBytes)
        {
            if (maxDecompressedBodySizeBytes <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxDecompressedBodySizeBytes),
                    maxDecompressedBodySizeBytes,
                    "Must be > 0.");

            _automaticDecompression = automaticDecompression;
            _maxDecompressedBodySizeBytes = maxDecompressedBodySizeBytes;
        }

        /// <summary>Adds <c>Accept-Encoding</c> and decodes gzip and deflate response bodies up to the configured size limit. Thread-safe.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="next"/> is null.</exception>
        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return (request, handler, context, cancellationToken) =>
            {
                if (!_automaticDecompression)
                    return next(request, handler, context, cancellationToken);

                return InvokeAsync(request, handler, context, cancellationToken);
            };

            async Task InvokeAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                var requestForNext = request;
                // RFC 9110 sections 8.4, 14.2 and 15.3.7: a range selects bytes of the selected representation, so a
                // range of a coded representation is a slice of coded bytes no decoder can read on its own. A request
                // carrying Range or If-Range therefore asks for no content coding (#784).
                if (!request.Headers.Contains("Accept-Encoding") &&
                    !request.Headers.Contains("Range") &&
                    !request.Headers.Contains("If-Range"))
                {
                    requestForNext = request.CopyWithSharedContent();
                    requestForNext.WithHeader("Accept-Encoding", "gzip, deflate");
                    context.UpdateRequest(requestForNext);
                }

                try
                {
                    await next(
                        requestForNext,
                        new DecompressionHandler(handler, _maxDecompressedBodySizeBytes, cancellationToken),
                        context,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (!ReferenceEquals(requestForNext, request))
                        requestForNext.Dispose();

                    throw;
                }
                finally
                {
                    if (!ReferenceEquals(requestForNext, request))
                        context.UpdateRequest(request);
                }
            }
        }
    }
}
