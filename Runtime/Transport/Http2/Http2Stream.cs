using System;
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
        public MemoryStream ResponseBody { get; }

        /// <summary>
    /// Buffer for accumulating HEADERS + CONTINUATION header blocks.
    /// Uses MemoryStream instead of List&lt;byte&gt; for efficient bulk appending.
    /// </summary>
    public MemoryStream HeaderBlockBuffer { get; }
        public bool HeadersReceived { get; set; }
        public bool PendingEndStream { get; set; }

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

        public TaskCompletionSource<UHttpResponse> ResponseTcs { get; }

        public CancellationTokenRegistration CancellationRegistration { get; set; }

        public Http2Stream(int streamId, UHttpRequest request, RequestContext context,
            int initialSendWindowSize, int initialRecvWindowSize)
        {
            StreamId = streamId;
            Request = request;
            Context = context;
            State = Http2StreamState.Idle;
            StatusCode = 0;
            ResponseBody = new MemoryStream();
            HeaderBlockBuffer = new MemoryStream();
            HeadersReceived = false;
            SendWindowSize = initialSendWindowSize;
            RecvWindowSize = initialRecvWindowSize;

            ResponseTcs = new TaskCompletionSource<UHttpResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (ResponseBody.Capacity < capacity)
                ResponseBody.Capacity = capacity;
        }

        public void ClearHeaderBlock()
        {
            HeaderBlockBuffer.SetLength(0);
        }

        public void Complete()
        {
            State = Http2StreamState.Closed;

            var response = new UHttpResponse(
                statusCode: (HttpStatusCode)StatusCode,
                headers: ResponseHeaders ?? new HttpHeaders(),
                body: ResponseBody.ToArray(),
                elapsedTime: Context.Elapsed,
                request: Request,
                error: null
            );

            ResponseTcs.TrySetResult(response);
        }

        public void Fail(Exception exception)
        {
            State = Http2StreamState.Closed;
            ResponseTcs.TrySetException(exception);
        }

        public void Cancel()
        {
            State = Http2StreamState.Closed;
            ResponseTcs.TrySetCanceled();
        }

        public void Dispose()
        {
            CancellationRegistration.Dispose();
            ResponseBody?.Dispose();
            HeaderBlockBuffer?.Dispose();
        }
    }
}
