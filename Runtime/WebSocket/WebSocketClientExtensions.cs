using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.WebSocket
{
    public static class WebSocketClientExtensions
    {
        public static async Task SendAsync<T>(
            this IWebSocketClient client,
            T message,
            IWebSocketMessageSerializer<T> serializer,
            CancellationToken ct = default)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));
            if (serializer == null)
                throw new ArgumentNullException(nameof(serializer));

            var payload = serializer.Serialize(message);

            if (serializer.MessageType == WebSocketOpcode.Text)
            {
                string text;
                try
                {
                    text = payload.Length == 0
                        ? string.Empty
                        : WebSocketConstants.StrictUtf8.GetString(payload.Span);
                }
                catch (Exception ex)
                {
                    throw new WebSocketException(
                        WebSocketError.SerializationFailed,
                        "Typed WebSocket serializer produced invalid UTF-8 text bytes.",
                        ex);
                }

                await client.SendAsync(text, ct).ConfigureAwait(false);
                return;
            }

            if (serializer.MessageType == WebSocketOpcode.Binary)
            {
                await client.SendAsync(payload, ct).ConfigureAwait(false);
                return;
            }

            throw new ArgumentException(
                "Serializer MessageType must be Text or Binary.",
                nameof(serializer));
        }

        public static async Task<T> ReceiveAsync<T>(
            this IWebSocketClient client,
            IWebSocketMessageSerializer<T> serializer,
            CancellationToken ct = default)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));
            if (serializer == null)
                throw new ArgumentNullException(nameof(serializer));

            using var raw = await client.ReceiveAsync(ct).ConfigureAwait(false);
            ValidateMessageType(raw, serializer.MessageType);
            return serializer.Deserialize(raw);
        }

        public static async IAsyncEnumerable<T> ReceiveAllAsync<T>(
            this IWebSocketClient client,
            IWebSocketMessageSerializer<T> serializer,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));
            if (serializer == null)
                throw new ArgumentNullException(nameof(serializer));

            await foreach (var raw in client.ReceiveAllAsync(ct).WithCancellation(ct))
            {
                using (raw)
                {
                    ValidateMessageType(raw, serializer.MessageType);
                    yield return serializer.Deserialize(raw);
                }
            }
        }

        private static void ValidateMessageType(WebSocketMessage message, WebSocketOpcode serializerType)
        {
            if (serializerType == WebSocketOpcode.Text && !message.IsText)
            {
                throw new WebSocketException(
                    WebSocketError.SerializationFailed,
                    "Expected a text WebSocket message for the configured serializer.");
            }

            if (serializerType == WebSocketOpcode.Binary && !message.IsBinary)
            {
                throw new WebSocketException(
                    WebSocketError.SerializationFailed,
                    "Expected a binary WebSocket message for the configured serializer.");
            }
        }
    }
}
