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
        private readonly bool _automaticDecompression;

        public DecompressionInterceptor(bool automaticDecompression = true)
        {
            _automaticDecompression = automaticDecompression;
        }

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return (request, handler, context, cancellationToken) =>
            {
                if (!_automaticDecompression)
                    return next(request, handler, context, cancellationToken);

                var requestForNext = request;
                if (!request.Headers.Contains("Accept-Encoding"))
                {
                    requestForNext = request.Clone();
                    requestForNext.WithHeader("Accept-Encoding", "gzip, deflate");
                    context.UpdateRequest(requestForNext);
                }

                return next(
                    requestForNext,
                    new DecompressionHandler(handler),
                    context,
                    cancellationToken);
            };
        }
    }
}
