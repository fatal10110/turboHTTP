using System;
using System.Net;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Represents an HTTP response.
    /// </summary>
    public class UHttpResponse
    {
        public HttpStatusCode StatusCode { get; }
        public HttpHeaders Headers { get; }
        public byte[] Body { get; }
        public TimeSpan ElapsedTime { get; }

        /// <summary>
        /// The original request that generated this response.
        /// </summary>
        public UHttpRequest Request { get; }

        /// <summary>
        /// Error that occurred during the request, if any.
        /// Null if the request succeeded.
        /// </summary>
        public UHttpError Error { get; }

        public UHttpResponse(
            HttpStatusCode statusCode,
            HttpHeaders headers,
            byte[] body,
            TimeSpan elapsedTime,
            UHttpRequest request,
            UHttpError error = null)
        {
            StatusCode = statusCode;
            Headers = headers ?? new HttpHeaders();
            Body = body;
            ElapsedTime = elapsedTime;
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Error = error;
        }

        /// <summary>
        /// Returns true if the status code is in the 2xx range.
        /// </summary>
        public bool IsSuccessStatusCode =>
            (int)StatusCode >= 200 && (int)StatusCode < 300;

        /// <summary>
        /// Returns true if an error occurred during the request.
        /// </summary>
        public bool IsError => Error != null;

        /// <summary>
        /// Get the response body as a UTF-8 string.
        /// Returns null if body is null or empty.
        /// </summary>
        public string GetBodyAsString()
        {
            if (Body == null || Body.Length == 0)
                return null;

            return System.Text.Encoding.UTF8.GetString(Body);
        }

        /// <summary>
        /// Throws an exception if the response is not successful or has an error.
        /// </summary>
        public void EnsureSuccessStatusCode()
        {
            if (IsError)
            {
                throw new UHttpException(Error);
            }

            if (!IsSuccessStatusCode)
            {
                var errorMsg = $"HTTP request failed with status {(int)StatusCode} {StatusCode}";
                var error = new UHttpError(
                    UHttpErrorType.HttpError,
                    errorMsg,
                    statusCode: StatusCode
                );
                throw new UHttpException(error);
            }
        }

        public override string ToString()
        {
            return $"{(int)StatusCode} {StatusCode} ({ElapsedTime.TotalMilliseconds:F0}ms)";
        }
    }
}
