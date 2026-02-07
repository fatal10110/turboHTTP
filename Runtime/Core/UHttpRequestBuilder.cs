using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if TURBOHTTP_USE_SYSTEM_TEXT_JSON
using System.Text.Json;
#endif
using TurboHTTP.JSON;

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

        public UHttpRequestBuilder WithHeader(string name, string value)
        {
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

        /// <summary>
        /// Set body to pre-serialized JSON string. This is the recommended approach
        /// for IL2CPP builds — users can serialize with their own IL2CPP-safe
        /// serializer (Unity's JsonUtility, Newtonsoft with AOT, or source-generated
        /// System.Text.Json). Sets Content-Type to application/json.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="json"/> is null.</exception>
        public UHttpRequestBuilder WithJsonBody(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            return WithBody(Encoding.UTF8.GetBytes(json)).ContentType("application/json");
        }

        /// <summary>
        /// Serialize the object to JSON and set as body.
        /// Uses the registered JSON serializer (LiteJson by default).
        /// </summary>
        /// <remarks>
        /// <para><b>IL2CPP Note:</b> LiteJson is AOT-safe but only supports primitives,
        /// dictionaries, and lists. For complex types, either:</para>
        /// <list type="bullet">
        /// <item>Register a Newtonsoft serializer at startup</item>
        /// <item>Use <see cref="WithJsonBody(string)"/> with pre-serialized JSON</item>
        /// </list>
        /// </remarks>
        public UHttpRequestBuilder WithJsonBody<T>(T value)
        {
            var json = TurboHTTP.JSON.JsonSerializer.Serialize(value);
            return WithBody(Encoding.UTF8.GetBytes(json)).ContentType("application/json");
        }

        /// <summary>
        /// Serialize the object to JSON using a specific serializer.
        /// </summary>
        /// <param name="value">Object to serialize</param>
        /// <param name="serializer">Serializer to use (overrides default)</param>
        public UHttpRequestBuilder WithJsonBody<T>(T value, IJsonSerializer serializer)
        {
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));
            var json = serializer.Serialize(value);
            return WithBody(Encoding.UTF8.GetBytes(json)).ContentType("application/json");
        }

#if TURBOHTTP_USE_SYSTEM_TEXT_JSON
        /// <summary>
        /// Serialize using System.Text.Json with custom options.
        /// Only available when TURBOHTTP_USE_SYSTEM_TEXT_JSON is defined.
        /// </summary>
        public UHttpRequestBuilder WithJsonBody<T>(T value, System.Text.Json.JsonSerializerOptions options)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(value, options);
            return WithBody(Encoding.UTF8.GetBytes(json)).ContentType("application/json");
        }
#endif

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

        public UHttpRequestBuilder WithBearerToken(string token)
        {
            _headers.Set("Authorization", $"Bearer {token}");
            return this;
        }

        public UHttpRequestBuilder Accept(string mediaType)
        {
            _headers.Set("Accept", mediaType);
            return this;
        }

        public UHttpRequestBuilder ContentType(string mediaType)
        {
            _headers.Set("Content-Type", mediaType);
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
        public Task<UHttpResponse> SendAsync(CancellationToken cancellationToken = default)
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
