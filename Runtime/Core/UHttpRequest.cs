using System;
using System.Collections.Generic;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Immutable representation of an HTTP request.
    /// Use UHttpRequestBuilder to construct instances.
    /// </summary>
    public class UHttpRequest
    {
        public HttpMethod Method { get; }
        public Uri Uri { get; }
        public HttpHeaders Headers { get; }
        public byte[] Body { get; }
        public TimeSpan Timeout { get; }

        /// <summary>
        /// User-provided key-value metadata attached to this request.
        /// Can be used by middleware to store/retrieve custom data.
        /// </summary>
        public IReadOnlyDictionary<string, object> Metadata { get; }

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
            Headers = headers?.Clone() ?? new HttpHeaders();
            Body = body;
            Timeout = timeout ?? TimeSpan.FromSeconds(30);
            Metadata = metadata ?? new Dictionary<string, object>();
        }

        /// <summary>
        /// Internal constructor that takes ownership of headers without cloning.
        /// Used by <see cref="UHttpRequestBuilder"/> which already builds a fresh headers instance.
        /// </summary>
        internal UHttpRequest(
            HttpMethod method,
            Uri uri,
            HttpHeaders headers,
            byte[] body,
            TimeSpan timeout,
            IReadOnlyDictionary<string, object> metadata,
            bool ownsHeaders)
        {
            Method = method;
            Uri = uri ?? throw new ArgumentNullException(nameof(uri));
            Headers = headers ?? new HttpHeaders();
            Body = body;
            Timeout = timeout;
            Metadata = metadata ?? new Dictionary<string, object>();
        }

        /// <summary>
        /// Create a copy of this request with modified properties.
        /// Useful for middleware that needs to transform requests.
        /// </summary>
        public UHttpRequest WithHeaders(HttpHeaders newHeaders)
        {
            return new UHttpRequest(Method, Uri, newHeaders, Body, Timeout, Metadata);
        }

        public UHttpRequest WithBody(byte[] newBody)
        {
            return new UHttpRequest(Method, Uri, Headers, newBody, Timeout, Metadata);
        }

        public UHttpRequest WithTimeout(TimeSpan newTimeout)
        {
            return new UHttpRequest(Method, Uri, Headers, Body, newTimeout, Metadata);
        }

        public UHttpRequest WithMetadata(IReadOnlyDictionary<string, object> newMetadata)
        {
            return new UHttpRequest(Method, Uri, Headers, Body, Timeout, newMetadata);
        }

        public override string ToString()
        {
            return $"{Method} {Uri}";
        }
    }
}
