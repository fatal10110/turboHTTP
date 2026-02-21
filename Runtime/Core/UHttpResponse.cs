using System;
using System.Buffers;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Represents an HTTP response.
    /// </summary>
    public class UHttpResponse : IDisposable
    {
        public HttpStatusCode StatusCode { get; }
        public HttpHeaders Headers { get; }
        public ReadOnlyMemory<byte> Body
        {
            get
            {
                ThrowIfDisposed();
                return _body;
            }
        }
        public TimeSpan ElapsedTime { get; }

        /// <summary>
        /// The original request that generated this response.
        /// </summary>
        public UHttpRequest Request { get; }

        /// <summary>
        /// Error that occurred during the request, if any.
        /// Null if the request succeeded.
        /// </summary>
        public UHttpError Error { get; }

        private ReadOnlyMemory<byte> _body;
        private readonly byte[] _pooledBodyBuffer;
        private readonly bool _bodyBufferRented;
        private int _disposed;

        public UHttpResponse(
            HttpStatusCode statusCode,
            HttpHeaders headers,
            ReadOnlyMemory<byte> body,
            TimeSpan elapsedTime,
            UHttpRequest request,
            UHttpError error = null,
            bool bodyFromPool = false)
        {
            StatusCode = statusCode;
            Headers = headers ?? new HttpHeaders();
            _body = body;
            ElapsedTime = elapsedTime;
            Request = request ?? throw new ArgumentNullException(nameof(request));
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
        /// Get the response body as a UTF-8 string.
        /// Returns null if body is empty.
        /// </summary>
        public string GetBodyAsString()
        {
            if (Body.IsEmpty)
                return null;

            return Encoding.UTF8.GetString(Body.Span);
        }

        /// <summary>
        /// Get the response body as a string using the specified encoding.
        /// Returns null if body is empty.
        /// </summary>
        public string GetBodyAsString(Encoding encoding)
        {
            if (encoding == null) throw new ArgumentNullException(nameof(encoding));
            if (Body.IsEmpty)
                return null;

            return encoding.GetString(Body.Span);
        }

        /// <summary>
        /// Detect the character encoding from the Content-Type header's charset parameter.
        /// Falls back to UTF-8 if no charset is specified or the charset is not recognized.
        /// Uses manual string parsing to avoid Regex allocation (IL2CPP-safe).
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

            if (_bodyBufferRented && _pooledBodyBuffer != null)
                ArrayPool<byte>.Shared.Return(_pooledBodyBuffer);

            _body = ReadOnlyMemory<byte>.Empty;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(UHttpResponse));
        }
    }
}
