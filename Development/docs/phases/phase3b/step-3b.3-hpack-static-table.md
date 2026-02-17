# Step 3B.3: HPACK Static Table

**File:** `Runtime/Transport/Http2/HpackStaticTable.cs`
**Depends on:** Nothing
**Spec:** RFC 7541 Appendix A (Static Table Definition)

## Purpose

Provide the 61-entry static header table used by HPACK for indexed header lookups. This table is shared across all connections and never changes.

## Class Design

```csharp
namespace TurboHTTP.Transport.Http2
{
    internal enum HpackMatchType
    {
        None,       // No match found
        NameMatch,  // Header name matches, value differs
        FullMatch   // Both name and value match
    }

    internal static class HpackStaticTable
    {
        public const int Length = 61;

        public static (string Name, string Value) Get(int index);
        public static (int Index, HpackMatchType Match) FindMatch(string name, string value);
    }
}
```

## Static Table Entries (RFC 7541 Appendix A)

The table is 1-indexed. Index 0 is invalid (COMPRESSION_ERROR if referenced).

```
Index | Name                    | Value
------+-------------------------+------------------
  1   | :authority              |
  2   | :method                 | GET
  3   | :method                 | POST
  4   | :path                   | /
  5   | :path                   | /index.html
  6   | :scheme                 | http
  7   | :scheme                 | https
  8   | :status                 | 200
  9   | :status                 | 204
 10   | :status                 | 206
 11   | :status                 | 304
 12   | :status                 | 400
 13   | :status                 | 404
 14   | :status                 | 500
 15   | accept-charset          |
 16   | accept-encoding         | gzip, deflate
 17   | accept-language         |
 18   | accept-ranges           |
 19   | accept                  |
 20   | access-control-allow-origin |
 21   | age                     |
 22   | allow                   |
 23   | authorization           |
 24   | cache-control           |
 25   | content-disposition     |
 26   | content-encoding        |
 27   | content-language        |
 28   | content-length          |
 29   | content-location        |
 30   | content-range           |
 31   | content-type            |
 32   | cookie                  |
 33   | date                    |
 34   | etag                    |
 35   | expect                  |
 36   | expires                 |
 37   | from                    |
 38   | host                    |
 39   | if-match                |
 40   | if-modified-since       |
 41   | if-none-match           |
 42   | if-range                |
 43   | if-unmodified-since     |
 44   | last-modified           |
 45   | link                    |
 46   | location                |
 47   | max-forwards            |
 48   | proxy-authenticate      |
 49   | proxy-authorization     |
 50   | range                   |
 51   | referer                 |
 52   | refresh                 |
 53   | retry-after             |
 54   | server                  |
 55   | set-cookie              |
 56   | strict-transport-security |
 57   | transfer-encoding       |
 58   | user-agent              |
 59   | vary                    |
 60   | via                     |
 61   | www-authenticate        |
```

## Implementation Details

### Storage

```csharp
private static readonly (string Name, string Value)[] s_table = new (string, string)[]
{
    ("", ""),                          // Index 0 — unused sentinel
    (":authority", ""),                // 1
    (":method", "GET"),                // 2
    (":method", "POST"),              // 3
    // ... all 61 entries ...
    ("www-authenticate", ""),          // 61
};
```

### `Get(int index)`

- Validate: `index >= 1 && index <= 61`. Throw `ArgumentOutOfRangeException` otherwise.
- Return `s_table[index]`.

### `FindMatch(string name, string value)`

Linear scan through all 61 entries:
1. Track the first name-only match index.
2. If both name and value match, return `(index, FullMatch)` immediately.
3. After scanning all entries, if a name-only match was found, return `(nameMatchIndex, NameMatch)`.
4. Otherwise, return `(0, None)`.

**Why linear scan is fine:** 61 entries is small. A `Dictionary` lookup would require two passes (name match + value match) and adds complexity. The static table is called once per header during encoding, and typical requests have 5-15 headers.

### Name Comparison

HPACK header names are always lowercase (RFC 7541 Section 4). Use `StringComparison.Ordinal` for comparison — the caller is responsible for lowercasing names before lookup.

## Validation Criteria

- [ ] All 61 entries match RFC 7541 Appendix A exactly
- [ ] `Get(0)` throws `ArgumentOutOfRangeException`
- [ ] `Get(62)` throws `ArgumentOutOfRangeException`
- [ ] `FindMatch(":method", "GET")` returns `(2, FullMatch)`
- [ ] `FindMatch(":method", "PUT")` returns `(2, NameMatch)` (name at index 2, but value differs)
- [ ] `FindMatch("x-custom", "foo")` returns `(0, None)`
- [ ] `FindMatch(":status", "200")` returns `(8, FullMatch)`
