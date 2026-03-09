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
        private readonly object _bodyGate = new object();

        private UHttpRequest _request;
        private int _statusCode;
        private HttpHeaders _responseHeaders;
        private SegmentedBuffer _body;
        private bool _bodyClosed;

        internal ResponseCollectorHandler(UHttpRequest request, RequestContext context)
        {
            _request = request;
            _ = context ?? throw new ArgumentNullException(nameof(context));
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
            var response = new UHttpResponse(
                (HttpStatusCode)_statusCode,
                _responseHeaders ?? new HttpHeaders(),
                body?.AsSequence() ?? ReadOnlySequence<byte>.Empty,
                body,
                context.Elapsed,
                _request);

            _tcs.TrySetResult(response);
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            DisposeBody();
            _tcs.TrySetException(error ?? new UHttpException(
                new UHttpError(UHttpErrorType.Unknown, "Unknown response error.")));
        }

        internal void Fail(Exception ex)
        {
            DisposeBody();

            if (ex is HandlerCallbackException handlerCallback && handlerCallback.InnerException != null)
                ex = handlerCallback.InnerException;

            if (ex is OperationCanceledException operationCanceledException)
            {
                _tcs.TrySetException(operationCanceledException);
                return;
            }

            _tcs.TrySetException(ex is UHttpException uHttpException
                ? uHttpException
                : new UHttpException(new UHttpError(UHttpErrorType.Unknown, ex?.Message ?? "Dispatch failed.", ex)));
        }

        internal void Cancel()
        {
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
