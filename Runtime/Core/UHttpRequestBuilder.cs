using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Fluent builder for constructing and sending HTTP requests.
    /// Created by <see cref="UHttpClient"/> verb methods (Get, Post, etc.).
    /// </summary>
    public class UHttpRequestBuilder
    {
        private readonly UHttpClient _client;
        private readonly HttpMethod _method;
        private readonly string _url;
        private readonly HttpHeaders _headers = new HttpHeaders();
        private byte[] _body;
        private TimeSpan? _timeout;
        private Dictionary<string, object> _metadata;

        internal UHttpRequestBuilder(UHttpClient client, HttpMethod method, string url)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _url = url ?? throw new ArgumentNullException(nameof(url));
            _method = method;
        }

        /// <summary>
        /// Set a header on the request. Validates name and value for CRLF injection.
        /// </summary>
        /// <exception cref="ArgumentException">Name or value contains CR/LF characters.</exception>
        public UHttpRequestBuilder WithHeader(string name, string value)
        {
            ValidateHeaderInput(name, nameof(name));
            ValidateHeaderInput(value, nameof(value));
            _headers.Set(name, value);
            return this;
        }

        /// <summary>
        /// Copy all headers from the given collection, preserving multi-value headers.
        /// Iterates Names + GetValues() to copy ALL values, not just first.
        /// </summary>
        public UHttpRequestBuilder WithHeaders(HttpHeaders headers)
        {
            if (headers == null) return this;

            foreach (var name in headers.Names)
            {
                foreach (var value in headers.GetValues(name))
                {
                    _headers.Add(name, value);
                }
            }
            return this;
        }

        public UHttpRequestBuilder WithBody(byte[] body)
        {
            _body = body;
            return this;
        }

        public UHttpRequestBuilder WithBody(string body)
        {
            _body = body != null ? Encoding.UTF8.GetBytes(body) : null;
            return this;
        }

        public UHttpRequestBuilder WithTimeout(TimeSpan timeout)
        {
            _timeout = timeout;
            return this;
        }

        public UHttpRequestBuilder WithMetadata(string key, object value)
        {
            if (_metadata == null)
                _metadata = new Dictionary<string, object>();
            _metadata[key] = value;
            return this;
        }

        /// <summary>
        /// Build the <see cref="UHttpRequest"/> by resolving relative URLs against BaseUrl,
        /// merging default + request headers, and applying the timeout.
        /// </summary>
        public UHttpRequest Build()
        {
            var uri = ResolveUri(_url);
            var mergedHeaders = MergeHeaders();
            var timeout = _timeout ?? _client.ClientOptions.DefaultTimeout;

            var metadata = _metadata != null
                ? new Dictionary<string, object>(_metadata)
                : null;

            if (metadata == null)
                metadata = new Dictionary<string, object>();

            if (!metadata.ContainsKey(RequestMetadataKeys.FollowRedirects))
                metadata[RequestMetadataKeys.FollowRedirects] = _client.ClientOptions.FollowRedirects;

            if (!metadata.ContainsKey(RequestMetadataKeys.MaxRedirects))
                metadata[RequestMetadataKeys.MaxRedirects] = _client.ClientOptions.MaxRedirects;

            if (_timeout.HasValue)
                metadata[RequestMetadataKeys.ExplicitTimeout] = true;

            if (!metadata.ContainsKey(RequestMetadataKeys.ProxyDisabled) &&
                !metadata.ContainsKey(RequestMetadataKeys.ProxySettings))
            {
                var resolvedProxy = ProxyEnvironmentResolver.Resolve(
                    uri,
                    _client.ClientOptions.Proxy);
                if (resolvedProxy != null)
                {
                    metadata[RequestMetadataKeys.ProxySettings] = resolvedProxy;
                }
            }

            return new UHttpRequest(
                _method,
                uri,
                mergedHeaders,
                _body,
                timeout,
                metadata,
                ownsHeaders: true);
        }

        /// <summary>
        /// Build and send the request via the owning <see cref="UHttpClient"/>.
        /// </summary>
        /// <remarks>
        /// The returned ValueTask must be awaited exactly once and must not be stored for later consumption.
        /// Convert to Task via <see cref="ValueTask{TResult}.AsTask"/> only when Task combinators are required.
        /// </remarks>
        public ValueTask<UHttpResponse> SendAsync(CancellationToken cancellationToken = default)
        {
            var request = Build();
            return _client.SendAsync(request, cancellationToken);
        }

        private Uri ResolveUri(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri))
                return absoluteUri;

            var baseUrl = _client.ClientOptions.BaseUrl;
            if (string.IsNullOrEmpty(baseUrl))
            {
                throw new InvalidOperationException(
                    $"Cannot resolve relative URL '{url}' without a BaseUrl configured in UHttpClientOptions.");
            }

            var baseUri = new Uri(baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
            return new Uri(baseUri, url);
        }

        /// <summary>
        /// Validate that a header name or value does not contain CR or LF characters.
        /// This is a defense-in-depth measure — the serializer also validates, but catching
        /// injection early provides clearer error messages and consistent behavior across
        /// all code paths (builder, middleware, extensions).
        /// </summary>
        private static void ValidateHeaderInput(string value, string paramName)
        {
            if (value != null && (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0))
                throw new ArgumentException(
                    $"Header {paramName} must not contain CR or LF characters.", paramName);
        }

        /// <summary>
        /// Merge default headers with request-specific headers.
        /// Request headers take precedence over defaults.
        /// </summary>
        private HttpHeaders MergeHeaders()
        {
            var defaultHeaders = _client.ClientOptions.DefaultHeaders;
            if (defaultHeaders == null || defaultHeaders.Count == 0)
                return _headers.Clone();

            var merged = new HttpHeaders();

            // Apply defaults first
            foreach (var name in defaultHeaders.Names)
            {
                foreach (var value in defaultHeaders.GetValues(name))
                {
                    merged.Add(name, value);
                }
            }

            // Request headers override defaults
            foreach (var name in _headers.Names)
            {
                var values = _headers.GetValues(name);
                merged.Set(name, values[0]);
                for (int i = 1; i < values.Count; i++)
                {
                    merged.Add(name, values[i]);
                }
            }

            return merged;
        }
    }
}
