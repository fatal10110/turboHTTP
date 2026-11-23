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
        private readonly object _completionGate = new object();
        private readonly CancellationToken _cancellationToken;
        private readonly long _maxBodyBytes;

        // Largest declared body that sizes its first segment (below the 85 KB large-object threshold);
        // larger or unknown bodies use SegmentedBuffer.DefaultSegmentSize segments.
        internal const int MaxPresizedBodyBytes = 64 * 1024;

        // Buffer for the end-of-body read once a declared body has filled its segment exactly.
        private const int EndProbeBytes = 256;

        private UHttpRequest _request;
        private UHttpResponse _stagedResponse;
        private bool _completionClaimed;

        internal BufferedResponseCollectorHandler(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken,
            long maxBodyBytes = UHttpClientOptions.DefaultMaxBufferedResponseBodySize)
        {
            _request = request;
            _ = context ?? throw new ArgumentNullException(nameof(context));
            _cancellationToken = cancellationToken;
            _maxBodyBytes = maxBodyBytes;
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

            if (!TryClaimCompletion(out var stagedResponse))
                return;

            stagedResponse?.Dispose();
            if (Internal.CancellationClassification.TryGetCallerCancellation(
                    responseError, _cancellationToken, out var canceled))
            {
                _tcs.TrySetException(canceled);
                return;
            }

            _tcs.TrySetException(responseError);
        }

        /// <summary>
        /// Fails the response task with the specified exception.
        /// </summary>
        internal void Fail(Exception ex)
        {
            if (ex is HandlerCallbackException handlerCallback && handlerCallback.InnerException != null)
                ex = handlerCallback.InnerException;

            if (!TryClaimCompletion(out var stagedResponse))
                return;

            stagedResponse?.Dispose();

            if (ex is OperationCanceledException operationCanceledException)
            {
                _tcs.TrySetException(operationCanceledException);
                return;
            }

            if (Internal.CancellationClassification.TryGetCallerCancellation(ex, _cancellationToken, out var canceled))
            {
                _tcs.TrySetException(canceled);
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
            if (!TryClaimCompletion(out var stagedResponse))
                return;

            stagedResponse?.Dispose();
            _tcs.TrySetCanceled();
        }

        internal void EnsureCompleted()
        {
            if (!TryClaimCompletion(out var stagedResponse))
                return;

            if (stagedResponse != null)
            {
                _tcs.TrySetResult(stagedResponse);
                return;
            }

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
            DetachedBufferedBody detachedBody = default;
            byte[] rented = null;
            bool detached = false;
            IDisposable detachedOwner = null;
            bool detachedOwnershipTransferred = false;
            HttpHeaders trailers = HttpHeaders.Empty;

            try
            {
                // The buffered cap applies here, where the bytes accumulate in memory; streamed bodies
                // are not capped (UHttpClientOptions.MaxBufferedResponseBodySize, 0 = unlimited).
                if (body.TryDetachBufferedBody(out detachedBody))
                {
                    detached = true;
                    ThrowIfOverBufferedLimit(detachedBody.Sequence.Length);
                }
                else if (body.TryGetBufferedData(out var data))
                {
                    ThrowIfOverBufferedLimit(data.Length);
                    if (!data.IsEmpty)
                    {
                        bufferedBody = new SegmentedBuffer();
                        bufferedBody.Write(data.Span);
                    }
                }
                else
                {
                    // Reads land in the segment memory itself (no intermediate copy, #1265). The declared
                    // length only sizes the first segment, bounded by MaxPresizedBodyBytes and the cap; the
                    // loop still reads until the source reports the end, so a short or long body behaves
                    // as it did with a fixed read buffer (framing is enforced by the source).
                    var declaredLength = body.Length;
                    var sizeHint = InitialSegmentSize(declaredLength);
                    bufferedBody = new SegmentedBuffer();
                    long totalRead = 0;
                    while (true)
                    {
                        int read;
                        if (declaredLength.HasValue
                            && totalRead == declaredLength.Value
                            && bufferedBody.WritableCapacity == 0)
                        {
                            // The declared body is in and its segment is full: look for the end through a
                            // small buffer instead of starting a segment that would stay empty.
                            rented ??= ByteArrayPool.Rent(EndProbeBytes);
                            read = await body.ReadAsync(rented, _cancellationToken).ConfigureAwait(false);
                            if (read == 0)
                                break;

                            totalRead += read;
                            ThrowIfOverBufferedLimit(totalRead);
                            bufferedBody.Write(new ReadOnlySpan<byte>(rented, 0, read));
                            continue;
                        }

                        var destination = bufferedBody.GetWriteMemory(sizeHint);
                        sizeHint = 0;
                        read = await body.ReadAsync(destination, _cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            break;

                        totalRead += read;
                        ThrowIfOverBufferedLimit(totalRead);
                        bufferedBody.Advance(read);
                    }
                }

                if (!detached)
                    trailers = await body.GetTrailersAsync(_cancellationToken).ConfigureAwait(false);

                UHttpResponse response = null;
                bool retainedRequest = false;
                bool attachedRequestRelease = false;
                try
                {
                    if (detached)
                    {
                        detachedOwner = detachedBody.DetachOwner();
                        response = new UHttpResponse(
                            (HttpStatusCode)statusCode,
                            headers ?? new HttpHeaders(),
                            detachedBody.Sequence,
                            detachedOwner,
                            context.Elapsed,
                            _request,
                            // Sources refuse to detach when the message has trailers
                            // (IResponseBodySource.TryDetachBufferedBody), so there are none here.
                            trailers: HttpHeaders.Empty);
                        detachedOwnershipTransferred = true;
                        detachedOwner = null;
                    }
                    else
                    {
                        response = new UHttpResponse(
                            (HttpStatusCode)statusCode,
                            headers ?? new HttpHeaders(),
                            bufferedBody?.AsSequence() ?? ReadOnlySequence<byte>.Empty,
                            bufferedBody,
                            context.Elapsed,
                            _request,
                            trailers: trailers);
                    }

                    bufferedBody = null;

                    if (_request.IsClientOwned)
                    {
                        _request.RetainForResponse();
                        retainedRequest = true;
                        response.AttachRequestRelease(_request.ReleaseResponseHold);
                        attachedRequestRelease = true;
                    }

                    StageResponse(response);
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
                detachedOwner?.Dispose();
                // Safe with detachedOwner.Dispose(): DetachOwner() transfers the shared owner token
                // out of detachedBody, so DisposeOwnedResources() becomes a no-op after transfer.
                if (detached && !detachedOwnershipTransferred)
                    detachedBody.DisposeOwnedResources();
                Fail(ex);
            }
            finally
            {
                if (rented != null)
                    ByteArrayPool.Return(rented);

                if (!detached)
                {
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

        /// <summary>
        /// First segment size for a read-loop body: the declared length when it exceeds the default segment
        /// but stays within <see cref="MaxPresizedBodyBytes"/> and the buffered cap, so a known small body
        /// lands in one segment through one read; 0 (the default segment size) otherwise. The length is
        /// peer-declared, so it never sizes more than <see cref="MaxPresizedBodyBytes"/>.
        /// </summary>
        private int InitialSegmentSize(long? declaredLength)
        {
            if (!declaredLength.HasValue)
                return 0;

            var length = declaredLength.Value;
            if (length <= SegmentedBuffer.DefaultSegmentSize || length > MaxPresizedBodyBytes)
                return 0;
            if (_maxBodyBytes > 0 && length > _maxBodyBytes)
                return 0;

            return (int)length;
        }

        private void ThrowIfOverBufferedLimit(long bufferedBytes)
        {
            if (_maxBodyBytes > 0 && bufferedBytes > _maxBodyBytes)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError,
                    $"Response body exceeds maximum size for a buffered response ({_maxBodyBytes} bytes, " +
                    "UHttpClientOptions.MaxBufferedResponseBodySize)"));
            }
        }

        private void StageResponse(UHttpResponse response)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response));

            var disposeResponse = false;
            lock (_completionGate)
            {
                if (_completionClaimed || _stagedResponse != null)
                {
                    disposeResponse = true;
                }
                else
                {
                    _stagedResponse = response;
                }
            }

            if (disposeResponse)
                response.Dispose();
        }

        private bool TryClaimCompletion(out UHttpResponse stagedResponse)
        {
            lock (_completionGate)
            {
                if (_completionClaimed)
                {
                    stagedResponse = null;
                    return false;
                }

                _completionClaimed = true;
                stagedResponse = _stagedResponse;
                _stagedResponse = null;
                return true;
            }
        }
    }
}
