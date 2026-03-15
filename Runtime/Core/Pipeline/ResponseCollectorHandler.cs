using System;
using System.Buffers;
using System.Net;
using System.Threading.Tasks;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Core
{
    /// <summary>
    /// An internal handler that acts as a bridge between the push-based <see cref="IHttpHandler"/> model
    /// and the pull-based returned <see cref="UHttpResponse"/>. It collects response data into a single object.
    /// </summary>
    internal sealed class ResponseCollectorHandler : IHttpHandler
    {
        private readonly TaskCompletionSource<UHttpResponse> _tcs =
            new TaskCompletionSource<UHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _bodyGate = new object();

        private UHttpRequest _request;
        private int _statusCode;
        private HttpHeaders _responseHeaders;
        private SegmentedBuffer _body;
        private UHttpResponse _bufferedResponse;
        private bool _bodyClosed;

        internal ResponseCollectorHandler(UHttpRequest request, RequestContext context)
        {
            _request = request;
            _ = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// Gets the task that completes with the fully collected HTTP response.
        /// </summary>
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
            BufferResponse(new UHttpResponse(
                (HttpStatusCode)_statusCode,
                _responseHeaders ?? new HttpHeaders(),
                body?.AsSequence() ?? ReadOnlySequence<byte>.Empty,
                body,
                context.Elapsed,
                _request));
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            var responseError = error ?? new UHttpException(
                new UHttpError(
                    UHttpErrorType.Unknown,
                    "IHttpHandler.OnResponseError received a null error."));

            if (_tcs.TrySetException(responseError))
                DisposeBufferedState();
        }

        /// <summary>
        /// Fails the response task with the specified exception, disposing of any collected body data.
        /// </summary>
        internal void Fail(Exception ex)
        {
            if (ex is HandlerCallbackException handlerCallback && handlerCallback.InnerException != null)
                ex = handlerCallback.InnerException;

            if (ex is OperationCanceledException operationCanceledException)
            {
                if (_tcs.TrySetException(operationCanceledException))
                    DisposeBufferedState();
                return;
            }

            var dispatchError = ex is UHttpException uHttpException
                ? uHttpException
                : new UHttpException(new UHttpError(UHttpErrorType.Unknown, ex?.Message ?? "Dispatch failed.", ex));

            if (_tcs.TrySetException(dispatchError))
                DisposeBufferedState();
        }

        /// <summary>
        /// Cancels the response task, disposing of any collected body data.
        /// </summary>
        internal void Cancel()
        {
            if (_tcs.TrySetCanceled())
                DisposeBufferedState();
        }

        /// <summary>
        /// Ensures that the response task is completed, faulting it if it is not.
        /// This is a safety net to prevent hanging when the pipeline completes without delivering a response.
        /// </summary>
        internal void CompleteBufferedResponse()
        {
            if (_tcs.Task.IsCompleted)
                return;

            var response = DetachBufferedResponse();
            if (response != null)
            {
                if (!_tcs.TrySetResult(response))
                    response.Dispose();
                return;
            }

            DisposeBody();
            _tcs.TrySetException(new InvalidOperationException(
                "Pipeline completed without delivering a response."));
        }

        internal void EnsureCompleted()
        {
            if (_tcs.Task.IsCompleted)
                return;

            DisposeBufferedState();
            _tcs.TrySetException(new InvalidOperationException(
                "Pipeline completed without delivering a response."));
        }

        private void DisposeBufferedState()
        {
            DisposeBody();
            DetachBufferedResponse()?.Dispose();
        }

        private void BufferResponse(UHttpResponse response)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response));

            lock (_bodyGate)
            {
                if (_tcs.Task.IsCompleted)
                {
                    response.Dispose();
                    return;
                }

                _bufferedResponse = response;
            }
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

        private void DisposeBody()
        {
            DetachBody()?.Dispose();
        }

        private UHttpResponse DetachBufferedResponse()
        {
            lock (_bodyGate)
            {
                var response = _bufferedResponse;
                _bufferedResponse = null;
                return response;
            }
        }
    }
}
