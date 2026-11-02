using System;
using TurboHTTP.Core;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Sends and receives <typeparamref name="T"/> as JSON text messages through the project's
    /// registered JSON serializer (TurboHTTP.JSON's <c>JsonSerializer.Default</c>, reached through
    /// the JSON bridge that TurboHTTP's startup bootstrap registers).
    /// </summary>
    /// <typeparam name="T">Message type.</typeparam>
    /// <remarks>
    /// <para>
    /// There is no reflection fallback: the shape of <typeparamref name="T"/> is whatever the
    /// registered serializer supports. With the default <c>JsonUtilitySerializer</c> that is a
    /// <c>[Serializable]</c> class with public fields (nested <c>[Serializable]</c> types, arrays and
    /// <c>List&lt;T&gt;</c> fields; no properties or dictionary fields) or
    /// <c>Dictionary&lt;string, object&gt;</c>. Types the serializer cannot map, such as DTOs with
    /// auto-properties under <c>JsonUtility</c>, fail with
    /// <see cref="WebSocketError.SerializationFailed"/>; register a full serializer with
    /// <c>JsonSerializer.SetDefault</c> for them.
    /// </para>
    /// <para>
    /// Failures of either direction are thrown as <see cref="WebSocketException"/> with
    /// <see cref="WebSocketError.SerializationFailed"/>, the serializer's exception as the inner
    /// exception. A JSON <c>null</c> payload deserializes to <c>null</c>. The instance is stateless
    /// and may be shared between connections and threads.
    /// </para>
    /// </remarks>
    public sealed class JsonWebSocketSerializer<T> : IWebSocketMessageSerializer<T>
        where T : class
    {
        /// <summary>
        /// JSON is sent as text messages.
        /// </summary>
        public WebSocketOpcode MessageType => WebSocketOpcode.Text;

        /// <summary>
        /// Serializes <paramref name="message"/> to UTF-8 JSON with the registered serializer.
        /// </summary>
        /// <exception cref="WebSocketException">
        /// <see cref="WebSocketError.SerializationFailed"/> when the serializer fails or no JSON
        /// serializer is registered.
        /// </exception>
        public ReadOnlyMemory<byte> Serialize(T message)
        {
            try
            {
                string json = ProjectJsonBridge.Serialize(
                    message,
                    typeof(T),
                    requiredBy: "WebSocket JSON serialization");

                return WebSocketConstants.StrictUtf8.GetBytes(json ?? string.Empty);
            }
            catch (Exception ex)
            {
                throw new WebSocketException(
                    WebSocketError.SerializationFailed,
                    "Failed to serialize WebSocket payload to JSON.",
                    ex);
            }
        }

        /// <summary>
        /// Deserializes a received message to <typeparamref name="T"/> with the registered serializer.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="raw"/> is null.</exception>
        /// <exception cref="WebSocketException">
        /// <see cref="WebSocketError.SerializationFailed"/> when the payload is not valid UTF-8 or
        /// JSON, the serializer cannot map it to <typeparamref name="T"/>, or no JSON serializer is
        /// registered.
        /// </exception>
        public T Deserialize(WebSocketMessage raw)
        {
            if (raw == null)
                throw new ArgumentNullException(nameof(raw));

            try
            {
                object result = ProjectJsonBridge.Deserialize(
                    ExtractJson(raw),
                    typeof(T),
                    requiredBy: "WebSocket JSON deserialization");

                if (result == null)
                    return null;
                if (result is T typed)
                    return typed;

                throw new InvalidOperationException(
                    "The registered JSON serializer returned '" + result.GetType().Name
                    + "' for '" + typeof(T).Name + "'.");
            }
            catch (Exception ex)
            {
                throw new WebSocketException(
                    WebSocketError.SerializationFailed,
                    "Failed to deserialize WebSocket JSON payload.",
                    ex);
            }
        }

        private static string ExtractJson(WebSocketMessage raw)
        {
            if (raw.IsText && raw.Text != null)
                return raw.Text;

            if (raw.Length == 0)
                return string.Empty;

            return WebSocketConstants.StrictUtf8.GetString(raw.Data.Span);
        }
    }
}
