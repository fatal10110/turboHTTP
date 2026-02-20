using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Testing
{
    /// <summary>
    /// Mock transport for unit testing middleware and client behavior.
    /// Returns configurable responses without making real network calls.
    /// Thread-safe for concurrent use.
    /// </summary>
    public class MockTransport : IHttpTransport
    {
        private readonly Func<UHttpRequest, RequestContext, CancellationToken, Task<UHttpResponse>> _fallbackHandler;
        private readonly ConcurrentQueue<QueuedResponse> _queuedResponses = new ConcurrentQueue<QueuedResponse>();
        private readonly ConcurrentQueue<UHttpRequest> _capturedRequests = new ConcurrentQueue<UHttpRequest>();
        private int _requestCount;
        private volatile UHttpRequest _lastRequest;
        private int _disposed;

        private sealed class QueuedResponse
        {
            public readonly HttpStatusCode StatusCode;
            public readonly HttpHeaders Headers;
            public readonly byte[] Body;
            public readonly UHttpError Error;
            public readonly TimeSpan Delay;

            public QueuedResponse(
                HttpStatusCode statusCode,
                HttpHeaders headers,
                byte[] body,
                UHttpError error,
                TimeSpan delay)
            {
                StatusCode = statusCode;
                Headers = headers?.Clone() ?? new HttpHeaders();
                Body = body != null ? (byte[])body.Clone() : null;
                Error = error;
                Delay = delay;
            }
        }

        /// <summary>
        /// Number of times SendAsync has been called.
        /// </summary>
        public int RequestCount => Interlocked.CompareExchange(ref _requestCount, 0, 0);

        /// <summary>
        /// The most recent request received by this transport.
        /// </summary>
        public UHttpRequest LastRequest => _lastRequest;

        /// <summary>
        /// Snapshot of captured requests in send order.
        /// </summary>
        public IReadOnlyList<UHttpRequest> CapturedRequests => _capturedRequests.ToArray();

        /// <summary>
        /// Create a MockTransport that returns the specified status code with
        /// optional headers, body, and error.
        /// </summary>
        public MockTransport(
            HttpStatusCode statusCode = HttpStatusCode.OK,
            HttpHeaders headers = null,
            byte[] body = null,
            UHttpError error = null)
        {
            _fallbackHandler = (req, ctx, ct) => Task.FromResult(
                CreateResponse(statusCode, headers, body, error, req, ctx));
        }

        /// <summary>
        /// Create a MockTransport with a custom handler for advanced scenarios
        /// (e.g., fail first N requests, return different responses per request).
        /// </summary>
        public MockTransport(
            Func<UHttpRequest, RequestContext, CancellationToken, Task<UHttpResponse>> handler)
        {
            _fallbackHandler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>
        /// Create a MockTransport with a simplified custom handler (sync, no context).
        /// </summary>
        public MockTransport(Func<UHttpRequest, UHttpResponse> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _fallbackHandler = (req, ctx, ct) => Task.FromResult(handler(req));
        }

        /// <summary>
        /// Queue a deterministic response fixture.
        /// </summary>
        public void EnqueueResponse(
            HttpStatusCode statusCode = HttpStatusCode.OK,
            HttpHeaders headers = null,
            byte[] body = null,
            UHttpError error = null,
            TimeSpan? delay = null)
        {
            var effectiveDelay = delay ?? TimeSpan.Zero;
            if (effectiveDelay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be >= 0.");

            _queuedResponses.Enqueue(new QueuedResponse(
                statusCode,
                headers,
                body,
                error,
                effectiveDelay));
        }

        /// <summary>
        /// Queue a JSON response fixture using the project JSON facade.
        /// </summary>
        public void EnqueueJsonResponse<T>(
            T payload,
            HttpStatusCode statusCode = HttpStatusCode.OK,
            TimeSpan? delay = null)
        {
            var json = SerializeViaProjectJson(payload, typeof(T));
            var headers = new HttpHeaders();
            headers.Set("Content-Type", "application/json");
            EnqueueResponse(
                statusCode,
                headers,
                Encoding.UTF8.GetBytes(json),
                error: null,
                delay: delay);
        }

        /// <summary>
        /// Queue an error response fixture for deterministic error-path tests.
        /// </summary>
        public void EnqueueError(
            UHttpError error,
            HttpStatusCode statusCode = HttpStatusCode.InternalServerError,
            HttpHeaders headers = null,
            byte[] body = null,
            TimeSpan? delay = null)
        {
            if (error == null) throw new ArgumentNullException(nameof(error));
            EnqueueResponse(statusCode, headers, body, error, delay);
        }

        /// <summary>
        /// Clear all queued response fixtures.
        /// </summary>
        public void ClearQueuedResponses()
        {
            while (_queuedResponses.TryDequeue(out _))
            {
            }
        }

        /// <summary>
        /// Clear captured request history.
        /// </summary>
        public void ClearCapturedRequests()
        {
            while (_capturedRequests.TryDequeue(out _))
            {
            }
        }

        public async Task<UHttpResponse> SendAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            Interlocked.Increment(ref _requestCount);
            _lastRequest = request;
            _capturedRequests.Enqueue(request);

            cancellationToken.ThrowIfCancellationRequested();

            if (_queuedResponses.TryDequeue(out var queued))
            {
                if (queued.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(queued.Delay, cancellationToken).ConfigureAwait(false);
                }

                return CreateResponse(
                    queued.StatusCode,
                    queued.Headers,
                    queued.Body,
                    queued.Error,
                    request,
                    context);
            }

            if (_fallbackHandler != null)
            {
                return await _fallbackHandler(request, context, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                "MockTransport has no queued responses and no fallback handler. " +
                "Call EnqueueResponse/EnqueueJsonResponse before sending.");
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            ClearQueuedResponses();
            ClearCapturedRequests();
        }

        private static UHttpResponse CreateResponse(
            HttpStatusCode statusCode,
            HttpHeaders headers,
            byte[] body,
            UHttpError error,
            UHttpRequest request,
            RequestContext context)
        {
            var responseHeaders = headers?.Clone() ?? new HttpHeaders();
            var responseBody = body != null ? (byte[])body.Clone() : null;
            return new UHttpResponse(
                statusCode,
                responseHeaders,
                responseBody,
                context?.Elapsed ?? TimeSpan.Zero,
                request,
                error);
        }

        private static string SerializeViaProjectJson(object payload, Type payloadType)
        {
            return ProjectJsonBridge.Serialize(
                payload,
                payloadType,
                requiredBy: "MockTransport.EnqueueJsonResponse(...)");
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(MockTransport));
        }
    }
}
