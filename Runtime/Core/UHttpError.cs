using System;
using System.Net;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Categorizes types of HTTP errors for better error handling.
    /// </summary>
    public enum UHttpErrorType
    {
        /// <summary>Network connectivity issue (no internet, DNS failure, connection refused)</summary>
        NetworkError,

        /// <summary>Request timeout</summary>
        Timeout,

        /// <summary>HTTP error status code (4xx, 5xx)</summary>
        HttpError,

        /// <summary>SSL/TLS certificate validation failure</summary>
        CertificateError,

        /// <summary>Request cancelled by user</summary>
        Cancelled,

        /// <summary>Invalid request configuration</summary>
        InvalidRequest,

        /// <summary>Unexpected exception</summary>
        Unknown
    }

    /// <summary>
    /// Structured error information for failed HTTP requests.
    /// </summary>
    public class UHttpError
    {
        public UHttpErrorType Type { get; }
        public string Message { get; }
        public Exception InnerException { get; }
        public HttpStatusCode? StatusCode { get; }

        public UHttpError(
            UHttpErrorType type,
            string message,
            Exception innerException = null,
            HttpStatusCode? statusCode = null)
        {
            Type = type;
            Message = message ?? "Unknown error";
            InnerException = innerException;
            StatusCode = statusCode;
        }

        /// <summary>
        /// Returns true if this error is retryable.
        /// Network errors and timeouts are typically retryable.
        /// 4xx client errors are not retryable.
        /// 5xx server errors may be retryable.
        /// </summary>
        public bool IsRetryable()
        {
            switch (Type)
            {
                case UHttpErrorType.NetworkError:
                case UHttpErrorType.Timeout:
                    return true;

                case UHttpErrorType.HttpError:
                    // 5xx server errors are retryable, 4xx client errors are not
                    if (StatusCode.HasValue)
                    {
                        int code = (int)StatusCode.Value;
                        return code >= 500 && code < 600;
                    }
                    return false;

                case UHttpErrorType.Cancelled:
                case UHttpErrorType.CertificateError:
                case UHttpErrorType.InvalidRequest:
                case UHttpErrorType.Unknown:
                default:
                    return false;
            }
        }

        public override string ToString()
        {
            var statusPart = StatusCode.HasValue ? $" (HTTP {(int)StatusCode.Value})" : "";
            return $"[{Type}]{statusPart} {Message}";
        }
    }

    /// <summary>
    /// Exception thrown by TurboHTTP when a request fails.
    /// </summary>
    public class UHttpException : Exception
    {
        public UHttpError HttpError { get; }

        public UHttpException(UHttpError error)
            : base((error ?? throw new ArgumentNullException(nameof(error))).Message, error.InnerException)
        {
            HttpError = error;
        }

        public override string ToString()
        {
            return $"UHttpException: {HttpError}";
        }
    }
}
