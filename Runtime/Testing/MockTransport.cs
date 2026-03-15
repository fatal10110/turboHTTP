using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Testing
{
    /// <summary>
    /// Mock transport for unit testing interceptor and client behavior.
    /// Returns configurable responses without making real network calls.
    /// Thread-safe for concurrent use.
    /// </summary>
    public class MockTransport : IHttpTransport
    {
        private readonly Func<UHttpRequest, RequestContext, CancellationToken, ValueTask<UHttpResponse>> _fallbackHandler;
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
        /// Number of times the transport send path has been called.
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
            _fallbackHandler = (req, ctx, ct) => new ValueTask<UHttpResponse>(
                CreateResponse(statusCode, headers, body, error, req, ctx));
        }

        /// <summary>
        /// Create a MockTransport with the default 200 OK fallback either enabled or disabled.
        /// When disabled, dispatches with no queued response report a synthetic network error.
        /// </summary>
        public MockTransport(bool useDefaultFallback)
        {
            if (!useDefaultFallback)
                return;

            _fallbackHandler = (req, ctx, ct) => new ValueTask<UHttpResponse>(
                CreateResponse(HttpStatusCode.OK, null, null, null, req, ctx));
        }

        /// <summary>
        /// Create a MockTransport with a custom handler for advanced scenarios
        /// (e.g., fail first N requests, return different responses per request).
        /// </summary>
        public MockTransport(
            Func<UHttpRequest, RequestContext, CancellationToken, ValueTask<UHttpResponse>> handler,
            bool preferValueTaskHandler = true)
        {
            // Keeps Task-returning lambda call-sites from becoming ambiguous with the Task overload.
            _ = preferValueTaskHandler;
            _fallbackHandler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>
        /// Create a MockTransport with a custom handler for advanced scenarios
        /// (e.g., fail first N requests, return different responses per request).
        /// </summary>
        public MockTransport(
            Func<UHttpRequest, RequestContext, CancellationToken, Task<UHttpResponse>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _fallbackHandler = (req, ctx, ct) => new ValueTask<UHttpResponse>(handler(req, ctx, ct));
        }

        /// <summary>
        /// Create a MockTransport with a simplified custom handler (sync, no context).
        /// </summary>
        public MockTransport(Func<UHttpRequest, UHttpResponse> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _fallbackHandler = (req, ctx, ct) => new ValueTask<UHttpResponse>(handler(req));
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
        /// Queue a transport error fixture for deterministic failure-path tests.
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

        public async ValueTask<UHttpResponse> SendAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            return await TransportDispatchHelper
                .CollectResponseAsync(this, request, context, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task DispatchAsync(
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (context == null) throw new ArgumentNullException(nameof(context));
            ThrowIfDisposed();

            context.SetState(TransportBehaviorFlags.SelfDrainsResponseBody, true);
            var safeHandler = HandlerCallbackSafetyWrapper.Wrap(handler, context);
            CaptureRequest(request);
            safeHandler.OnRequestStart(request, context);

            if (_queuedResponses.TryDequeue(out var queued))
            {
                if (queued.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(queued.Delay, cancellationToken).ConfigureAwait(false);
                }

                DriveHandler(safeHandler, queued, context);
                return;
            }

            if (_fallbackHandler == null)
            {
                safeHandler.OnResponseError(
                    new UHttpException(new UHttpError(
                        UHttpErrorType.NetworkError,
                        "MockTransport: no queued response")),
                    context);
                return;
            }

            UHttpResponse response = null;
            try
            {
                response = await _fallbackHandler(request, context, cancellationToken).ConfigureAwait(false);
                if (response == null)
                {
                    safeHandler.OnResponseError(new UHttpException(
                        new UHttpError(
                            UHttpErrorType.Unknown,
                            "MockTransport produced a null response.")),
                        context);
                    return;
                }

                DriveHandler(response, safeHandler, context);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UHttpException ex) when (ex.HttpError != null && ex.HttpError.Type == UHttpErrorType.Cancelled)
            {
                throw new OperationCanceledException(ex.HttpError.Message, ex, cancellationToken);
            }
            catch (UHttpException ex)
            {
                safeHandler.OnResponseError(ex, context);
            }
            catch (Exception ex)
            {
                safeHandler.OnResponseError(new UHttpException(
                    new UHttpError(UHttpErrorType.Unknown, ex.Message, ex)), context);
            }
            finally
            {
                response?.Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            ClearQueuedResponses();
            ClearCapturedRequests();
        }

        private void CaptureRequest(UHttpRequest request)
        {
            Interlocked.Increment(ref _requestCount);
            var snapshot = SnapshotRequest(request);
            _lastRequest = snapshot;
            _capturedRequests.Enqueue(snapshot);
        }

        private static UHttpResponse CreateResponse(
            HttpStatusCode statusCode,
            HttpHeaders headers,
            byte[] body,
            UHttpError error,
            UHttpRequest request,
            RequestContext context)
        {
            return new UHttpResponse(
                statusCode,
                headers?.Clone() ?? new HttpHeaders(),
                body != null ? (byte[])body.Clone() : null,
                context?.Elapsed ?? TimeSpan.Zero,
                request,
                error != null ? WithStatusCode(error, statusCode) : null);
        }

        private static void DriveHandler(
            IHttpHandler handler,
            QueuedResponse response,
            RequestContext context)
        {
            if (response == null)
                throw new InvalidOperationException("MockTransport produced a null queued response.");

            if (response.Error != null)
            {
                handler.OnResponseError(new UHttpException(WithStatusCode(response.Error, response.StatusCode)), context);
                return;
            }

            handler.OnResponseStart((int)response.StatusCode, response.Headers, context);
            if (response.Body != null && response.Body.Length > 0)
                handler.OnResponseData(response.Body, context);
            handler.OnResponseEnd(HttpHeaders.Empty, context);
        }

        private static void DriveHandler(
            UHttpResponse response,
            IHttpHandler handler,
            RequestContext context)
        {
            if (response == null)
                throw new InvalidOperationException("MockTransport produced a null response.");

            if (response.Error != null)
            {
                handler.OnResponseError(new UHttpException(response.Error), context);
                return;
            }

            var headers = response.Headers?.Clone() ?? new HttpHeaders();
            // Stage body segments into owned arrays before response disposal so the
            // fallback path does not depend on UHttpResponse pooled-body lifetime.
            var body = SnapshotResponseBody(response.Body);

            handler.OnResponseStart((int)response.StatusCode, headers, context);

            if (body.Count == 1)
            {
                var segment = body[0];
                if (segment.Length > 0)
                    handler.OnResponseData(segment, context);
            }
            else
            {
                for (int i = 0; i < body.Count; i++)
                {
                    var segment = body[i];
                    if (segment.Length > 0)
                        handler.OnResponseData(segment, context);
                }
            }

            handler.OnResponseEnd(HttpHeaders.Empty, context);
        }

        private static IReadOnlyList<byte[]> SnapshotResponseBody(ReadOnlySequence<byte> body)
        {
            if (body.IsEmpty)
                return Array.Empty<byte[]>();

            if (body.IsSingleSegment)
                return new List<byte[]> { body.FirstSpan.ToArray() };

            var segments = new List<byte[]>();
            foreach (ReadOnlyMemory<byte> segment in body)
            {
                if (!segment.IsEmpty)
                    segments.Add(segment.ToArray());
            }

            return segments;
        }

        private static UHttpError WithStatusCode(UHttpError error, HttpStatusCode statusCode)
        {
            if (error == null || error.StatusCode.HasValue)
                return error;

            return new UHttpError(error.Type, error.Message, error.InnerException, statusCode);
        }

        private static UHttpRequest SnapshotRequest(UHttpRequest request)
        {
            if (request == null)
                return null;

            var bodyCopy = request.Body.IsEmpty
                ? null
                : request.Body.ToArray();

            return new UHttpRequest(
                request.Method,
                request.Uri,
                request.Headers,
                bodyCopy,
                request.Timeout,
                request.Metadata);
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
