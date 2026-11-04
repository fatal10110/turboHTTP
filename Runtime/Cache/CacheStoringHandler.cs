using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Cache
{
    internal sealed class CacheStoringHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly CacheInterceptor _owner;
        private readonly HttpMethod _requestMethod;
        private readonly Uri _requestUri;
        private readonly HttpHeaders _requestHeaders;
        private readonly string _baseKey;
        private readonly long _maxCacheableResponseBodyBytes;

        internal CacheStoringHandler(
            IHttpHandler inner,
            CacheInterceptor owner,
            UHttpRequest request,
            string baseKey,
            RequestContext context)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            _requestMethod = request.Method;
            _requestUri = request.Uri;
            _requestHeaders = request.Headers.Clone();
            _baseKey = baseKey ?? throw new ArgumentNullException(nameof(baseKey));
            _maxCacheableResponseBodyBytes = owner.MaxCacheableResponseBodyBytes;
            _ = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            _inner.OnRequestStart(request, context);
        }

        public async ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            var capturedHeaders = headers?.Clone() ?? new HttpHeaders();
            if (!CacheInterceptor.ShouldConsiderForStorage(_requestMethod, statusCode))
            {
                await _inner.OnResponseStartAsync(statusCode, capturedHeaders, body, context).ConfigureAwait(false);
                return;
            }

            if (body == null)
            {
                await _inner.OnResponseStartAsync(statusCode, capturedHeaders, body, context).ConfigureAwait(false);
                return;
            }

            if (body.Length.HasValue && body.Length.Value > _maxCacheableResponseBodyBytes)
            {
                await _inner.OnResponseStartAsync(statusCode, capturedHeaders, body, context).ConfigureAwait(false);
                return;
            }

            // RFC 9111 section 4.2.3 response_time: the headers have arrived.
            var responseTimeUtc = _owner.GetUtcNow();
            if (body.TryGetBufferedData(out var buffered))
            {
                if ((long)buffered.Length > _maxCacheableResponseBodyBytes)
                {
                    await _inner.OnResponseStartAsync(statusCode, capturedHeaders, body, context).ConfigureAwait(false);
                    return;
                }

                // A fully buffered body has its trailers already (#155). Read them before the consumer can
                // consume or dispose the body; when they cannot be read the response is delivered but not stored.
                HttpHeaders trailersToStore;
                try
                {
                    var trailers = await body.GetTrailersAsync(CancellationToken.None).ConfigureAwait(false);
                    trailersToStore = trailers == null || trailers.Count == 0 ? null : trailers.Clone();
                }
                catch (Exception ex)
                {
                    TurboDiagnostics.Log("[TurboHTTP][Cache] Response not stored: trailers unavailable", ex);
                    await _inner.OnResponseStartAsync(statusCode, capturedHeaders, body, context).ConfigureAwait(false);
                    return;
                }

                // The consumer owns capturedHeaders (UHttpResponse.Headers) and may mutate it while the background
                // store runs; the store gets its own copy, taken before the consumer sees the headers (#61).
                var headersToStore = capturedHeaders.Clone();
                SegmentedBuffer bodyToStore = null;
                if (!buffered.IsEmpty)
                {
                    bodyToStore = new SegmentedBuffer();
                    bodyToStore.Write(buffered.Span);
                }

                try
                {
                    await _inner.OnResponseStartAsync(statusCode, capturedHeaders, body, context).ConfigureAwait(false);
                }
                catch
                {
                    bodyToStore?.Dispose();
                    throw;
                }

                try
                {
                    _owner.QueueStoreResponse(
                        _requestMethod,
                        _requestUri,
                        _requestHeaders,
                        _baseKey,
                        statusCode,
                        headersToStore,
                        bodyToStore,
                        trailersToStore,
                        responseTimeUtc);
                    bodyToStore = null;
                }
                catch (Exception ex)
                {
                    bodyToStore?.Dispose();
                    TurboDiagnostics.Log("[TurboHTTP][Cache] Failed to queue cache store", ex);
                }

                return;
            }

            var tee = new TeeBodySource(
                body,
                _owner,
                _requestMethod,
                _requestUri,
                _requestHeaders,
                _baseKey,
                statusCode,
                capturedHeaders,
                responseTimeUtc,
                _maxCacheableResponseBodyBytes);

            await _inner.OnResponseStartAsync(statusCode, capturedHeaders, tee, context).ConfigureAwait(false);
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            _inner.OnResponseError(error, context);
        }
    }

    internal sealed class TeeBodySource : IResponseBodySource
    {
        private static readonly TimeSpan TrailerLoadTimeout = TimeSpan.FromSeconds(2);

        private readonly IResponseBodySource _inner;
        private readonly CacheInterceptor _owner;
        private readonly HttpMethod _requestMethod;
        private readonly Uri _requestUri;
        private readonly HttpHeaders _requestHeaders;
        private readonly string _baseKey;
        private readonly int _statusCode;
        private readonly HttpHeaders _responseHeaders;
        private readonly DateTime _responseTimeUtc;
        private readonly long _maxCacheableResponseBodyBytes;

        private IStreamingCacheAccumulator _accumulator;
        private long _accumulatedBytes;
        private HttpHeaders _trailers;
        // Copy for the store, taken before the consumer receives _trailers (#61/#155); null when there are none.
        private HttpHeaders _trailersToStore;
        private int _trailersLoaded;
        private int _completedNaturally;
        private int _aborted;
        private int _disposed;

        internal TeeBodySource(
            IResponseBodySource inner,
            CacheInterceptor owner,
            HttpMethod requestMethod,
            Uri requestUri,
            HttpHeaders requestHeaders,
            string baseKey,
            int statusCode,
            HttpHeaders responseHeaders,
            DateTime responseTimeUtc,
            long maxCacheableResponseBodyBytes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _requestMethod = requestMethod;
            _requestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
            _requestHeaders = requestHeaders?.Clone() ?? new HttpHeaders();
            _baseKey = baseKey ?? throw new ArgumentNullException(nameof(baseKey));
            _statusCode = statusCode;
            _responseHeaders = responseHeaders?.Clone() ?? new HttpHeaders();
            _responseTimeUtc = responseTimeUtc;
            if (maxCacheableResponseBodyBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCacheableResponseBodyBytes));

            _maxCacheableResponseBodyBytes = maxCacheableResponseBodyBytes;
            _accumulator = owner.CreateStreamingAccumulator();
        }

        public long? Length => _inner.Length;

        public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            data = default;
            return false;
        }

        public bool TryDetachBufferedBody(out DetachedBufferedBody body)
        {
            body = default;
            return false;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            ThrowIfDisposed();

            try
            {
                var read = await _inner.ReadAsync(destination, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    Volatile.Write(ref _completedNaturally, 1);
                    return 0;
                }

                Accumulate(destination.Slice(0, read).Span);
                return read;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                Abort();
                throw;
            }
        }

        public async ValueTask DrainAsync(CancellationToken ct)
        {
            ThrowIfDisposed();

            byte[] rented = null;
            try
            {
                rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
                while (await ReadAsync(rented, ct).ConfigureAwait(false) != 0)
                {
                }
            }
            finally
            {
                if (rented != null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public void Abort()
        {
            if (Interlocked.Exchange(ref _aborted, 1) != 0)
                return;

            DetachAccumulator();
            _inner.Abort();
        }

        public async ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
        {
            ThrowIfDisposed();

            if (Volatile.Read(ref _trailersLoaded) != 0)
                return _trailers ?? HttpHeaders.Empty;

            try
            {
                SetLoadedTrailers(await _inner.GetTrailersAsync(ct).ConfigureAwait(false));
                return _trailers ?? HttpHeaders.Empty;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                Abort();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                if (Volatile.Read(ref _aborted) == 0 &&
                    Volatile.Read(ref _completedNaturally) != 0 &&
                    Volatile.Read(ref _accumulator) != null &&
                    await EnsureTrailersLoadedForStoreAsync().ConfigureAwait(false))
                {
                    var bodyToStore = DetachAccumulatorForStore();
                    if (bodyToStore == null)
                        return;

                    try
                    {
                        _owner.QueueStoreResponse(
                            _requestMethod,
                            _requestUri,
                            _requestHeaders,
                            _baseKey,
                            _statusCode,
                            _responseHeaders,
                            bodyToStore,
                            _trailersToStore,
                            _responseTimeUtc);
                        bodyToStore = null;
                    }
                    catch (Exception ex)
                    {
                        TurboDiagnostics.Log("[TurboHTTP][Cache] Failed to queue cache store", ex);
                    }
                    finally
                    {
                        bodyToStore?.Dispose();
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _accumulator, null)?.Dispose();
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void Accumulate(ReadOnlySpan<byte> chunk)
        {
            if (_accumulator == null || chunk.IsEmpty)
                return;

            if (_accumulatedBytes > _maxCacheableResponseBodyBytes - chunk.Length)
            {
                DetachAccumulator();
                return;
            }

            try
            {
                _accumulator.Write(chunk);
                _accumulatedBytes += chunk.Length;
            }
            catch (Exception ex)
            {
                TurboDiagnostics.Log("[TurboHTTP][Cache] Tee accumulator detached after write failure", ex);
                DetachAccumulator();
            }
        }

        private async ValueTask<bool> EnsureTrailersLoadedForStoreAsync()
        {
            if (Volatile.Read(ref _trailersLoaded) != 0)
                return true;

            CancellationTokenSource timeoutCts = null;
            try
            {
                timeoutCts = new CancellationTokenSource(TrailerLoadTimeout);
                SetLoadedTrailers(await _inner.GetTrailersAsync(timeoutCts.Token).ConfigureAwait(false));
                return true;
            }
            catch
            {
                Abort();
                return false;
            }
            finally
            {
                timeoutCts?.Dispose();
            }
        }

        private void SetLoadedTrailers(HttpHeaders trailers)
        {
            _trailers = trailers;
            _trailersToStore = trailers == null || trailers.Count == 0 ? null : trailers.Clone();
            Volatile.Write(ref _trailersLoaded, 1);
        }

        private void DetachAccumulator()
        {
            Interlocked.Exchange(ref _accumulator, null)?.Dispose();
        }

        private SegmentedBuffer DetachAccumulatorForStore()
        {
            var accumulator = Interlocked.Exchange(ref _accumulator, null);
            if (accumulator == null)
                return null;

            try
            {
                return accumulator.DetachBuffer();
            }
            finally
            {
                accumulator.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(TeeBodySource));
        }
    }
}
