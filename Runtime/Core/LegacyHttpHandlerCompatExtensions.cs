#if UNITY_INCLUDE_TESTS
using System;
using System.Runtime.CompilerServices;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Core
{
    public static class LegacyHttpHandlerCompatExtensions
    {
        private sealed class PendingResponse
        {
            internal int StatusCode;
            internal HttpHeaders Headers;
            internal SegmentedBuffer Body;
        }

        private static readonly ConditionalWeakTable<IHttpHandler, PendingResponse> PendingResponses =
            new ConditionalWeakTable<IHttpHandler, PendingResponse>();

        private static readonly object Gate = new object();

        public static void OnResponseStart(
            this IHttpHandler handler,
            int statusCode,
            HttpHeaders headers,
            RequestContext context)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            lock (Gate)
            {
                if (PendingResponses.TryGetValue(handler, out var previous))
                {
                    previous.Body?.Dispose();
                    PendingResponses.Remove(handler);
                }

                PendingResponses.Add(handler, new PendingResponse
                {
                    StatusCode = statusCode,
                    Headers = headers ?? new HttpHeaders(),
                    Body = new SegmentedBuffer()
                });
            }
        }

        public static void OnResponseData(
            this IHttpHandler handler,
            ReadOnlySpan<byte> chunk,
            RequestContext context)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            if (chunk.IsEmpty)
                return;

            PendingResponse pending;
            lock (Gate)
            {
                if (!PendingResponses.TryGetValue(handler, out pending))
                    throw new InvalidOperationException("Legacy response data arrived before response start.");
            }

            pending.Body.Write(chunk);
        }

        public static void OnResponseEnd(
            this IHttpHandler handler,
            HttpHeaders trailers,
            RequestContext context)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            PendingResponse pending;
            lock (Gate)
            {
                if (!PendingResponses.TryGetValue(handler, out pending))
                    throw new InvalidOperationException("Legacy response end arrived before response start.");

                PendingResponses.Remove(handler);
            }

            try
            {
                var body = pending.Body != null
                    ? pending.Body.AsSequence().ToArray()
                    : Array.Empty<byte>();
                // Test-only sync shim: the compat path always hands off a buffered in-memory source.
                // Do not use this helper with handlers that require an actual asynchronous response start.
                handler.OnResponseStartAsync(
                        pending.StatusCode,
                        pending.Headers,
                        new BufferedResponseBodySource(body, trailers ?? HttpHeaders.Empty),
                        context)
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                pending.Body?.Dispose();
            }
        }
    }
}
#endif
