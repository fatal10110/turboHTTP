using System;
using System.Buffers;

namespace TurboHTTP.WebSocket
{
    /// <summary> Defines the type of message payload. </summary>
    public enum WebSocketMessageType
    {
        /// <summary> Text payload (UTF-8). </summary>
        Text = 0,
        /// <summary> Binary payload. </summary>
        Binary = 1
    }

    /// <summary>
    /// Leased incoming message payload. Dispose to return pooled data buffer.
    /// </summary>
    public sealed class WebSocketMessage : IDisposable
    {
        private byte[] _buffer;
        private int _length;
        private readonly bool _returnToPool;

        internal WebSocketMessage(
            WebSocketMessageType type,
            byte[] buffer,
            int length,
            string text,
            bool returnToPool = true)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative.");
            if (length > 0 && buffer == null)
                throw new ArgumentNullException(nameof(buffer), "Non-empty message requires a payload buffer.");

            Type = type;
            _buffer = buffer;
            _length = length;
            Text = text;
            _returnToPool = returnToPool;
        }

        /// <summary> Gets the payload type (Text or Binary). </summary>
        public WebSocketMessageType Type { get; }

        /// <summary> Gets whether this is a text message. </summary>
        public bool IsText => Type == WebSocketMessageType.Text;

        /// <summary> Gets whether this is a binary message. </summary>
        public bool IsBinary => Type == WebSocketMessageType.Binary;

        /// <summary> Gets the decoded string for text messages. Null for binary messages. </summary>
        public string Text { get; }

        /// <summary> Gets the binary payload data. </summary>
        public ReadOnlyMemory<byte> Data
        {
            get
            {
                var buffer = _buffer;
                if (buffer == null || _length == 0)
                    return ReadOnlyMemory<byte>.Empty;

                return new ReadOnlyMemory<byte>(buffer, 0, _length);
            }
        }

        public int Length => _length;

        public void Dispose()
        {
            var buffer = _buffer;
            if (buffer == null)
                return;

            _buffer = null;
            _length = 0;

            if (_returnToPool)
                ArrayPool<byte>.Shared.Return(buffer);
        }

        public static WebSocketMessage CreateDetachedCopy(WebSocketMessage source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            int length = source.Length;
            if (length == 0)
            {
                return new WebSocketMessage(
                    source.Type,
                    buffer: null,
                    length: 0,
                    text: source.IsText ? source.Text : null,
                    returnToPool: false);
            }

            var copy = new byte[length];
            source.Data.CopyTo(copy);
            return new WebSocketMessage(
                source.Type,
                copy,
                length,
                source.IsText ? source.Text : null,
                returnToPool: false);
        }
    }
}
