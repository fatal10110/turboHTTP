using System;
using System.Text;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.JSON
{
    /// <summary>
    /// Extension methods for adding JSON body support to <see cref="UHttpRequest"/>.
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
        public static UHttpRequest WithJsonBody(this UHttpRequest request, string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            // UTF-8 encode into a pooled buffer. Uses GetByteCount+GetBytes (two passes) because
            // Encoding.UTF8.GetBytes(ReadOnlySpan<char>, Span<byte>) requires .NET Core 2.1+
            // and is unavailable on .NET Standard 2.1 / Unity IL2CPP. A temporary byte[] is
            // allocated by GetBytes; the final body bytes land in the pooled writer.
            var writer = new PooledArrayBufferWriter();
            try
            {
                var byteCount = Encoding.UTF8.GetByteCount(json);
                var span = writer.GetSpan(byteCount);
                var bytes = Encoding.UTF8.GetBytes(json);
                bytes.CopyTo(span);
                writer.Advance(byteCount);
                var owner = writer.DetachAsOwner();
                return request.WithLeasedBody(owner).WithHeader("Content-Type", ContentTypes.Json);
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Serialize the object to JSON and set as body.
        /// Uses the registered JSON serializer (LiteJson by default).
        /// Writes into a pooled buffer; intermediate allocations depend on the serializer:
        /// LiteJson bridges via its string path (one string + one temp byte[] — .NET Standard 2.1
        /// IL2CPP constraint). System.Text.Json writes directly with zero intermediate allocation.
        /// </summary>
        /// <remarks>
        /// <para><b>IL2CPP Note:</b> LiteJson is AOT-safe but only supports primitives,
        /// dictionaries, and lists. For complex types, either:</para>
        /// <list type="bullet">
        /// <item>Register a Newtonsoft serializer at startup</item>
        /// <item>Use <see cref="WithJsonBody(UHttpRequest, string)"/> with pre-serialized JSON</item>
        /// </list>
        /// </remarks>
        public static UHttpRequest WithJsonBody<T>(this UHttpRequest request, T value)
        {
            var writer = new PooledArrayBufferWriter();
            try
            {
                JsonSerializer.Default.Serialize(value, writer);
                var owner = writer.DetachAsOwner();
                return request.WithLeasedBody(owner).WithHeader("Content-Type", ContentTypes.Json);
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Serialize the object to JSON using a specific serializer.
        /// Writes into a pooled buffer; intermediate allocations depend on the serializer
        /// (see <see cref="WithJsonBody{T}(UHttpRequest, T)"/> remarks).
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="value">Object to serialize.</param>
        /// <param name="serializer">Serializer to use (overrides default).</param>
        public static UHttpRequest WithJsonBody<T>(this UHttpRequest request, T value, IJsonSerializer serializer)
        {
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));
            var writer = new PooledArrayBufferWriter();
            try
            {
                serializer.Serialize(value, writer);
                var owner = writer.DetachAsOwner();
                return request.WithLeasedBody(owner).WithHeader("Content-Type", ContentTypes.Json);
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }

#if TURBOHTTP_USE_SYSTEM_TEXT_JSON
        /// <summary>
        /// Serialize using System.Text.Json with custom options.
        /// Only available when TURBOHTTP_USE_SYSTEM_TEXT_JSON is defined.
        /// </summary>
        public static UHttpRequest WithJsonBody<T>(this UHttpRequest request, T value, System.Text.Json.JsonSerializerOptions options)
        {
            var writer = new PooledArrayBufferWriter();
            try
            {
                // System.Text.Json natively supports IBufferWriter<byte> (Utf8JsonWriter).
                using var jsonWriter = new System.Text.Json.Utf8JsonWriter(writer);
                System.Text.Json.JsonSerializer.Serialize(jsonWriter, value, options);
                jsonWriter.Flush();
                var owner = writer.DetachAsOwner();
                return request.WithLeasedBody(owner).WithHeader("Content-Type", ContentTypes.Json);
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }
#endif
    }
}
