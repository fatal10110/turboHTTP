using System;
using System.Text;
using TurboHTTP.Core;

namespace TurboHTTP.JSON
{
    /// <summary>
    /// Extension methods for adding JSON body support to <see cref="UHttpRequestBuilder"/>.
    /// </summary>
    public static class JsonRequestBuilderExtensions
    {
        /// <summary>
        /// Set body to pre-serialized JSON string. Sets Content-Type to application/json.
        /// This is the recommended approach for IL2CPP builds — users can serialize
        /// with their own IL2CPP-safe serializer (Unity's JsonUtility, Newtonsoft with AOT,
        /// or source-generated System.Text.Json).
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="json"/> is null.</exception>
        public static UHttpRequestBuilder WithJsonBody(this UHttpRequestBuilder builder, string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            return builder.WithBody(Encoding.UTF8.GetBytes(json)).WithHeader("Content-Type", ContentTypes.Json);
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
        /// <item>Use <see cref="WithJsonBody(UHttpRequestBuilder, string)"/> with pre-serialized JSON</item>
        /// </list>
        /// </remarks>
        public static UHttpRequestBuilder WithJsonBody<T>(this UHttpRequestBuilder builder, T value)
        {
            var json = JsonSerializer.Serialize(value);
            return builder.WithBody(Encoding.UTF8.GetBytes(json)).WithHeader("Content-Type", ContentTypes.Json);
        }

        /// <summary>
        /// Serialize the object to JSON using a specific serializer.
        /// </summary>
        /// <param name="builder">The request builder.</param>
        /// <param name="value">Object to serialize.</param>
        /// <param name="serializer">Serializer to use (overrides default).</param>
        public static UHttpRequestBuilder WithJsonBody<T>(this UHttpRequestBuilder builder, T value, IJsonSerializer serializer)
        {
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));
            var json = serializer.Serialize(value);
            return builder.WithBody(Encoding.UTF8.GetBytes(json)).WithHeader("Content-Type", ContentTypes.Json);
        }

#if TURBOHTTP_USE_SYSTEM_TEXT_JSON
        /// <summary>
        /// Serialize using System.Text.Json with custom options.
        /// Only available when TURBOHTTP_USE_SYSTEM_TEXT_JSON is defined.
        /// </summary>
        public static UHttpRequestBuilder WithJsonBody<T>(this UHttpRequestBuilder builder, T value, System.Text.Json.JsonSerializerOptions options)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(value, options);
            return builder.WithBody(Encoding.UTF8.GetBytes(json)).WithHeader("Content-Type", ContentTypes.Json);
        }
#endif
    }
}
