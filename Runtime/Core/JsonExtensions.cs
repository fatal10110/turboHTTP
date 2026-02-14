using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.JSON;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Extension methods for JSON serialization/deserialization on responses and client.
    /// Uses the registered <see cref="TurboHTTP.JSON.JsonSerializer"/> (LiteJson by default).
    /// </summary>
    public static class JsonExtensions
    {
        /// <summary>
        /// Deserialize the response body as JSON.
        /// Returns default(T) if body is null or empty.
        /// </summary>
        /// <remarks>
        /// Uses UTF-8 encoding unconditionally per RFC 8259 Section 8.1:
        /// "JSON text exchanged between systems MUST be encoded using UTF-8."
        /// </remarks>
        /// <exception cref="JsonSerializationException">Deserialization failed</exception>
        public static T AsJson<T>(this UHttpResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (response.Body == null || response.Body.Length == 0)
                return default;

            var json = Encoding.UTF8.GetString(response.Body);
            return TurboHTTP.JSON.JsonSerializer.Deserialize<T>(json);
        }

        /// <summary>
        /// Deserialize the response body as JSON using a specific serializer.
        /// Returns default(T) if body is null or empty.
        /// </summary>
        public static T AsJson<T>(this UHttpResponse response, IJsonSerializer serializer)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));
            if (response.Body == null || response.Body.Length == 0)
                return default;

            var json = Encoding.UTF8.GetString(response.Body);
            return serializer.Deserialize<T>(json);
        }

        /// <summary>
        /// Try to deserialize the response body as JSON.
        /// Returns false if deserialization fails or body is empty.
        /// </summary>
        public static bool TryAsJson<T>(this UHttpResponse response, out T result)
        {
            try
            {
                result = response.AsJson<T>();
                return response.Body != null && response.Body.Length > 0;
            }
            catch (JsonSerializationException)
            {
                result = default;
                return false;
            }
            catch (ArgumentNullException)
            {
                result = default;
                return false;
            }
        }

        /// <summary>
        /// Send a GET request and deserialize the JSON response.
        /// Throws on non-2xx status.
        /// </summary>
        public static async Task<T> GetJsonAsync<T>(
            this UHttpClient client,
            string url,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            var response = await client
                .Get(url)
                .Accept(ContentTypes.Json)
                .SendAsync(cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            return response.AsJson<T>();
        }

        /// <summary>
        /// Send a POST request with JSON body and deserialize the response.
        /// Throws on non-2xx status.
        /// </summary>
        public static async Task<TResponse> PostJsonAsync<TRequest, TResponse>(
            this UHttpClient client,
            string url,
            TRequest data,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            var response = await client
                .Post(url)
                .WithJsonBody(data)
                .Accept(ContentTypes.Json)
                .SendAsync(cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            return response.AsJson<TResponse>();
        }

        /// <summary>
        /// Send a PUT request with JSON body and deserialize the response.
        /// Throws on non-2xx status.
        /// </summary>
        public static async Task<TResponse> PutJsonAsync<TRequest, TResponse>(
            this UHttpClient client,
            string url,
            TRequest data,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            var response = await client
                .Put(url)
                .WithJsonBody(data)
                .Accept(ContentTypes.Json)
                .SendAsync(cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            return response.AsJson<TResponse>();
        }

        /// <summary>
        /// Send a PATCH request with JSON body and deserialize the response.
        /// Throws on non-2xx status.
        /// </summary>
        public static async Task<TResponse> PatchJsonAsync<TRequest, TResponse>(
            this UHttpClient client,
            string url,
            TRequest data,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            var response = await client
                .Patch(url)
                .WithJsonBody(data)
                .Accept(ContentTypes.Json)
                .SendAsync(cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            return response.AsJson<TResponse>();
        }

        /// <summary>
        /// Send a DELETE request and deserialize the JSON response.
        /// Throws on non-2xx status.
        /// </summary>
        public static async Task<T> DeleteJsonAsync<T>(
            this UHttpClient client,
            string url,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            var response = await client
                .Delete(url)
                .Accept(ContentTypes.Json)
                .SendAsync(cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            return response.AsJson<T>();
        }
    }
}
