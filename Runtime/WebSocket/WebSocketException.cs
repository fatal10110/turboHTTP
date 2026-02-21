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

        public WebSocketError Error { get; }

        public WebSocketCloseCode? CloseCode { get; }

        public string CloseReason { get; }

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
