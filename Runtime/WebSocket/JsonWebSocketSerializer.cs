using System;
using TurboHTTP.Core;

namespace TurboHTTP.WebSocket
{
    public sealed class JsonWebSocketSerializer<T> : IWebSocketMessageSerializer<T>
        where T : class
    {
        public WebSocketOpcode MessageType => WebSocketOpcode.Text;

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

        public T Deserialize(WebSocketMessage raw)
        {
            if (raw == null)
                throw new ArgumentNullException(nameof(raw));

            try
            {
                string json;
                if (raw.IsText && raw.Text != null)
                {
                    json = raw.Text;
                }
                else if (raw.Length == 0)
                {
                    json = string.Empty;
                }
                else
                {
                    json = WebSocketConstants.StrictUtf8.GetString(raw.Data.Span);
                }

                var deserialized = ProjectJsonBridge.Deserialize(
                    json,
                    typeof(T),
                    requiredBy: "WebSocket JSON deserialization");

                return (T)deserialized;
            }
            catch (Exception ex)
            {
                throw new WebSocketException(
                    WebSocketError.SerializationFailed,
                    "Failed to deserialize WebSocket JSON payload.",
                    ex);
            }
        }
    }
}
