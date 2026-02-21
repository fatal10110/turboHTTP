using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Message-level extension transform contract for the WebSocket pipeline.
    /// </summary>
    public interface IWebSocketExtension : IDisposable
    {
        string Name { get; }

        byte RsvBitMask { get; }

        IReadOnlyList<WebSocketExtensionOffer> BuildOffers();

        bool AcceptNegotiation(WebSocketExtensionParameters serverParams);

        IMemoryOwner<byte> TransformOutbound(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, out byte rsvBits);

        IMemoryOwner<byte> TransformInbound(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, byte rsvBits);

        void Reset();
    }

    /// <summary>
    /// Structured extension offer used to build Sec-WebSocket-Extensions header values.
    /// </summary>
    public readonly struct WebSocketExtensionOffer
    {
        private readonly IReadOnlyDictionary<string, string> _parameters;

        public WebSocketExtensionOffer(string extensionToken, IReadOnlyDictionary<string, string> parameters = null)
        {
            if (!IsValidToken(extensionToken))
            {
                throw new ArgumentException(
                    "Extension token must be a valid HTTP token.",
                    nameof(extensionToken));
            }

            ExtensionToken = extensionToken;
            _parameters = parameters ?? new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
        }

        public string ExtensionToken { get; }

        public IReadOnlyDictionary<string, string> Parameters => _parameters;

        public string ToHeaderValue()
        {
            var builder = new StringBuilder(64);
            builder.Append(ExtensionToken);

            foreach (var parameter in _parameters)
            {
                if (!IsValidToken(parameter.Key))
                {
                    throw new FormatException("Extension parameter name is not a valid token: " + parameter.Key);
                }

                builder.Append("; ");
                builder.Append(parameter.Key);

                if (parameter.Value == null)
                    continue;

                builder.Append('=');
                AppendParameterValue(builder, parameter.Value);
            }

            return builder.ToString();
        }

        public override string ToString()
        {
            return ToHeaderValue();
        }

        private static void AppendParameterValue(StringBuilder builder, string value)
        {
            if (value.Length == 0)
            {
                builder.Append("\"\"");
                return;
            }

            bool needsQuotes = false;
            for (int i = 0; i < value.Length; i++)
            {
                if (!IsTokenChar(value[i]))
                {
                    needsQuotes = true;
                    break;
                }
            }

            if (!needsQuotes)
            {
                builder.Append(value);
                return;
            }

            builder.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"' || c == '\\')
                    builder.Append('\\');

                builder.Append(c);
            }

            builder.Append('"');
        }

        internal static bool IsValidToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                return false;

            for (int i = 0; i < token.Length; i++)
            {
                if (!IsTokenChar(token[i]))
                    return false;
            }

            return true;
        }

        internal static bool IsTokenChar(char c)
        {
            if (c <= 32 || c >= 127)
                return false;

            switch (c)
            {
                case '(': case ')': case '<': case '>': case '@':
                case ',': case ';': case ':': case '\\': case '"':
                case '/': case '[': case ']': case '?': case '=':
                case '{': case '}':
                    return false;
                default:
                    return true;
            }
        }
    }

    /// <summary>
    /// Parsed extension entry from Sec-WebSocket-Extensions negotiation.
    /// </summary>
    public sealed class WebSocketExtensionParameters
    {
        private readonly IReadOnlyDictionary<string, string> _parameters;

        public WebSocketExtensionParameters(string extensionToken, IReadOnlyDictionary<string, string> parameters)
        {
            if (!WebSocketExtensionOffer.IsValidToken(extensionToken))
            {
                throw new ArgumentException(
                    "Extension token must be a valid HTTP token.",
                    nameof(extensionToken));
            }

            ExtensionToken = extensionToken;
            _parameters = parameters ?? new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
        }

        public string ExtensionToken { get; }

        public IReadOnlyDictionary<string, string> Parameters => _parameters;

        public static WebSocketExtensionParameters Parse(string headerValue)
        {
            if (string.IsNullOrWhiteSpace(headerValue))
                throw new FormatException("Extension header value is empty.");

            int index = 0;
            SkipOws(headerValue, ref index);
            string extensionToken = ReadToken(headerValue, ref index, "extension token");
            SkipOws(headerValue, ref index);

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            while (index < headerValue.Length)
            {
                if (headerValue[index] != ';')
                    throw new FormatException("Expected ';' before extension parameter list.");

                index++;
                SkipOws(headerValue, ref index);

                string name = ReadToken(headerValue, ref index, "parameter name");
                SkipOws(headerValue, ref index);

                string value = null;
                if (index < headerValue.Length && headerValue[index] == '=')
                {
                    index++;
                    SkipOws(headerValue, ref index);
                    value = ReadParameterValue(headerValue, ref index);
                    SkipOws(headerValue, ref index);
                }

                if (parameters.ContainsKey(name))
                    throw new FormatException("Duplicate extension parameter: " + name);

                parameters[name] = value;
            }

            return new WebSocketExtensionParameters(extensionToken, parameters);
        }

        private static void SkipOws(string value, ref int index)
        {
            while (index < value.Length)
            {
                char c = value[index];
                if (c != ' ' && c != '\t')
                    break;

                index++;
            }
        }

        private static string ReadToken(string value, ref int index, string tokenName)
        {
            int start = index;
            while (index < value.Length && WebSocketExtensionOffer.IsTokenChar(value[index]))
                index++;

            if (index == start)
                throw new FormatException("Expected " + tokenName + ".");

            return value.Substring(start, index - start);
        }

        private static string ReadParameterValue(string value, ref int index)
        {
            if (index >= value.Length)
                throw new FormatException("Expected extension parameter value.");

            if (value[index] == '"')
                return ReadQuotedValue(value, ref index);

            return ReadToken(value, ref index, "parameter value");
        }

        private static string ReadQuotedValue(string value, ref int index)
        {
            if (value[index] != '"')
                throw new FormatException("Expected quoted-string value.");

            index++; // opening DQUOTE
            var builder = new StringBuilder();

            while (index < value.Length)
            {
                char c = value[index++];

                if (c == '"')
                    return builder.ToString();

                if (c == '\\')
                {
                    if (index >= value.Length)
                        throw new FormatException("Invalid quoted-string escape sequence.");

                    builder.Append(value[index++]);
                    continue;
                }

                if (c < 0x20 && c != '\t')
                    throw new FormatException("Quoted-string contains an invalid control character.");

                builder.Append(c);
            }

            throw new FormatException("Unterminated quoted-string value.");
        }
    }
}
