using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Core
{
    /// <summary>
    /// An internal handler that buffers an <see cref="IResponseBodySource"/> into a <see cref="UHttpResponse"/>.
    /// </summary>
    internal sealed class BufferedResponseCollectorHandler : IHttpHandler
    {
        private readonly TaskCompletionSource<UHttpResponse> _tcs =
            new TaskCompletionSource<UHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationToken _cancellationToken;

        private UHttpRequest _request;

        internal BufferedResponseCollectorHandler(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            _request = request;
            _ = context ?? throw new ArgumentNullException(nameof(context));
            _cancellationToken = cancellationToken;
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

        public ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            if (body == null)
                throw new ArgumentNullException(nameof(body));

            return CollectAsync(statusCode, headers, body, context);
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            var responseError = error ?? new UHttpException(
                new UHttpError(
                    UHttpErrorType.Unknown,
                    "IHttpHandler.OnResponseError received a null error."));

            if (_tcs.TrySetException(responseError))
                return;
        }

        /// <summary>
        /// Fails the response task with the specified exception.
        /// </summary>
        internal void Fail(Exception ex)
        {
            if (ex is HandlerCallbackException handlerCallback && handlerCallback.InnerException != null)
                ex = handlerCallback.InnerException;

            if (ex is OperationCanceledException operationCanceledException)
            {
                _tcs.TrySetException(operationCanceledException);
                return;
            }

            var dispatchError = ex is UHttpException uHttpException
                ? uHttpException
                : new UHttpException(new UHttpError(UHttpErrorType.Unknown, ex?.Message ?? "Dispatch failed.", ex));

            _tcs.TrySetException(dispatchError);
        }

        /// <summary>
        /// Cancels the response task.
        /// </summary>
        internal void Cancel()
        {
            _tcs.TrySetCanceled();
        }

        internal void EnsureCompleted()
        {
            if (_tcs.Task.IsCompleted)
                return;

            _tcs.TrySetException(new InvalidOperationException(
                "Pipeline completed without delivering a response."));
        }

        private async ValueTask CollectAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            SegmentedBuffer bufferedBody = null;
            byte[] rented = null;

            try
            {
                if (body.TryGetBufferedData(out var data))
                {
                    if (!data.IsEmpty)
                    {
                        bufferedBody = new SegmentedBuffer();
                        bufferedBody.Write(data.Span);
                    }
                }
                else
                {
                    rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
                    bufferedBody = new SegmentedBuffer();
                    while (true)
                    {
                        var read = await body.ReadAsync(rented, _cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            break;

                        bufferedBody.Write(new ReadOnlySpan<byte>(rented, 0, read));
                    }
                }

                _ = await body.GetTrailersAsync(_cancellationToken).ConfigureAwait(false);

                UHttpResponse response = null;
                bool retainedRequest = false;
                bool attachedRequestRelease = false;
                try
                {
                    response = new UHttpResponse(
                        (HttpStatusCode)statusCode,
                        headers ?? new HttpHeaders(),
                        bufferedBody?.AsSequence() ?? ReadOnlySequence<byte>.Empty,
                        bufferedBody,
                        context.Elapsed,
                        _request);
                    bufferedBody = null;

                    if (_request.IsPooled)
                    {
                        _request.RetainForResponse();
                        retainedRequest = true;
                        response.AttachRequestRelease(_request.ReleaseResponseHold);
                        attachedRequestRelease = true;
                    }

                    if (!_tcs.TrySetResult(response))
                        response.Dispose();

                    response = null;
                }
                finally
                {
                    if (response != null)
                        response.Dispose();
                    if (retainedRequest && !attachedRequestRelease)
                        _request.ReleaseResponseHold();
                }
            }
            catch (Exception ex)
            {
                bufferedBody?.Dispose();
                Fail(ex);
            }
            finally
            {
                if (rented != null)
                    ArrayPool<byte>.Shared.Return(rented);

                try
                {
                    await body.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Fail(ex);
                }
            }
        }
    }
}
