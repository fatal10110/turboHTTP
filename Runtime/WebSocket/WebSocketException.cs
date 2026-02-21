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
                    return true;

                case WebSocketError.InvalidFrame:
                case WebSocketError.InvalidCloseCode:
                case WebSocketError.InvalidUtf8:
                case WebSocketError.MaskedServerFrame:
                case WebSocketError.UnexpectedContinuation:
                case WebSocketError.ReservedOpcode:
                case WebSocketError.ProtocolViolation:
                case WebSocketError.PayloadLengthOverflow:
                case WebSocketError.HandshakeFailed:
                case WebSocketError.MessageTooLarge:
                default:
                    return false;
            }
        }

        private static UHttpErrorType MapErrorType(WebSocketError error)
        {
            switch (error)
            {
                case WebSocketError.HandshakeFailed:
                case WebSocketError.InvalidFrame:
                case WebSocketError.InvalidCloseCode:
                case WebSocketError.InvalidUtf8:
                case WebSocketError.MaskedServerFrame:
                case WebSocketError.UnexpectedContinuation:
                case WebSocketError.ReservedOpcode:
                case WebSocketError.ProtocolViolation:
                case WebSocketError.PayloadLengthOverflow:
                case WebSocketError.MessageTooLarge:
                    return UHttpErrorType.InvalidRequest;

                case WebSocketError.ConnectionClosed:
                case WebSocketError.PongTimeout:
                case WebSocketError.SendFailed:
                case WebSocketError.ReceiveFailed:
                    return UHttpErrorType.NetworkError;

                default:
                    return UHttpErrorType.Unknown;
            }
        }
    }
}
