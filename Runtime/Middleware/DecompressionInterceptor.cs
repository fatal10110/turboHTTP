using System;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    /// <summary>
    /// Interceptor that injects <c>Accept-Encoding</c> and transparently decompresses
    /// gzip/deflate response bodies.
    /// </summary>
    public sealed class DecompressionInterceptor : IHttpInterceptor
    {
        internal const long DefaultMaxDecompressedBodySizeBytes = 100L * 1024 * 1024;

        private readonly bool _automaticDecompression;
        private readonly long _maxDecompressedBodySizeBytes;

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
                if (!request.Headers.Contains("Accept-Encoding"))
                {
                    requestForNext = request.Clone();
                    requestForNext.WithHeader("Accept-Encoding", "gzip, deflate");
                    context.UpdateRequest(requestForNext);
                }

                try
                {
                    await next(
                        requestForNext,
                        new DecompressionHandler(handler, _maxDecompressedBodySizeBytes),
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
