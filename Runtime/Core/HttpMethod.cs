namespace TurboHTTP.Core
{
    /// <summary>
    /// HTTP methods (verbs) for requests.
    /// </summary>
    public enum HttpMethod
    {
        GET,
        POST,
        PUT,
        DELETE,
        PATCH,
        HEAD,
        OPTIONS
    }

    /// <summary>
    /// Extension methods for HttpMethod.
    /// </summary>
    public static class HttpMethodExtensions
    {
        private static readonly string[] MethodStrings =
        {
            "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"
        };

        /// <summary>
        /// Returns true if this HTTP method is considered idempotent.
        /// Idempotent methods can be safely retried without side effects.
        /// </summary>
        public static bool IsIdempotent(this HttpMethod method)
        {
            return method == HttpMethod.GET
                || method == HttpMethod.HEAD
                || method == HttpMethod.PUT
                || method == HttpMethod.DELETE
                || method == HttpMethod.OPTIONS;
        }

        /// <summary>
        /// Returns true if this HTTP method typically has a request body.
        /// </summary>
        public static bool HasBody(this HttpMethod method)
        {
            return method == HttpMethod.POST
                || method == HttpMethod.PUT
                || method == HttpMethod.PATCH;
        }

        /// <summary>
        /// Converts HttpMethod to its uppercase string representation.
        /// Uses pre-allocated strings to avoid GC allocations.
        /// </summary>
        public static string ToUpperString(this HttpMethod method)
        {
            int index = (int)method;
            if (index >= 0 && index < MethodStrings.Length)
                return MethodStrings[index];
            return method.ToString().ToUpperInvariant();
        }
    }
}
