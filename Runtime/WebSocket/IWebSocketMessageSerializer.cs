using System;

namespace TurboHTTP.WebSocket
{
    public interface IWebSocketMessageSerializer<T>
    {
        WebSocketOpcode MessageType { get; }

        ReadOnlyMemory<byte> Serialize(T message);

        T Deserialize(WebSocketMessage raw);
    }

    public sealed class RawStringSerializer : IWebSocketMessageSerializer<string>
    {
        public static readonly RawStringSerializer Instance = new RawStringSerializer();

        public WebSocketOpcode MessageType => WebSocketOpcode.Text;

        public ReadOnlyMemory<byte> Serialize(string message)
        {
            message = message ?? string.Empty;
            return WebSocketConstants.StrictUtf8.GetBytes(message);
        }

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
