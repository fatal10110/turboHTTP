using System;
using System.Collections;
using System.Collections.Generic;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Case-insensitive HTTP header collection with multi-value support.
    /// Per RFC 9110 Section 5.3, multiple header field lines with the same name
    /// can be combined with comma separation, except for Set-Cookie (RFC 6265).
    /// </summary>
    public class HttpHeaders : IEnumerable<KeyValuePair<string, string>>
    {
        private readonly Dictionary<string, List<string>> _headers;

        public HttpHeaders()
        {
            _headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Set a header, replacing all existing values for that name.
        /// </summary>
        public void Set(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Header name cannot be null or empty", nameof(name));

            _headers[name] = new List<string> { value ?? string.Empty };
        }

        /// <summary>
        /// Add a value to a header. If the header already exists, the value is appended.
        /// Use this for headers that support multiple values (e.g., Set-Cookie, Via).
        /// </summary>
        public void Add(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Header name cannot be null or empty", nameof(name));

            if (_headers.TryGetValue(name, out var values))
            {
                values.Add(value ?? string.Empty);
            }
            else
            {
                _headers[name] = new List<string> { value ?? string.Empty };
            }
        }

        /// <summary>
        /// Get the first header value, or null if not present.
        /// For multi-value headers, use GetValues() instead.
        /// </summary>
        public string Get(string name)
        {
            if (_headers.TryGetValue(name, out var values) && values.Count > 0)
                return values[0];
            return null;
        }

        /// <summary>
        /// Get all values for a header name.
        /// Returns empty list if the header is not present.
        /// </summary>
        public IReadOnlyList<string> GetValues(string name)
        {
            if (_headers.TryGetValue(name, out var values))
                return values;
            return Array.Empty<string>();
        }

        /// <summary>
        /// Check if a header exists.
        /// </summary>
        public bool Contains(string name)
        {
            return _headers.ContainsKey(name);
        }

        /// <summary>
        /// Remove a header (all values).
        /// </summary>
        public bool Remove(string name)
        {
            return _headers.Remove(name);
        }

        /// <summary>
        /// Get all header names.
        /// </summary>
        public IEnumerable<string> Names => _headers.Keys;

        /// <summary>
        /// Get the number of distinct header names.
        /// </summary>
        public int Count => _headers.Count;

        /// <summary>
        /// Create a deep copy of this header collection.
        /// </summary>
        public HttpHeaders Clone()
        {
            var clone = new HttpHeaders();
            foreach (var kvp in _headers)
            {
                clone._headers[kvp.Key] = new List<string>(kvp.Value);
            }
            return clone;
        }

        /// <summary>
        /// Enumerates headers as key-value pairs.
        /// Multi-value headers yield one item per value in insertion order.
        /// </summary>
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            foreach (var kvp in _headers)
            {
                for (int i = 0; i < kvp.Value.Count; i++)
                    yield return new KeyValuePair<string, string>(kvp.Key, kvp.Value[i]);
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// Indexer for convenient access: headers["Content-Type"]
        /// Gets the first value; sets replaces all values.
        /// </summary>
        public string this[string name]
        {
            get => Get(name);
            set => Set(name, value);
        }
    }
}
