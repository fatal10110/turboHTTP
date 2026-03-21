using System;
using System.Buffers;
using System.Diagnostics;
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

            if (body.TryGetBufferedData(out var buffered))
            {
                SegmentedBuffer bodyToStore = null;
                if ((long)buffered.Length <= _maxCacheableResponseBodyBytes && !buffered.IsEmpty)
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

                if ((long)buffered.Length > _maxCacheableResponseBodyBytes)
                    return;

                try
                {
                    _owner.QueueStoreResponse(
                        _requestMethod,
                        _requestUri,
                        _requestHeaders,
                        _baseKey,
                        statusCode,
                        capturedHeaders,
                        bodyToStore);
                    bodyToStore = null;
                }
                catch (Exception ex)
                {
                    bodyToStore?.Dispose();
                    Debug.WriteLine("[TurboHTTP][Cache] Failed to queue cache store: " + ex);
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
        private readonly IResponseBodySource _inner;
        private readonly CacheInterceptor _owner;
        private readonly HttpMethod _requestMethod;
        private readonly Uri _requestUri;
        private readonly HttpHeaders _requestHeaders;
        private readonly string _baseKey;
        private readonly int _statusCode;
        private readonly HttpHeaders _responseHeaders;
        private readonly long _maxCacheableResponseBodyBytes;

        private SegmentedBuffer _accumulator = new SegmentedBuffer();
        private long _accumulatedBytes;
        private HttpHeaders _trailers;
        private bool _trailersLoaded;
        private bool _completedNaturally;
        private bool _aborted;
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
            if (maxCacheableResponseBodyBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCacheableResponseBodyBytes));

            _maxCacheableResponseBodyBytes = maxCacheableResponseBodyBytes;
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
                    _completedNaturally = true;
                    return 0;
                }

                Accumulate(destination.Slice(0, read).Span);
                return read;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
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
            if (_aborted)
                return;

            _aborted = true;
            DetachAccumulator();
            _inner.Abort();
        }

        public async ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
        {
            ThrowIfDisposed();

            if (_trailersLoaded)
                return _trailers ?? HttpHeaders.Empty;

            try
            {
                _trailers = await _inner.GetTrailersAsync(ct).ConfigureAwait(false);
                _trailersLoaded = true;
                return _trailers ?? HttpHeaders.Empty;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
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
                if (!_aborted &&
                    _completedNaturally &&
                    _accumulator != null &&
                    await EnsureTrailersLoadedForStoreAsync().ConfigureAwait(false))
                {
                    var bodyToStore = _accumulator;
                    _accumulator = null;

                    try
                    {
                        _owner.QueueStoreResponse(
                            _requestMethod,
                            _requestUri,
                            _requestHeaders,
                            _baseKey,
                            _statusCode,
                            _responseHeaders,
                            bodyToStore);
                        bodyToStore = null;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[TurboHTTP][Cache] Failed to queue cache store: " + ex);
                    }
                    finally
                    {
                        bodyToStore?.Dispose();
                    }
                }
            }
            finally
            {
                _accumulator?.Dispose();
                _accumulator = null;
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
                Debug.WriteLine("[TurboHTTP][Cache] Tee accumulator detached after write failure: " + ex);
                DetachAccumulator();
            }
        }

        private async ValueTask<bool> EnsureTrailersLoadedForStoreAsync()
        {
            if (_trailersLoaded)
                return true;

            try
            {
                _trailers = await _inner.GetTrailersAsync(CancellationToken.None).ConfigureAwait(false);
                _trailersLoaded = true;
                return true;
            }
            catch
            {
                Abort();
                return false;
            }
        }

        private void DetachAccumulator()
        {
            _accumulator?.Dispose();
            _accumulator = null;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(TeeBodySource));
        }
    }
}
