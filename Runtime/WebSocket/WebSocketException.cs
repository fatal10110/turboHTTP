using System;
using System.Net;
using TurboHTTP.Core;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Exception for WebSocket lifecycle and protocol failures.
    /// </summary>
    public class WebSocketException : UHttpException
    {
        /// <summary>Creates a <see cref="WebSocketError.ConnectionClosed"/> exception.</summary>
        public WebSocketException(string message)
            : this(WebSocketError.ConnectionClosed, message)
        {
        }

        /// <summary>Creates a <see cref="WebSocketError.ConnectionClosed"/> exception with an inner exception.</summary>
        public WebSocketException(string message, Exception innerException)
            : this(WebSocketError.ConnectionClosed, message, innerException)
        {
        }

        /// <summary>Creates a <see cref="WebSocketError.ConnectionClosed"/> exception for a close with <paramref name="closeCode"/> and <paramref name="closeReason"/>.</summary>
        public WebSocketException(WebSocketCloseCode closeCode, string closeReason)
            : this(
                WebSocketError.ConnectionClosed,
                "WebSocket closed with code " + (int)closeCode + ".",
                null,
                closeCode,
                closeReason)
        {
        }

        /// <summary>Creates an exception for <paramref name="error"/> with an optional inner exception and close status.</summary>
        public WebSocketException(
            WebSocketError error,
            string message,
            Exception innerException = null,
            WebSocketCloseCode? closeCode = null,
            string closeReason = null)
            : this(error, message, innerException, closeCode, closeReason, null)
        {
        }

        /// <summary>
        /// A failed opening handshake: <paramref name="handshakeStatusCode"/> is also the
        /// <see cref="UHttpError.StatusCode"/>, and a 4xx/5xx status makes the error type
        /// <see cref="UHttpErrorType.HttpError"/> (#1139).
        /// </summary>
        internal WebSocketException(
            WebSocketError error,
            string message,
            Exception innerException,
            int? handshakeStatusCode)
            : this(error, message, innerException, null, null, handshakeStatusCode)
        {
        }

        private WebSocketException(
            WebSocketError error,
            string message,
            Exception innerException,
            WebSocketCloseCode? closeCode,
            string closeReason,
            int? handshakeStatusCode)
            : base(new UHttpError(
                MapErrorType(error, handshakeStatusCode),
                message,
                innerException,
                handshakeStatusCode.HasValue ? (HttpStatusCode)handshakeStatusCode.Value : (HttpStatusCode?)null))
        {
            Error = error;
            CloseCode = closeCode;
            CloseReason = closeReason;
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

        /// <summary>
        /// Determines if the error represents a potentially transient state that can be retried, by the
        /// same rule as <see cref="UHttpError.IsRetryable"/>: dropped connections, network and proxy
        /// failures, DNS failures and timeouts are retryable; certificate failures, invalid requests and
        /// protocol violations are not. A rejected opening handshake is retryable only for 5xx, matching
        /// <see cref="UHttpError.IsRetryable"/> for <see cref="UHttpErrorType.HttpError"/>. A 408 or 429
        /// rejection is not retryable here but is not permanent either (see
        /// <see cref="IsPermanentConnectFailure"/>), so the resilient client still reconnects after one.
        /// </summary>
        public bool IsRetryable()
        {
            switch (Error)
            {
                case WebSocketError.ConnectionClosed:
                case WebSocketError.PongTimeout:
                case WebSocketError.SendFailed:
                case WebSocketError.ReceiveFailed:
                case WebSocketError.ProxyConnectionFailed:
                case WebSocketError.ProxyTunnelFailed:
                case WebSocketError.Timeout:
                case WebSocketError.DnsResolutionFailed:
                    return true;

                case WebSocketError.HandshakeFailed:
                    return HandshakeStatusCode.HasValue &&
                        HandshakeStatusCode.Value >= 500 && HandshakeStatusCode.Value < 600;

                case WebSocketError.InvalidFrame:
                case WebSocketError.FrameTooLarge:
                case WebSocketError.InvalidCloseCode:
                case WebSocketError.InvalidUtf8:
                case WebSocketError.MaskedServerFrame:
                case WebSocketError.UnexpectedContinuation:
                case WebSocketError.ReservedOpcode:
                case WebSocketError.ProtocolViolation:
                case WebSocketError.PayloadLengthOverflow:
                case WebSocketError.SerializationFailed:
                case WebSocketError.ProxyAuthenticationRequired:
                case WebSocketError.ExtensionNegotiationFailed:
                case WebSocketError.MessageTooLarge:
                case WebSocketError.CompressionFailed:
                case WebSocketError.DecompressionFailed:
                case WebSocketError.DecompressedMessageTooLarge:
                case WebSocketError.CleartextNotPermitted:
                case WebSocketError.CertificateError:
                case WebSocketError.InvalidRequest:
                default:
                    return false;
            }
        }

        /// <summary>
        /// True when a connect attempt that failed with this error fails the same way on every attempt,
        /// so the resilient client stops reconnecting: a certificate failure, an invalid request, a
        /// cleartext policy refusal, a 407 from the proxy, and a 4xx rejection of the opening handshake
        /// other than 408 and 429 (RFC 9110 section 15.5.9, RFC 6585 section 4: the server may accept a
        /// later attempt). Everything <see cref="IsRetryable"/> calls transient is not permanent.
        /// </summary>
        internal bool IsPermanentConnectFailure()
        {
            switch (Error)
            {
                case WebSocketError.CertificateError:
                case WebSocketError.InvalidRequest:
                case WebSocketError.CleartextNotPermitted:
                case WebSocketError.ProxyAuthenticationRequired:
                    return true;

                case WebSocketError.HandshakeFailed:
                    if (!HandshakeStatusCode.HasValue)
                        return false;

                    int status = HandshakeStatusCode.Value;
                    return status >= 400 && status < 500 && !IsTransientHandshakeStatus(status);

                default:
                    return false;
            }
        }

        private static bool IsTransientHandshakeStatus(int status) =>
            status == 408 || status == 429 || (status >= 500 && status < 600);

        private static UHttpErrorType MapErrorType(WebSocketError error, int? handshakeStatusCode)
        {
            switch (error)
            {
                case WebSocketError.HandshakeFailed:
                    return handshakeStatusCode >= 400 && handshakeStatusCode < 600
                        ? UHttpErrorType.HttpError
                        : UHttpErrorType.InvalidRequest;

                case WebSocketError.ExtensionNegotiationFailed:
                case WebSocketError.InvalidFrame:
                case WebSocketError.FrameTooLarge:
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
                case WebSocketError.InvalidRequest:
                    return UHttpErrorType.InvalidRequest;

                case WebSocketError.ConnectionClosed:
                case WebSocketError.PongTimeout:
                case WebSocketError.SendFailed:
                case WebSocketError.ReceiveFailed:
                case WebSocketError.ProxyConnectionFailed:
                case WebSocketError.ProxyTunnelFailed:
                case WebSocketError.DnsResolutionFailed:
                    return UHttpErrorType.NetworkError;

                case WebSocketError.Timeout:
                    return UHttpErrorType.Timeout;

                case WebSocketError.CertificateError:
                    return UHttpErrorType.CertificateError;

                default:
                    return UHttpErrorType.Unknown;
            }
        }
    }
}
