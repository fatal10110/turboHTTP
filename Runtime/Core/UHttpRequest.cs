using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
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

        private UHttpRequestBody _content;
        private bool _ownsContent;
        private int _isLeased;
        private int _disposeRequested;
        private int _responseHoldCount;
        private int _sendInProgress;
        private int _disposed;
        private int _hasManagedTrailerHeader;

        /// <summary> Gets the HTTP method for this request. </summary>
        public HttpMethod Method { get; private set; }
        /// <summary> Gets the target URI for this request. </summary>
        public Uri Uri { get; private set; }
        /// <summary> Gets the headers associated with this request. </summary>
        public HttpHeaders Headers => _headers;
        /// <summary> Gets the request body model. </summary>
        public UHttpRequestBody Content
        {
            get
            {
                ThrowIfDisposed();
                return _content;
            }
            private set => _content = value ?? new EmptyRequestBody();
        }

        /// <summary> Gets the timeout duration for this request. </summary>
        public TimeSpan Timeout { get; private set; }

        /// <summary>
        /// User-provided key-value metadata attached to this request.
        /// </summary>
        public IReadOnlyDictionary<string, object> Metadata => _metadata;

        /// <summary>
        /// Optional buffered-body preview for callers that can use the 22a content model.
        /// </summary>
        public bool TryGetBufferedContent(out ReadOnlyMemory<byte> data)
        {
            ThrowIfDisposed();
            if (_content == null)
            {
                data = ReadOnlyMemory<byte>.Empty;
                return true;
            }

            return _content.TryGetBufferedData(out data);
        }

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
            _content = new EmptyRequestBody();
            _ownsContent = true;
            Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UHttpRequest"/> class.
        /// </summary>
        /// <param name="method">The HTTP method.</param>
        /// <param name="uri">The target URI.</param>
        /// <param name="headers">Optional headers.</param>
        /// <param name="body">Optional body as a byte array.</param>
        /// <param name="timeout">Optional timeout duration.</param>
        /// <param name="metadata">Optional metadata dictionary.</param>
        public UHttpRequest(
            HttpMethod method,
            Uri uri,
            HttpHeaders headers = null,
            byte[] body = null,
            TimeSpan? timeout = null,
            IReadOnlyDictionary<string, object> metadata = null)
            : this(
                method,
                uri,
                headers,
                body != null ? (UHttpRequestBody)new BufferedRequestBody(body) : new EmptyRequestBody(),
                ownsContent: true,
                timeout,
                metadata)
        {
        }

        private UHttpRequest(
            HttpMethod method,
            Uri uri,
            HttpHeaders headers,
            UHttpRequestBody content,
            bool ownsContent,
            TimeSpan? timeout,
            IReadOnlyDictionary<string, object> metadata)
        {
            Method = method;
            Uri = uri ?? throw new ArgumentNullException(nameof(uri));
            _headers = headers?.Clone() ?? new HttpHeaders();
            _content = content ?? new EmptyRequestBody();
            _ownsContent = ownsContent;
            Timeout = timeout ?? TimeSpan.FromSeconds(30);
            _metadata = metadata != null
                ? new Dictionary<string, object>(metadata)
                : new Dictionary<string, object>();
            _hasManagedTrailerHeader = _content != null && _content.TrailerProvider != null ? 1 : 0;
            if (_hasManagedTrailerHeader != 0)
                SyncManagedTrailerHeader();
        }

        /// <summary>
        /// Creates a detached copy of this request.
        /// </summary>
        public UHttpRequest Clone()
        {
            ThrowIfDisposed();
            return new UHttpRequest(
                Method,
                Uri,
                _headers.Clone(),
                Content.CloneDetached(),
                ownsContent: true,
                Timeout,
                new Dictionary<string, object>(_metadata));
        }

        /// <summary>
        /// Adds or replaces a header on this request. Note: Mutates the request in-place.
        /// </summary>
        public UHttpRequest WithHeader(string name, string value)
        {
            ThrowIfDisposed();
            ValidateHeaderInput(name, nameof(name));
            ValidateHeaderInput(value, nameof(value));
            _headers.Set(name, value);
            if (Volatile.Read(ref _hasManagedTrailerHeader) != 0 &&
                string.Equals(name, "Trailer", StringComparison.OrdinalIgnoreCase))
            {
                SyncManagedTrailerHeader();
            }
            return this;
        }

        /// <summary>
        /// Replaces all headers on this request with the provided set. Note: Mutates the request in-place.
        /// </summary>
        public UHttpRequest WithHeaders(HttpHeaders newHeaders)
        {
            ThrowIfDisposed();
            if (ReferenceEquals(newHeaders, _headers))
                return this;

            _headers.Clear();
            CopyHeaders(newHeaders, _headers);
            SyncManagedTrailerHeader();
            return this;
        }

        /// <summary>
        /// Sets or replaces the request body using a byte array. Note: Mutates the request in-place.
        /// </summary>
        public UHttpRequest WithBody(byte[] body)
        {
            return WithBody(body != null
                ? new ReadOnlyMemory<byte>(body)
                : ReadOnlyMemory<byte>.Empty);
        }

        /// <summary>
        /// Sets or replaces the request body using raw bytes. Note: Mutates the request in-place.
        /// </summary>
        public UHttpRequest WithBody(ReadOnlyMemory<byte> body)
        {
            ThrowIfDisposed();
            ReplaceContent(
                body.IsEmpty
                    ? (UHttpRequestBody)new EmptyRequestBody()
                    : new BufferedRequestBody(body),
                ownsContent: true);
            return this;
        }

        /// <summary>
        /// Sets or replaces the request body using a UTF-8 string. Note: Mutates the request in-place.
        /// </summary>
        public UHttpRequest WithBody(string body)
        {
            ThrowIfDisposed();
            ReplaceContent(
                body != null
                    ? (UHttpRequestBody)new BufferedRequestBody(Encoding.UTF8.GetBytes(body))
                    : new EmptyRequestBody(),
                ownsContent: true);
            return this;
        }

        /// <summary>
        /// Sets or replaces the request body using a pooled memory owner. Note: Mutates the request in-place.
        /// </summary>
        public UHttpRequest WithLeasedBody(IMemoryOwner<byte> bodyOwner, int length)
        {
            ThrowIfDisposed();
            ReplaceContent(
                bodyOwner != null
                    ? (UHttpRequestBody)new OwnedMemoryRequestBody(bodyOwner, length)
                    : new EmptyRequestBody(),
                ownsContent: true);
            return this;
        }

        /// <summary>
        /// Sets or replaces the request body using a stream. Note: Mutates the request in-place.
        /// </summary>
        public UHttpRequest WithStreamBody(Stream stream, long? contentLength = null, bool leaveOpen = false)
        {
            ThrowIfDisposed();
            ReplaceContent(
                stream != null
                    ? (UHttpRequestBody)new StreamRequestBody(stream, contentLength, leaveOpen)
                    : new EmptyRequestBody(),
                ownsContent: true);
            return this;
        }

        /// <summary>
        /// Sets or replaces the request body using a replayable stream factory. Note: Mutates the request in-place.
        /// </summary>
        public UHttpRequest WithBodyFactory(
            Func<CancellationToken, ValueTask<Stream>> factory,
            long? contentLength = null)
        {
            ThrowIfDisposed();
            ReplaceContent(
                factory != null
                    ? (UHttpRequestBody)new FactoryRequestBody(factory, contentLength)
                    : new EmptyRequestBody(),
                ownsContent: true);
            return this;
        }

        /// <summary>
        /// Declares request trailers and attaches a provider that will produce them after the body reaches EOF.
        /// The declared names are mirrored into the Trailer request header for transport serialization.
        /// HTTP/1.1 sends request trailers only on chunked bodies; zero-length bodies with request trailers
        /// are emitted as chunked requests so the terminal trailer block can still be written.
        /// </summary>
        public UHttpRequest WithRequestTrailers(
            IReadOnlyList<string> declaredNames,
            Func<HttpHeaders> provider)
        {
            ThrowIfDisposed();
            if (declaredNames == null)
                throw new ArgumentNullException(nameof(declaredNames));
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            ValidateDeclaredTrailerNames(declaredNames);

            Content.SetRequestTrailers(declaredNames, provider);
            Interlocked.Exchange(ref _hasManagedTrailerHeader, 1);
            SyncManagedTrailerHeader();
            return this;
        }

        /// <summary>
        /// Sets or clears the Expect: 100-continue header on this request.
        /// The transport-specific wait semantics are applied only when the request
        /// ultimately carries a body.
        /// </summary>
        public UHttpRequest WithExpectContinue(bool enable = true)
        {
            ThrowIfDisposed();

            if (enable)
                _headers.Set("Expect", "100-continue");
            else
                _headers.Remove("Expect");

            return this;
        }

        /// <summary>
        /// Sets the timeout for this request. Note: Mutates the request in-place.
        /// </summary>
        public UHttpRequest WithTimeout(TimeSpan timeout)
        {
            ThrowIfDisposed();
            Timeout = timeout;
            _metadata[RequestMetadataKeys.ExplicitTimeout] = true;
            return this;
        }

        /// <summary>
        /// Adds or replaces a metadata key-value pair on this request. Note: Mutates the request in-place.
        /// </summary>
        public UHttpRequest WithMetadata(string key, object value)
        {
            ThrowIfDisposed();
            if (key == null)
                throw new ArgumentNullException(nameof(key));

            _metadata[key] = value;
            return this;
        }

        /// <summary>
        /// Replaces all metadata on this request with the provided dictionary. Note: Mutates the request in-place.
        /// </summary>
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
        public async ValueTask<UHttpResponse> SendBufferedAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_leaseOwner == null)
                throw new InvalidOperationException(
                    "This request is not associated with a client. Use client.SendBufferedAsync(request) instead.");

            try
            {
                return await _leaseOwner.SendBufferedAsync(this, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Dispose();
            }
        }

        public async ValueTask<UHttpStreamingResponse> SendStreamingAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_leaseOwner == null)
                throw new InvalidOperationException(
                    "This request is not associated with a client. Use client.SendStreamingAsync(request) instead.");

            try
            {
                return await _leaseOwner.SendStreamingAsync(this, cancellationToken).ConfigureAwait(false);
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

                DisposeContent();
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
            Interlocked.Exchange(ref _hasManagedTrailerHeader, 0);
            ReplaceContent(new EmptyRequestBody(), ownsContent: true);
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

        internal void SetTimeoutInternal(TimeSpan timeout)
        {
            ThrowIfDisposed();
            Timeout = timeout;
        }

        internal UHttpRequest WithLeasedBody(IMemoryOwner<byte> bodyOwner)
        {
            return WithLeasedBody(bodyOwner, bodyOwner?.Memory.Length ?? 0);
        }

        internal UHttpRequest WithContentInternal(UHttpRequestBody content)
        {
            ThrowIfDisposed();
            ReplaceContent(content, ownsContent: true);
            return this;
        }

        internal UHttpRequest CopyWithSharedContent()
        {
            ThrowIfDisposed();

            return new UHttpRequest(
                Method,
                Uri,
                _headers.Clone(),
                Content,
                ownsContent: false,
                Timeout,
                new Dictionary<string, object>(_metadata));
        }

        internal static UHttpRequest CreateDerived(
            HttpMethod method,
            Uri uri,
            HttpHeaders headers,
            UHttpRequestBody content,
            bool ownsContent,
            TimeSpan timeout,
            IReadOnlyDictionary<string, object> metadata)
        {
            return new UHttpRequest(
                method,
                uri,
                headers,
                content,
                ownsContent,
                timeout,
                metadata);
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
            Interlocked.Exchange(ref _hasManagedTrailerHeader, 0);
            ReplaceContent(new EmptyRequestBody(), ownsContent: true);
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

        private void ReplaceContent(UHttpRequestBody content, bool ownsContent)
        {
            var nextContent = content ?? new EmptyRequestBody();
            var previousContent = _content;
            var previousOwned = _ownsContent;

            _content = nextContent;
            _ownsContent = ownsContent;
            if (nextContent.TrailerProvider != null)
                Interlocked.Exchange(ref _hasManagedTrailerHeader, 1);

            SyncManagedTrailerHeader();

            if (previousOwned && previousContent != null && !ReferenceEquals(previousContent, nextContent))
                previousContent.Dispose();
        }

        private void SyncManagedTrailerHeader()
        {
            if (Volatile.Read(ref _hasManagedTrailerHeader) == 0)
                return;

            if (_content == null || _content.TrailerProvider == null)
            {
                _headers.Remove("Trailer");
                Interlocked.Exchange(ref _hasManagedTrailerHeader, 0);
                return;
            }

            var declaredNames = _content.DeclaredTrailerNames;
            if (declaredNames == null || declaredNames.Count == 0)
            {
                _headers.Remove("Trailer");
                return;
            }

            _headers.Set("Trailer", BuildDeclaredTrailerHeaderValue(declaredNames));
        }

        private static void ValidateDeclaredTrailerNames(IReadOnlyList<string> declaredNames)
        {
            // Core validates syntax and duplicate declarations only.
            // Transport-specific prohibited trailer fields are filtered later because Core must
            // remain independent from the transport assembly.
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < declaredNames.Count; i++)
            {
                var name = declaredNames[i];
                ValidateHeaderInput(name, nameof(declaredNames));
                ValidateTrailerFieldName(name);
                if (!seenNames.Add(name))
                {
                    throw new ArgumentException(
                        $"Duplicate trailer field name declared: {name}",
                        nameof(declaredNames));
                }
            }
        }

        private static string BuildDeclaredTrailerHeaderValue(IReadOnlyList<string> declaredNames)
        {
            if (declaredNames == null || declaredNames.Count == 0)
                return string.Empty;

            if (declaredNames.Count == 1)
                return declaredNames[0];

            var builder = new StringBuilder();
            for (int i = 0; i < declaredNames.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(declaredNames[i]);
            }

            return builder.ToString();
        }

        private static void ValidateTrailerFieldName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Trailer field name cannot be null or empty.", nameof(name));

            var span = name.AsSpan();
            for (int i = 0; i < span.Length; i++)
            {
                if (!IsRfc9110TChar(span[i]))
                    throw new ArgumentException(
                        $"Trailer field name contains invalid characters: {name}",
                        nameof(name));
            }
        }

        private static bool IsRfc9110TChar(char c)
        {
            if ((c >= '0' && c <= '9') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z'))
            {
                return true;
            }

            switch (c)
            {
                case '!':
                case '#':
                case '$':
                case '%':
                case '&':
                case '\'':
                case '*':
                case '+':
                case '-':
                case '.':
                case '^':
                case '_':
                case '`':
                case '|':
                case '~':
                    return true;
                default:
                    return false;
            }
        }

        private void DisposeContent()
        {
            if (!_ownsContent || _content == null)
                return;

            var content = _content;
            _content = null;
            _ownsContent = false;
            content.Dispose();
        }
    }
}
