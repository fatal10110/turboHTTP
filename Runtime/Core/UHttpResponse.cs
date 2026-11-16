using System;
using System.Buffers;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Represents an HTTP response.
    /// </summary>
    public class UHttpResponse : IDisposable
    {
        /// <summary> Gets the HTTP status code of the response. </summary>
        public HttpStatusCode StatusCode { get; }
        /// <summary> Gets the headers returned in the response. </summary>
        public HttpHeaders Headers { get; }

        /// <summary>
        /// Trailer headers received after the response body.
        /// </summary>
        public HttpHeaders Trailers { get; }

        /// <summary>
        /// Response body sequence. This is the primary body representation.
        /// </summary>
        /// <remarks>
        /// The returned <see cref="ReadOnlySequence{T}"/> is valid only for the lifetime of
        /// this <see cref="UHttpResponse"/>. Disposing this response returns the underlying
        /// pooled buffers to <see cref="System.Buffers.ArrayPool{T}.Shared"/>. Any segment
        /// references held past <see cref="Dispose"/> point to memory that may be reused for
        /// unrelated purposes — this is use-after-pool-return and will cause data corruption.
        /// Always consume the body before or within a <c>using</c> block. A response that is never
        /// disposed does not return its buffers: its finalizer leaves them to the garbage collector,
        /// so a sequence still held after the response itself became unreachable stays intact.
        /// </remarks>
        public ReadOnlySequence<byte> Body
        {
            get
            {
                ThrowIfDisposed();
                return _body;
            }
        }

        /// <summary> Gets the elapsed time taken to complete the request and receive the response. </summary>
        public TimeSpan ElapsedTime { get; }

        /// <summary>
        /// The original request that generated this response.
        /// </summary>
        /// <remarks>
        /// Readable only while the response is alive: the getter throws <see cref="ObjectDisposedException"/> after
        /// the response is disposed. A request is never reused after its lease ends (see <see cref="UHttpRequest"/>),
        /// so a reference taken earlier is never handed to another caller. Once the lease has ended,
        /// <c>Method</c>, <c>Uri</c> and <c>Headers</c> can still be read, while <c>Content</c>, mutation and sending
        /// throw <see cref="ObjectDisposedException"/>; build a new request to resend.
        /// </remarks>
        public UHttpRequest Request
        {
            get
            {
                ThrowIfDisposed();
                return _request;
            }
        }

        private readonly UHttpRequest _request;

        /// <summary>
        /// Error that occurred during the request, if any.
        /// Null if the request succeeded.
        /// </summary>
        public UHttpError Error { get; }

        private ReadOnlySequence<byte> _body;
        private readonly byte[] _pooledBodyBuffer;
        private readonly bool _bodyBufferRented;
        private IDisposable _segmentedBodyOwner;
        private readonly object _disposeCallbackGate = new object();
        private Action _onDispose;
        private int _disposed;

        public UHttpResponse(
            HttpStatusCode statusCode,
            HttpHeaders headers,
            ReadOnlyMemory<byte> body,
            TimeSpan elapsedTime,
            UHttpRequest request,
            UHttpError error = null,
            bool bodyFromPool = false,
            HttpHeaders trailers = null)
        {
            StatusCode = statusCode;
            Headers = headers ?? new HttpHeaders();
            Trailers = trailers ?? HttpHeaders.Empty;
            _body = body.IsEmpty
                ? ReadOnlySequence<byte>.Empty
                : new ReadOnlySequence<byte>(body);
            ElapsedTime = elapsedTime;
            _request = request ?? throw new ArgumentNullException(nameof(request));
            Error = error;

            if (bodyFromPool && !body.IsEmpty)
            {
                if (!MemoryMarshal.TryGetArray(body, out var segment) || segment.Array == null)
                {
                    throw new ArgumentException(
                        "Body must be array-backed when bodyFromPool is true.",
                        nameof(body));
                }

                _pooledBodyBuffer = segment.Array;
                _bodyBufferRented = true;
            }
        }

        internal UHttpResponse(
            HttpStatusCode statusCode,
            HttpHeaders headers,
            ReadOnlySequence<byte> body,
            IDisposable segmentedBodyOwner,
            TimeSpan elapsedTime,
            UHttpRequest request,
            UHttpError error = null,
            HttpHeaders trailers = null)
        {
            StatusCode = statusCode;
            Headers = headers ?? new HttpHeaders();
            Trailers = trailers ?? HttpHeaders.Empty;
            _body = body;
            _segmentedBodyOwner = segmentedBodyOwner;
            ElapsedTime = elapsedTime;
            _request = request ?? throw new ArgumentNullException(nameof(request));
            Error = error;
        }

        /// <summary>
        /// Returns true if the status code is in the 2xx range.
        /// </summary>
        public bool IsSuccessStatusCode =>
            (int)StatusCode >= 200 && (int)StatusCode < 300;

        /// <summary>
        /// Returns true if an error occurred during the request.
        /// </summary>
        public bool IsError => Error != null;

        /// <summary>
        /// Get the response body as a UTF-8 string. Always decodes UTF-8 and ignores the charset of
        /// <c>Content-Type</c>; use <c>GetBodyAsString(GetContentEncoding())</c> for other charsets.
        /// Returns null if body is empty.
        /// </summary>
        public string GetBodyAsString()
        {
            var body = Body;
            if (body.IsEmpty)
                return null;

            string text;
            if (body.IsSingleSegment)
            {
                text = Encoding.UTF8.GetString(body.FirstSpan);
            }
            else
            {
                var bytes = body.ToArray();
                text = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
            }

            // Keeps this response, and so its pooled body buffers, alive until decoding is done (#582).
            GC.KeepAlive(this);
            return text;
        }

        /// <summary>
        /// Get the response body as a string using the specified encoding.
        /// Returns null if body is empty.
        /// </summary>
        public string GetBodyAsString(Encoding encoding)
        {
            if (encoding == null) throw new ArgumentNullException(nameof(encoding));

            var body = Body;
            if (body.IsEmpty)
                return null;

            string text;
            if (body.IsSingleSegment)
            {
                text = encoding.GetString(body.FirstSpan);
            }
            else
            {
                var bytes = body.ToArray();
                text = encoding.GetString(bytes, 0, bytes.Length);
            }

            // Keeps this response, and so its pooled body buffers, alive until decoding is done (#582).
            GC.KeepAlive(this);
            return text;
        }

        /// <summary>
        /// Detect the character encoding from the Content-Type header's charset parameter.
        /// Falls back to UTF-8 if no charset is specified or the charset is not recognized.
        /// </summary>
        public Encoding GetContentEncoding()
        {
            var contentType = Headers.Get("Content-Type");
            if (string.IsNullOrEmpty(contentType))
                return Encoding.UTF8;

            var charsetIndex = contentType.IndexOf("charset", StringComparison.OrdinalIgnoreCase);
            if (charsetIndex < 0)
                return Encoding.UTF8;

            var eqIndex = contentType.IndexOf('=', charsetIndex + 7);
            if (eqIndex < 0)
                return Encoding.UTF8;

            var start = eqIndex + 1;
            while (start < contentType.Length && contentType[start] == ' ')
                start++;
            if (start >= contentType.Length)
                return Encoding.UTF8;

            bool quoted = contentType[start] == '"';
            if (quoted) start++;

            var end = start;
            while (end < contentType.Length && contentType[end] != '"'
                   && contentType[end] != ';' && contentType[end] != ' ')
                end++;

            if (end == start)
                return Encoding.UTF8;

            var charset = contentType.Substring(start, end - start);
            try
            {
                return Encoding.GetEncoding(charset);
            }
            catch
            {
                return Encoding.UTF8;
            }
        }

        /// <summary>
        /// Throws an exception if the response is not successful or has an error.
        /// </summary>
        public void EnsureSuccessStatusCode()
        {
            if (IsError)
            {
                throw new UHttpException(Error);
            }

            if (!IsSuccessStatusCode)
            {
                var errorMsg = $"HTTP request failed with status {(int)StatusCode} {StatusCode}";
                var error = new UHttpError(
                    UHttpErrorType.HttpError,
                    errorMsg,
                    statusCode: StatusCode
                );
                throw new UHttpException(error);
            }
        }

        public override string ToString()
        {
            return $"{(int)StatusCode} {StatusCode} ({ElapsedTime.TotalMilliseconds:F0}ms)";
        }

        internal void AttachRequestRelease(Action releaseAction)
        {
            if (releaseAction == null)
                return;

            var releaseImmediately = false;
            lock (_disposeCallbackGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    releaseImmediately = true;
                }
                else
                {
                    _onDispose = (Action)Delegate.Combine(_onDispose, releaseAction);
                }
            }

            if (releaseImmediately)
                releaseAction();
        }

        ~UHttpResponse()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (!disposing)
            {
                // Finalizer path (#582): nothing is returned to the pool. Body is a ReadOnlySequence
                // that does not keep this response alive, so a caller may still read these buffers;
                // returning them would let another rent overwrite the bytes under it. Pooled arrays
                // are counted with ByteArrayPool.Abandon so the #292 leak counter stays exact, then
                // left to the GC. An owner that cannot abandon is dropped, never disposed here.
                if (_bodyBufferRented && _pooledBodyBuffer != null)
                    ByteArrayPool.Abandon(_pooledBodyBuffer);

                var abandoned = Interlocked.Exchange(ref _segmentedBodyOwner, null);
                (abandoned as IAbandonableBufferOwner)?.AbandonToGc();
                return;
            }

            // Explicit Dispose: return the pooled byte[] through the counting ByteArrayPool (#292).
            if (_bodyBufferRented && _pooledBodyBuffer != null)
                ByteArrayPool.Return(_pooledBodyBuffer);

            // Release the detached body owner (for example SegmentedBuffer or an
            // ArrayPool-backed response-body owner).
            var owner = Interlocked.Exchange(ref _segmentedBodyOwner, null);
            owner?.Dispose();
            _body = ReadOnlySequence<byte>.Empty;

            // IMPORTANT: Only invoke managed callbacks on the explicit-Dispose path.
            // The _onDispose callback triggers ReleaseResponseHold -> TryCompleteLease,
            // which disposes the request's owned body content (possibly returning pooled
            // buffers under a lock). Invoking this from the finalizer thread risks deadlock
            // (finalizer blocked on a lock held by a GC-waiting thread) and accessing
            // partially-finalized objects. If the user forgets Dispose(), the request's
            // lease never completes and its owned body content is not disposed eagerly.
            // (The finalizer path returned above.)
            Action callback;
            lock (_disposeCallbackGate)
            {
                callback = _onDispose;
                _onDispose = null;
            }

            callback?.Invoke();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(UHttpResponse));
        }
    }
}
