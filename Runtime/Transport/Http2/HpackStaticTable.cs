using System;

namespace TurboHTTP.Transport.Http2
{
    internal enum HpackMatchType
    {
        None,
        NameMatch,
        FullMatch
    }

    /// <summary>
    /// HPACK static header table. RFC 7541 Appendix A.
    /// 61 entries, 1-indexed. Index 0 is invalid.
    /// </summary>
    internal static class HpackStaticTable
    {
        public const int Length = 61;

        private static readonly (string Name, string Value)[] s_table =
        {
            ("", ""),                              // 0 — unused sentinel
            (":authority", ""),                     // 1
            (":method", "GET"),                     // 2
            (":method", "POST"),                    // 3
            (":path", "/"),                         // 4
            (":path", "/index.html"),               // 5
            (":scheme", "http"),                    // 6
            (":scheme", "https"),                   // 7
            (":status", "200"),                     // 8
            (":status", "204"),                     // 9
            (":status", "206"),                     // 10
            (":status", "304"),                     // 11
            (":status", "400"),                     // 12
            (":status", "404"),                     // 13
            (":status", "500"),                     // 14
            ("accept-charset", ""),                 // 15
            ("accept-encoding", "gzip, deflate"),   // 16
            ("accept-language", ""),                // 17
            ("accept-ranges", ""),                  // 18
            ("accept", ""),                         // 19
            ("access-control-allow-origin", ""),    // 20
            ("age", ""),                            // 21
            ("allow", ""),                          // 22
            ("authorization", ""),                  // 23
            ("cache-control", ""),                  // 24
            ("content-disposition", ""),            // 25
            ("content-encoding", ""),               // 26
            ("content-language", ""),               // 27
            ("content-length", ""),                 // 28
            ("content-location", ""),               // 29
            ("content-range", ""),                  // 30
            ("content-type", ""),                   // 31
            ("cookie", ""),                         // 32
            ("date", ""),                           // 33
            ("etag", ""),                           // 34
            ("expect", ""),                         // 35
            ("expires", ""),                        // 36
            ("from", ""),                           // 37
            ("host", ""),                           // 38
            ("if-match", ""),                       // 39
            ("if-modified-since", ""),              // 40
            ("if-none-match", ""),                  // 41
            ("if-range", ""),                       // 42
            ("if-unmodified-since", ""),            // 43
            ("last-modified", ""),                  // 44
            ("link", ""),                           // 45
            ("location", ""),                       // 46
            ("max-forwards", ""),                   // 47
            ("proxy-authenticate", ""),             // 48
            ("proxy-authorization", ""),            // 49
            ("range", ""),                          // 50
            ("referer", ""),                        // 51
            ("refresh", ""),                        // 52
            ("retry-after", ""),                    // 53
            ("server", ""),                         // 54
            ("set-cookie", ""),                     // 55
            ("strict-transport-security", ""),      // 56
            ("transfer-encoding", ""),              // 57
            ("user-agent", ""),                     // 58
            ("vary", ""),                           // 59
            ("via", ""),                            // 60
            ("www-authenticate", ""),               // 61
        };

        public static (string Name, string Value) Get(int index)
        {
            if (index < 1 || index > Length)
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"HPACK static table index must be 1–{Length}, got {index}");
            return s_table[index];
        }

        /// <summary>
        /// Search the static table for a matching header.
        /// Returns (index, FullMatch) if both name and value match,
        /// (index, NameMatch) if only the name matches, or (0, None) if no match.
        /// Names are compared with Ordinal (caller must lowercase).
        /// </summary>
        public static (int Index, HpackMatchType Match) FindMatch(string name, string value)
        {
            int nameMatchIndex = 0;

            for (int i = 1; i <= Length; i++)
            {
                if (string.Equals(s_table[i].Name, name, StringComparison.Ordinal))
                {
                    if (string.Equals(s_table[i].Value, value, StringComparison.Ordinal))
                        return (i, HpackMatchType.FullMatch);

                    if (nameMatchIndex == 0)
                        nameMatchIndex = i;
                }
            }

            if (nameMatchIndex > 0)
                return (nameMatchIndex, HpackMatchType.NameMatch);

            return (0, HpackMatchType.None);
        }
    }
}
