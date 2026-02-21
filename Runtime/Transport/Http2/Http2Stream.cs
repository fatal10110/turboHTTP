using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http2
{
    internal enum Http2StreamState
    {
        Idle,
        Open,
        HalfClosedLocal,
        HalfClosedRemote,
        Closed
    }

    /// <summary>
    /// Represents a single HTTP/2 stream — one request/response pair multiplexed
    /// on a shared TCP connection. RFC 7540 Section 5.1.
    /// </summary>
    internal class Http2Stream : IDisposable
    {
        public int StreamId { get; }
        public UHttpRequest Request { get; }
        public RequestContext Context { get; }

        public Http2StreamState State { get; set; }

        public int StatusCode { get; set; }
        public HttpHeaders ResponseHeaders { get; set; }
        public int ResponseBodyLength => _responseBodyLength;
        public int ResponseBodyCapacity => _responseBodyBuffer?.Length ?? 0;

        /// <summary>
        /// Buffer for accumulating HEADERS + CONTINUATION header blocks.
        /// Uses MemoryStream instead of List&lt;byte&gt; for efficient bulk appending.
        /// </summary>
        public MemoryStream HeaderBlockBuffer { get; }
        public bool HeadersReceived { get; set; }
        public bool PendingEndStream { get; set; }

        private byte[] _responseBodyBuffer;
        private int _responseBodyLength;
        private int _disposed;
        private int _responseCompleted;
        private readonly PoolableValueTaskSource<UHttpResponse> _responseSource;
        private readonly ValueTask<UHttpResponse> _responseTask;

        private int _sendWindowSize;
        public int SendWindowSize
        {
            get => Interlocked.CompareExchange(ref _sendWindowSize, 0, 0);
            set => Interlocked.Exchange(ref _sendWindowSize, value);
        }

        public int AdjustSendWindowSize(int delta) => Interlocked.Add(ref _sendWindowSize, delta);

        /// <summary>
        /// Stream-level receive window. Tracks how many bytes the peer is allowed to send
        /// on this stream. Decremented when DATA arrives, replenished via WINDOW_UPDATE.
        /// Only accessed from the single read loop thread.
        /// </summary>
        public int RecvWindowSize { get; set; }

        public ValueTask<UHttpResponse> ResponseTask => _responseTask;
        public bool IsResponseCompleted => Volatile.Read(ref _responseCompleted) != 0;

        public CancellationTokenRegistration CancellationRegistration { get; set; }

        public Http2Stream(
            int streamId,
            UHttpRequest request,
            RequestContext context,
            int initialSendWindowSize,
            int initialRecvWindowSize,
            PoolableValueTaskSourcePool<UHttpResponse> responseSourcePool)
        {
            StreamId = streamId;
            Request = request;
            Context = context;
            State = Http2StreamState.Idle;
            StatusCode = 0;
            HeaderBlockBuffer = new MemoryStream();
            HeadersReceived = false;
            SendWindowSize = initialSendWindowSize;
            RecvWindowSize = initialRecvWindowSize;
            _responseSource = (responseSourcePool ?? throw new ArgumentNullException(nameof(responseSourcePool))).Rent();
            _responseTask = _responseSource.CreateValueTask();
        }

        public void AppendHeaderBlock(byte[] data, int offset, int length)
        {
            // Use MemoryStream.Write for efficient bulk copy instead of byte-by-byte
            HeaderBlockBuffer.Write(data, offset, length);
        }

        public ArraySegment<byte> GetHeaderBlockSegment()
        {
            if (HeaderBlockBuffer.TryGetBuffer(out var segment))
            {
                return new ArraySegment<byte>(
                    segment.Array,
                    segment.Offset,
                    (int)HeaderBlockBuffer.Length);
            }

            // Fallback for non-exposable buffers.
            var copy = HeaderBlockBuffer.ToArray();
            return new ArraySegment<byte>(copy, 0, copy.Length);
        }

        public void EnsureResponseBodyCapacity(int capacity)
        {
            if (capacity <= 0)
                return;

            EnsureBodyCapacity(capacity);
        }

        public void AppendResponseData(byte[] source, int offset, int length)
        {
            if (length <= 0)
                return;

            ThrowIfDisposed();
            EnsureBodyCapacity(_responseBodyLength + length);
            Buffer.BlockCopy(source, offset, _responseBodyBuffer, _responseBodyLength, length);
            _responseBodyLength += length;
        }

        public void ClearHeaderBlock()
        {
            HeaderBlockBuffer.SetLength(0);
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _responseCompleted, 1) != 0)
                return;

            State = Http2StreamState.Closed;

            var bodyBuffer = _responseBodyBuffer;
            var bodyLength = _responseBodyLength;
            var bodyFromPool = bodyBuffer != null && bodyLength > 0;

            // Transfer ownership of the pooled body to UHttpResponse.
            _responseBodyBuffer = null;
            _responseBodyLength = 0;

            var response = new UHttpResponse(
                statusCode: (HttpStatusCode)StatusCode,
                headers: ResponseHeaders ?? new HttpHeaders(),
                body: bodyFromPool
                    ? new ReadOnlyMemory<byte>(bodyBuffer, 0, bodyLength)
                    : ReadOnlyMemory<byte>.Empty,
                elapsedTime: Context.Elapsed,
                request: Request,
                error: null,
                bodyFromPool: bodyFromPool
            );

            _responseSource.SetResult(response);
        }

        public void Fail(Exception exception)
        {
            if (Interlocked.Exchange(ref _responseCompleted, 1) != 0)
                return;

            State = Http2StreamState.Closed;
            _responseSource.SetException(exception ?? new InvalidOperationException("HTTP/2 stream failed."));
        }

        public void Cancel(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _responseCompleted, 1) != 0)
                return;

            State = Http2StreamState.Closed;
            _responseSource.SetCanceled(cancellationToken);
        }

        public void CancelWithoutConsumption(CancellationToken cancellationToken = default)
        {
            Cancel(cancellationToken);
            _responseSource.ReturnWithoutConsumption();
        }

        public void ReturnResponseSourceIfUnconsumed()
        {
            _responseSource.ReturnWithoutConsumption();
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            CancellationRegistration.Dispose();
            if (_responseBodyBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_responseBodyBuffer);
                _responseBodyBuffer = null;
                _responseBodyLength = 0;
            }
            HeaderBlockBuffer?.Dispose();
        }

        private void EnsureBodyCapacity(int required)
        {
            ThrowIfDisposed();
            if (required <= 0)
                return;

            if (_responseBodyBuffer == null)
            {
                _responseBodyBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1024, required));
                return;
            }

            if (_responseBodyBuffer.Length >= required)
                return;

            var resized = ArrayPool<byte>.Shared.Rent(Math.Max(required, _responseBodyBuffer.Length * 2));
            try
            {
                if (_responseBodyLength > 0)
                    Buffer.BlockCopy(_responseBodyBuffer, 0, resized, 0, _responseBodyLength);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(resized);
                throw;
            }

            ArrayPool<byte>.Shared.Return(_responseBodyBuffer);
            _responseBodyBuffer = resized;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(Http2Stream));
        }
    }
}
