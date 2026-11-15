using System;

namespace TurboHTTP.WebSocket
{
    /// <summary>Converts typed messages to and from WebSocket payloads for the typed helpers in <see cref="WebSocketClientExtensions"/>.</summary>
    public interface IWebSocketMessageSerializer<T>
    {
        /// <summary>Message type the serializer produces and expects: <see cref="WebSocketOpcode.Text"/> or <see cref="WebSocketOpcode.Binary"/>.</summary>
        WebSocketOpcode MessageType { get; }

        /// <summary>Serializes <paramref name="message"/>; a text serializer must produce valid UTF-8.</summary>
        ReadOnlyMemory<byte> Serialize(T message);

        /// <summary>Deserializes a received message. The caller disposes <paramref name="raw"/> afterwards, so keep no reference to its data.</summary>
        T Deserialize(WebSocketMessage raw);
    }

    /// <summary>Pass-through serializer for UTF-8 text messages.</summary>
    public sealed class RawStringSerializer : IWebSocketMessageSerializer<string>
    {
        /// <summary>Shared instance.</summary>
        public static readonly RawStringSerializer Instance = new RawStringSerializer();

        /// <inheritdoc />
        public WebSocketOpcode MessageType => WebSocketOpcode.Text;

        /// <inheritdoc />
        public ReadOnlyMemory<byte> Serialize(string message)
        {
            message = message ?? string.Empty;
            return WebSocketConstants.StrictUtf8.GetBytes(message);
        }

        /// <inheritdoc />
        public string Deserialize(WebSocketMessage raw)
        {
            if (raw == null)
                throw new ArgumentNullException(nameof(raw));

            if (raw.IsText && raw.Text != null)
                return raw.Text;

            try
            {
                return raw.Data.Length == 0
                    ? string.Empty
                    : WebSocketConstants.StrictUtf8.GetString(raw.Data.Span);
            }
            catch (Exception ex)
            {
                throw new WebSocketException(
                    WebSocketError.SerializationFailed,
                    "Failed to decode WebSocket message as UTF-8 text.",
                    ex);
            }
        }
    }
}
