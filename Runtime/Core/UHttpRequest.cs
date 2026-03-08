using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Mutable HTTP request.
    /// Instances created via <see cref="UHttpClient.CreateRequest"/> are leased from a pool
    /// and must be disposed to return them.
    /// </summary>
    public sealed class UHttpRequest : IDisposable
    {
        private readonly UHttpClient _leaseOwner;
        private readonly HttpHeaders _headers;
        private readonly Dictionary<string, object> _metadata;

        private IMemoryOwner<byte> _bodyOwner;
        private int _isLeased;
        private int _disposeRequested;
        private int _responseHoldCount;
        private int _sendInProgress;
        private int _disposed;

        public HttpMethod Method { get; private set; }
        public Uri Uri { get; private set; }
        public HttpHeaders Headers => _headers;
        public ReadOnlyMemory<byte> Body { get; private set; }
        public TimeSpan Timeout { get; private set; }

        /// <summary>
        /// User-provided key-value metadata attached to this request.
        /// </summary>
        public IReadOnlyDictionary<string, object> Metadata => _metadata;

        /// <summary>
        /// Optional pool-owned body. Internal infrastructure only.
        /// </summary>
        internal IMemoryOwner<byte> BodyOwner => _bodyOwner;

        internal bool IsPooled => _leaseOwner != null;

        internal bool IsOwnedBy(UHttpClient client)
        {
            return ReferenceEquals(_leaseOwner, client);
        }

        internal UHttpRequest(UHttpClient leaseOwner)
        {
            _leaseOwner = leaseOwner ?? throw new ArgumentNullException(nameof(leaseOwner));
            _headers = new HttpHeaders();
            _metadata = new Dictionary<string, object>();
            Timeout = TimeSpan.FromSeconds(30);
        }

        public UHttpRequest(
            HttpMethod method,
            Uri uri,
            HttpHeaders headers = null,
            byte[] body = null,
            TimeSpan? timeout = null,
            IReadOnlyDictionary<string, object> metadata = null)
        {
            Method = method;
            Uri = uri ?? throw new ArgumentNullException(nameof(uri));
            _headers = headers?.Clone() ?? new HttpHeaders();
            Body = body != null
                ? new ReadOnlyMemory<byte>(body)
                : ReadOnlyMemory<byte>.Empty;
            Timeout = timeout ?? TimeSpan.FromSeconds(30);
            _metadata = metadata != null
                ? new Dictionary<string, object>(metadata)
                : new Dictionary<string, object>();
        }

        /// <summary>
        /// Creates a detached copy of this request.
        /// </summary>
        public UHttpRequest Clone()
        {
            ThrowIfDisposed();

            byte[] bodyCopy = null;
            if (!Body.IsEmpty)
                bodyCopy = Body.ToArray();

            return new UHttpRequest(
                Method,
                Uri,
                _headers.Clone(),
                bodyCopy,
                Timeout,
                new Dictionary<string, object>(_metadata));
        }

        public UHttpRequest WithHeader(string name, string value)
        {
            ThrowIfDisposed();
            ValidateHeaderInput(name, nameof(name));
            ValidateHeaderInput(value, nameof(value));
            _headers.Set(name, value);
            return this;
        }

        public UHttpRequest WithHeaders(HttpHeaders newHeaders)
        {
            ThrowIfDisposed();
            if (ReferenceEquals(newHeaders, _headers))
                return this;

            _headers.Clear();
            CopyHeaders(newHeaders, _headers);
            return this;
        }

        public UHttpRequest WithBody(byte[] body)
        {
            ThrowIfDisposed();
            DisposeBodyOwner();
            Body = body != null
                ? new ReadOnlyMemory<byte>(body)
                : ReadOnlyMemory<byte>.Empty;
            return this;
        }

        public UHttpRequest WithBody(string body)
        {
            ThrowIfDisposed();
            DisposeBodyOwner();
            Body = body != null
                ? new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(body))
                : ReadOnlyMemory<byte>.Empty;
            return this;
        }

        public UHttpRequest WithTimeout(TimeSpan timeout)
        {
            ThrowIfDisposed();
            Timeout = timeout;
            _metadata[RequestMetadataKeys.ExplicitTimeout] = true;
            return this;
        }

        public UHttpRequest WithMetadata(string key, object value)
        {
            ThrowIfDisposed();
            if (key == null)
                throw new ArgumentNullException(nameof(key));

            _metadata[key] = value;
            return this;
        }

        public UHttpRequest WithMetadata(IReadOnlyDictionary<string, object> metadata)
        {
            ThrowIfDisposed();
            if (ReferenceEquals(metadata, _metadata))
                return this;

            _metadata.Clear();
            if (metadata == null)
                return this;

            foreach (var pair in metadata)
                _metadata[pair.Key] = pair.Value;
            return this;
        }

        /// <summary>
        /// Sends this request through the owning client.
        /// Only available for requests created by <see cref="UHttpClient"/>.
        /// </summary>
        public async ValueTask<UHttpResponse> SendAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_leaseOwner == null)
                throw new InvalidOperationException(
                    "This request is not associated with a client. Use client.SendAsync(request) instead.");

            try
            {
                return await _leaseOwner.SendAsync(this, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Dispose();
            }
        }

        public override string ToString()
        {
            return $"{Method} {Uri}";
        }

        public void Dispose()
        {
            if (_leaseOwner == null)
            {
                if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                    return;

                DisposeBodyOwner();
                return;
            }

            if (Interlocked.CompareExchange(ref _disposeRequested, 1, 0) != 0)
                return;

            TryReturnToPool();
        }

        internal void ActivateLease(HttpMethod method, Uri uri, TimeSpan timeout)
        {
            if (uri == null)
                throw new ArgumentNullException(nameof(uri));
            if (_leaseOwner == null)
                throw new InvalidOperationException("Only pooled requests can be activated.");
            if (Interlocked.CompareExchange(ref _isLeased, 1, 0) != 0)
                throw new InvalidOperationException("Request is already leased.");

            Interlocked.Exchange(ref _disposeRequested, 0);
            Interlocked.Exchange(ref _responseHoldCount, 0);
            Interlocked.Exchange(ref _sendInProgress, 0);

            Method = method;
            Uri = uri;
            Timeout = timeout;
            _headers.Clear();
            _metadata.Clear();
            Body = ReadOnlyMemory<byte>.Empty;
            DisposeBodyOwner();
        }

        internal void ApplyDefaultHeaders(HttpHeaders defaultHeaders)
        {
            CopyHeaders(defaultHeaders, _headers);
        }

        internal void BeginSend()
        {
            ThrowIfDisposed();
            if (_leaseOwner == null)
                return;

            if (Volatile.Read(ref _responseHoldCount) != 0)
            {
                throw new InvalidOperationException(
                    "Cannot send a pooled request while a previous response still references it.");
            }

            if (Interlocked.CompareExchange(ref _sendInProgress, 1, 0) != 0)
            {
                throw new InvalidOperationException("Request is already being sent.");
            }
        }

        internal void EndSend()
        {
            if (_leaseOwner == null)
                return;

            Interlocked.Exchange(ref _sendInProgress, 0);
            TryReturnToPool();
        }

        internal void RetainForResponse()
        {
            if (_leaseOwner == null)
                return;

            Interlocked.Increment(ref _responseHoldCount);
        }

        internal void ReleaseResponseHold()
        {
            if (_leaseOwner == null)
                return;

            var remaining = Interlocked.Decrement(ref _responseHoldCount);
            if (remaining < 0)
            {
                Interlocked.Exchange(ref _responseHoldCount, 0);
                return;
            }

            TryReturnToPool();
        }

        internal void DisposeBodyOwner()
        {
            // Interlocked.Exchange for atomicity: prevents a double-dispose race if this
            // method is called concurrently (e.g., Http2Connection outer finally and
            // ResetForPool both calling DisposeBodyOwner on the same pooled request).
            var owner = Interlocked.Exchange(ref _bodyOwner, null);
            owner?.Dispose();
        }

        internal void SetTimeoutInternal(TimeSpan timeout)
        {
            ThrowIfDisposed();
            Timeout = timeout;
        }

        internal UHttpRequest WithLeasedBody(IMemoryOwner<byte> bodyOwner)
        {
            ThrowIfDisposed();
            DisposeBodyOwner();
            _bodyOwner = bodyOwner;
            Body = bodyOwner?.Memory ?? ReadOnlyMemory<byte>.Empty;
            return this;
        }

        private void TryReturnToPool()
        {
            if (_leaseOwner == null)
                return;
            if (Volatile.Read(ref _disposeRequested) == 0)
                return;
            if (Volatile.Read(ref _sendInProgress) != 0)
                return;
            if (Volatile.Read(ref _responseHoldCount) != 0)
                return;
            if (Interlocked.CompareExchange(ref _isLeased, 0, 1) != 1)
                return;

            ResetForPool();
            _leaseOwner.ReturnRequestToPool(this);
        }

        private void ResetForPool()
        {
            Method = HttpMethod.GET;
            Uri = null;
            Timeout = TimeSpan.FromSeconds(30);
            _headers.Clear();
            _metadata.Clear();
            Body = ReadOnlyMemory<byte>.Empty;
            DisposeBodyOwner();
            Interlocked.Exchange(ref _disposeRequested, 0);
            Interlocked.Exchange(ref _responseHoldCount, 0);
            Interlocked.Exchange(ref _sendInProgress, 0);
            Interlocked.Exchange(ref _disposed, 0);
        }

        private void ThrowIfDisposed()
        {
            if (_leaseOwner == null)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(UHttpRequest));
                return;
            }

            if (Volatile.Read(ref _isLeased) == 0 || Volatile.Read(ref _disposeRequested) != 0)
                throw new ObjectDisposedException(nameof(UHttpRequest));
        }

        private static void ValidateHeaderInput(string value, string paramName)
        {
            if (value != null && (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0))
                throw new ArgumentException(
                    $"Header {paramName} must not contain CR or LF characters.",
                    paramName);
        }

        private static void CopyHeaders(HttpHeaders source, HttpHeaders destination)
        {
            if (source == null || destination == null)
                return;

            foreach (var name in source.Names)
            {
                var values = source.GetValues(name);
                if (values == null || values.Count == 0)
                    continue;

                destination.Set(name, values[0]);
                for (int i = 1; i < values.Count; i++)
                    destination.Add(name, values[i]);
            }
        }
    }
}
