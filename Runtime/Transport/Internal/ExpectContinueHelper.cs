using System;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Internal
{
    internal static class ExpectContinueHelper
    {
        internal static bool ShouldAwaitExpectContinue(UHttpRequest request)
        {
            return request != null &&
                HasExpectContinueHeader(request.Headers) &&
                HasRequestBody(request.Content);
        }

        internal static bool HasRequestBody(UHttpRequestBody content)
        {
            if (content == null)
                return false;

            if (content.TrailerProvider != null)
                return true;

            if (content.TryGetBufferedData(out var buffered))
                return !buffered.IsEmpty;

            return !content.Length.HasValue || content.Length.Value > 0;
        }

        internal static bool TryGetKnownRequestBodyLength(UHttpRequestBody content, out long length)
        {
            length = 0;
            if (content == null)
                return false;

            if (content.TryGetBufferedData(out var buffered))
            {
                length = buffered.Length;
                return true;
            }

            if (!content.Length.HasValue)
                return false;

            length = content.Length.Value;
            return true;
        }

        internal static bool HasExpectContinueHeader(HttpHeaders headers)
        {
            if (headers == null || !headers.Contains("Expect"))
                return false;

            var values = headers.GetValues("Expect");
            for (int i = 0; i < values.Count; i++)
            {
                if (HeaderValueContainsToken(values[i], "100-continue"))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True when the request carries the 100-continue expectation and no other one: every
        /// member of every <c>Expect</c> field line is <c>100-continue</c> (RFC 9110 Section 10.1.1).
        /// A 417 to a request with another expectation may be about that one, so only this shape is
        /// repeated without <c>Expect</c>.
        /// </summary>
        internal static bool HasOnlyExpectContinueExpectation(HttpHeaders headers)
        {
            if (headers == null || !headers.Contains("Expect"))
                return false;

            var tokenSpan = "100-continue".AsSpan();
            bool sawContinue = false;
            var values = headers.GetValues("Expect");
            for (int i = 0; i < values.Count; i++)
            {
                var span = (values[i] ?? string.Empty).AsSpan();
                while (true)
                {
                    int comma = span.IndexOf(',');
                    var segment = TrimWhitespace(comma >= 0 ? span.Slice(0, comma) : span);
                    if (segment.Length > 0)
                    {
                        if (!segment.Equals(tokenSpan, StringComparison.OrdinalIgnoreCase))
                            return false;
                        sawContinue = true;
                    }

                    if (comma < 0)
                        break;

                    span = span.Slice(comma + 1);
                }
            }

            return sawContinue;
        }

        internal static bool HeaderValueContainsToken(string value, string token)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrEmpty(token))
                return false;

            var span = value.AsSpan();
            var tokenSpan = token.AsSpan();
            while (span.Length > 0)
            {
                int comma = span.IndexOf(',');
                var segment = comma >= 0 ? span.Slice(0, comma) : span;
                segment = TrimWhitespace(segment);
                if (segment.Equals(tokenSpan, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (comma < 0)
                    break;

                span = span.Slice(comma + 1);
            }

            return false;
        }

        private static ReadOnlySpan<char> TrimWhitespace(ReadOnlySpan<char> value)
        {
            int start = 0;
            while (start < value.Length && char.IsWhiteSpace(value[start]))
                start++;

            int end = value.Length - 1;
            while (end >= start && char.IsWhiteSpace(value[end]))
                end--;

            return end >= start
                ? value.Slice(start, end - start + 1)
                : ReadOnlySpan<char>.Empty;
        }
    }
}
