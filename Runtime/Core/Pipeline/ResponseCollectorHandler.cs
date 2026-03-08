using System;
using System.Buffers;
using System.Net;
using System.Threading.Tasks;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Core
{
    internal sealed class ResponseCollectorHandler : IHttpHandler
    {
        private readonly TaskCompletionSource<UHttpResponse> _tcs =
            new TaskCompletionSource<UHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RequestContext _context;
        private readonly object _bodyGate = new object();

        private UHttpRequest _request;
        private int _statusCode;
        private HttpHeaders _responseHeaders;
        private SegmentedBuffer _body;
        private bool _bodyClosed;

        internal ResponseCollectorHandler(UHttpRequest request, RequestContext context)
        {
            _request = request;
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public Task<UHttpResponse> ResponseTask => _tcs.Task;

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            if (request != null)
                _request = request;
        }

        public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
        {
            _statusCode = statusCode;
            _responseHeaders = headers;
        }

        public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
        {
            if (chunk.IsEmpty)
                return;

            lock (_bodyGate)
            {
                if (_bodyClosed)
                    return;

                if (_body == null)
                    _body = new SegmentedBuffer();

                _body.Write(chunk);
            }
        }

        public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
        {
            var body = DetachBody();

            var responseError = _context.GetResponseError();
            var response = new UHttpResponse(
                (HttpStatusCode)_statusCode,
                _responseHeaders ?? new HttpHeaders(),
                body?.AsSequence() ?? ReadOnlySequence<byte>.Empty,
                body,
                _context.Elapsed,
                _request,
                responseError);

            _tcs.TrySetResult(response);
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            if (TrySetStoredCancellationException())
                return;

            DisposeBody();
            _tcs.TrySetException(error ?? new UHttpException(new UHttpError(UHttpErrorType.Unknown, "Unknown response error.")));
        }

        internal void Fail(Exception ex)
        {
            if (TrySetStoredCancellationException())
                return;

            // Exact-type check intentionally keeps plain cancellation as a canceled task while
            // preserving derived OperationCanceledException subtypes (for example
            // BackgroundRequestQueuedException) as typed exceptions with attached metadata.
            if (ex is TaskCanceledException ||
                (ex != null && ex.GetType() == typeof(OperationCanceledException)))
            {
                Cancel();
                return;
            }

            if (ex is OperationCanceledException operationCanceled)
            {
                DisposeBody();
                _tcs.TrySetException(operationCanceled);
                return;
            }

            DisposeBody();
            _tcs.TrySetException(ex is UHttpException uHttpException
                ? uHttpException
                : new UHttpException(new UHttpError(UHttpErrorType.Unknown, ex?.Message ?? "Dispatch failed.", ex)));
        }

        internal void Cancel()
        {
            if (TrySetStoredCancellationException())
                return;

            DisposeBody();
            _tcs.TrySetCanceled();
        }

        internal void EnsureCompleted()
        {
            if (!_tcs.Task.IsCompleted)
            {
                DisposeBody();
                _tcs.TrySetException(new InvalidOperationException(
                    "Pipeline completed without delivering a response."));
            }
        }

        private void DisposeBody()
        {
            SegmentedBuffer body;
            lock (_bodyGate)
            {
                body = _body;
                _body = null;
                _bodyClosed = true;
            }

            body?.Dispose();
        }

        private bool TrySetStoredCancellationException()
        {
            var stored = _context.GetCancellationException();
            if (stored == null)
                return false;

            DisposeBody();
            _tcs.TrySetException(stored);
            return true;
        }

        private SegmentedBuffer DetachBody()
        {
            lock (_bodyGate)
            {
                var body = _body;
                _body = null;
                _bodyClosed = true;
                return body;
            }
        }
    }
}
