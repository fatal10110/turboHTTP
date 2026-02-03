# Step 3B.6: HPACK Dynamic Table

**File:** `Runtime/Transport/Http2/HpackDynamicTable.cs`
**Depends on:** Step 3B.3 (HpackStaticTable)
**Spec:** RFC 7541 Sections 2.3.2, 2.3.3, 4.1, 4.2, 4.3, 4.4

## Purpose

Implement the HPACK dynamic table — a FIFO header cache bounded by byte size. Each HTTP/2 connection has two dynamic tables: one for encoding (maintained by the encoder) and one for decoding (maintained by the decoder). The tables are independent and never shared.

## Class Design

```csharp
namespace TurboHTTP.Transport.Http2
{
    internal class HpackDynamicTable
    {
        public HpackDynamicTable(int maxSize = 4096);

        public int MaxSize { get; }
        public int CurrentSize { get; }
        public int Count { get; }

        public void Add(string name, string value);
        public (string Name, string Value) Get(int index);
        public (int Index, HpackMatchType Match) FindMatch(string name, string value);
        public void SetMaxSize(int newMaxSize);
    }
}
```

## HPACK Indexing Scheme

The static table occupies indices 1–61. The dynamic table starts at index 62. The most recently added entry has index 62, the second most recent has index 63, etc.

```
Index 1–61:  Static table (fixed, shared)
Index 62:    Dynamic table entry 0 (newest)
Index 63:    Dynamic table entry 1
...
Index 62+N:  Dynamic table entry N (oldest)
```

## Implementation Details

### Storage

```csharp
private readonly List<(string Name, string Value)> _entries = new List<(string, string)>();
private int _maxSize;
private int _currentSize;
```

`_entries[0]` is the newest entry (most recently added). This maps to HPACK index 62.

### Entry Size (RFC 7541 Section 4.1)

Each entry's size is:
```
entrySize = name.Length + value.Length + 32
```

The 32-byte overhead accounts for the estimated memory overhead of the entry structure (per RFC 7541 Section 4.1). This is used for both capacity tracking and eviction decisions.

### `Add(string name, string value)`

RFC 7541 Section 4.4:

```csharp
public void Add(string name, string value)
{
    int entrySize = name.Length + value.Length + 32;

    // If the new entry is larger than the max table size,
    // clear the table entirely (entry is NOT added).
    if (entrySize > _maxSize)
    {
        _entries.Clear();
        _currentSize = 0;
        return;
    }

    // Evict oldest entries until there's room
    while (_currentSize + entrySize > _maxSize && _entries.Count > 0)
    {
        var last = _entries[_entries.Count - 1];
        _currentSize -= (last.Name.Length + last.Value.Length + 32);
        _entries.RemoveAt(_entries.Count - 1);
    }

    // Insert at position 0 (newest)
    _entries.Insert(0, (name, value));
    _currentSize += entrySize;
}
```

### `Get(int index)`

Combined lookup across static and dynamic tables:

```csharp
public (string Name, string Value) Get(int index)
{
    if (index < 1)
        throw new HpackDecodingException("Invalid HPACK index 0");

    if (index <= HpackStaticTable.Length)
        return HpackStaticTable.Get(index);

    int dynamicIndex = index - HpackStaticTable.Length - 1;
    if (dynamicIndex >= _entries.Count)
        throw new HpackDecodingException($"HPACK index {index} out of range (dynamic table has {_entries.Count} entries)");

    return _entries[dynamicIndex];
}
```

### `FindMatch(string name, string value)`

Search both static and dynamic tables for the best match:

```csharp
public (int Index, HpackMatchType Match) FindMatch(string name, string value)
{
    // Check static table first
    var (staticIndex, staticMatch) = HpackStaticTable.FindMatch(name, value);
    if (staticMatch == HpackMatchType.FullMatch)
        return (staticIndex, HpackMatchType.FullMatch);

    // Check dynamic table
    int nameMatchIndex = staticIndex; // May be 0 (no static name match)
    HpackMatchType bestMatch = staticMatch;

    for (int i = 0; i < _entries.Count; i++)
    {
        var entry = _entries[i];
        if (string.Equals(entry.Name, name, StringComparison.Ordinal))
        {
            if (string.Equals(entry.Value, value, StringComparison.Ordinal))
                return (i + HpackStaticTable.Length + 1, HpackMatchType.FullMatch);

            if (bestMatch == HpackMatchType.None)
            {
                nameMatchIndex = i + HpackStaticTable.Length + 1;
                bestMatch = HpackMatchType.NameMatch;
            }
        }
    }

    return (nameMatchIndex, bestMatch);
}
```

### `SetMaxSize(int newMaxSize)`

Called when the server sends `SETTINGS_HEADER_TABLE_SIZE`:

```csharp
public void SetMaxSize(int newMaxSize)
{
    _maxSize = newMaxSize;

    // Evict entries that no longer fit
    while (_currentSize > _maxSize && _entries.Count > 0)
    {
        var last = _entries[_entries.Count - 1];
        _currentSize -= (last.Name.Length + last.Value.Length + 32);
        _entries.RemoveAt(_entries.Count - 1);
    }
}
```

Setting max size to 0 clears the table entirely — this is a valid server behavior to prevent dynamic table usage.

## Thread Safety

NOT thread-safe. Each encoder and decoder owns its own `HpackDynamicTable` instance:
- **Encoder's table:** Accessed only during `SendRequestAsync` under the write lock.
- **Decoder's table:** Accessed only from the single read loop thread.

No cross-thread access occurs.

## Edge Cases

- **Empty table (maxSize = 0):** Valid. No entries can be added. All lookups fall through to static table.
- **Entry larger than maxSize:** Table is emptied, entry is NOT added (RFC 7541 Section 4.4).
- **Rapid eviction:** Adding a large entry may evict multiple smaller entries.
- **Size update to 0 then back:** Server may send two SETTINGS_HEADER_TABLE_SIZE updates (0 then N) to force a table flush. This is valid behavior.

## Validation Criteria

- [ ] Add entries and verify they appear at index 62
- [ ] Second entry is at index 63, first stays at 62
- [ ] FIFO eviction when `_currentSize + newEntry > _maxSize`: oldest entries removed first
- [ ] Entry size = `name.Length + value.Length + 32`
- [ ] Entry exceeding `_maxSize` clears table entirely and is NOT added
- [ ] `SetMaxSize(0)` clears all entries
- [ ] `Get(0)` throws
- [ ] `Get(62)` returns the most recently added entry
- [ ] `FindMatch` prefers static table FullMatch over dynamic table NameMatch
- [ ] `FindMatch` returns dynamic table FullMatch when available
- [ ] Combined static+dynamic indexing is correct across boundary (index 61 → 62)
