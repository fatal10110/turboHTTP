using System;
using System.Collections.Generic;

namespace TurboHTTP.Transport.Internal
{
    internal static class TrailerFieldValidator
    {
        internal static readonly HashSet<string> ProhibitedResponseTrailers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Transfer-Encoding",
                "Content-Length",
                "Host",
                "Trailer",
                "Connection",
                "Keep-Alive",
                "Proxy-Connection",
                "Upgrade",
                "TE",
                "Content-Encoding",
                "Content-Type",
                "Content-Range",
                "Authorization",
                "Proxy-Authorization",
                "WWW-Authenticate",
                "Proxy-Authenticate",
                "Cache-Control",
                "Expect",
                "Max-Forwards",
                "Pragma",
                "Range",
                "If-Match",
                "If-None-Match",
                "If-Modified-Since",
                "If-Unmodified-Since",
                "If-Range",
                "Age",
                "Expires",
                "Date",
                "Location",
                "Content-Location",
                "Retry-After",
                "Vary"
            };

        internal static readonly HashSet<string> ProhibitedRequestTrailers =
            new HashSet<string>(ProhibitedResponseTrailers, StringComparer.OrdinalIgnoreCase);

        internal static bool IsProhibitedResponseTrailer(string name)
        {
            return ProhibitedResponseTrailers.Contains(name);
        }

        internal static bool IsProhibitedRequestTrailer(string name)
        {
            return ProhibitedRequestTrailers.Contains(name);
        }
    }
}
