using System;
using TurboHTTP.Core;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Exception for WebSocket lifecycle and protocol failures.
    /// </summary>
    public class WebSocketException : UHttpException
    {
        public WebSocketException(string message)
            : this(WebSocketError.ConnectionClosed, message)
        {
        }

        public WebSocketException(string message, Exception innerException)
            : this(WebSocketError.ConnectionClosed, message, innerException)
        {
        }

        public WebSocketException(WebSocketCloseCode closeCode, string closeReason)
            : this(
                WebSocketError.ConnectionClosed,
                "WebSocket closed with code " + (int)closeCode + ".",
                null,
                closeCode,
                closeReason)
        {
        }

        public WebSocketException(
            WebSocketError error,
            string message,
            Exception innerException = null,
            WebSocketCloseCode? closeCode = null,
            string closeReason = null)
            : base(new UHttpError(MapErrorType(error), message, innerException))
        {
            Error = error;
            CloseCode = closeCode;
            CloseReason = closeReason;
        }

        internal WebSocketException(
            WebSocketError error,
            string message,
            Exception innerException,
            int? handshakeStatusCode)
            : this(error, message, innerException)
        {
            HandshakeStatusCode = handshakeStatusCode;
        }

        /// <summary> Gets the specific WebSocket error categorization. </summary>
        public WebSocketError Error { get; }

        /// <summary> Gets the closure code provided by the remote endpoint, if applicable. </summary>
        public WebSocketCloseCode? CloseCode { get; }

        /// <summary> Gets the closure reason provided by the remote endpoint, if applicable. </summary>
        public string CloseReason { get; }

        /// <summary>
        /// Gets the HTTP status code of the server's response when the opening handshake failed after a
        /// status line was received (for example 401 or 403; RFC 6455 section 4.1: any status other than
        /// 101 fails the handshake), or <c>null</c> when no response status is involved.
        /// </summary>
        public int? HandshakeStatusCode { get; }

        /// <summary> Determines if the error represents a potentially transient state that can be retried. </summary>
        public bool IsRetryable()
        {
            switch (Error)
            {
                case WebSocketError.ConnectionClosed:
                case WebSocketError.PongTimeout:
                case WebSocketError.SendFailed:
                case WebSocketError.ReceiveFailed:
                case WebSocketError.ProxyConnectionFailed:
                    return true;

                case WebSocketError.InvalidFrame:
                case WebSocketError.InvalidCloseCode:
                case WebSocketError.InvalidUtf8:
                case WebSocketError.MaskedServerFrame:
                case WebSocketError.UnexpectedContinuation:
                case WebSocketError.ReservedOpcode:
                case WebSocketError.ProtocolViolation:
                case WebSocketError.PayloadLengthOverflow:
                case WebSocketError.SerializationFailed:
                case WebSocketError.ProxyAuthenticationRequired:
                case WebSocketError.HandshakeFailed:
                case WebSocketError.ExtensionNegotiationFailed:
                case WebSocketError.MessageTooLarge:
                case WebSocketError.CompressionFailed:
                case WebSocketError.DecompressionFailed:
                case WebSocketError.DecompressedMessageTooLarge:
                case WebSocketError.CleartextNotPermitted:
                default:
                    return false;
            }
        }

        private static UHttpErrorType MapErrorType(WebSocketError error)
        {
            switch (error)
            {
                case WebSocketError.HandshakeFailed:
                case WebSocketError.ExtensionNegotiationFailed:
                case WebSocketError.InvalidFrame:
                case WebSocketError.InvalidCloseCode:
                case WebSocketError.InvalidUtf8:
                case WebSocketError.MaskedServerFrame:
                case WebSocketError.UnexpectedContinuation:
                case WebSocketError.ReservedOpcode:
                case WebSocketError.ProtocolViolation:
                case WebSocketError.PayloadLengthOverflow:
                case WebSocketError.SerializationFailed:
                case WebSocketError.ProxyAuthenticationRequired:
                case WebSocketError.MessageTooLarge:
                case WebSocketError.CompressionFailed:
                case WebSocketError.DecompressionFailed:
                case WebSocketError.DecompressedMessageTooLarge:
                case WebSocketError.CleartextNotPermitted:
                    return UHttpErrorType.InvalidRequest;

                case WebSocketError.ConnectionClosed:
                case WebSocketError.PongTimeout:
                case WebSocketError.SendFailed:
                case WebSocketError.ReceiveFailed:
                case WebSocketError.ProxyConnectionFailed:
                case WebSocketError.ProxyTunnelFailed:
                    return UHttpErrorType.NetworkError;

                default:
                    return UHttpErrorType.Unknown;
            }
        }
    }
}
