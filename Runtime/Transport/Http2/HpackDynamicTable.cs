using System;
using System.Collections.Generic;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// HPACK dynamic header table. RFC 7541 Sections 2.3.2, 2.3.3, 4.1–4.4.
    /// FIFO cache bounded by byte size. Index 62 is the newest entry.
    /// NOT thread-safe — each encoder/decoder owns its own instance.
    /// </summary>
    internal class HpackDynamicTable
    {
        private readonly LinkedList<(string Name, string Value)> _entries = new LinkedList<(string, string)>();
        private int _maxSize;
        private int _currentSize;

        public int MaxSize => _maxSize;
        public int CurrentSize => _currentSize;
        public int Count => _entries.Count;

        public HpackDynamicTable(int maxSize = 4096)
        {
            _maxSize = maxSize;
        }

        /// <summary>
        /// RFC 7541 Section 4.1: entry size = octet length of name + octet length of value + 32.
        /// Uses Latin-1 byte count (not string char count) to handle any multi-byte edge cases.
        /// </summary>
        private static int EntrySize(string name, string value)
        {
            return EncodingHelper.Latin1.GetByteCount(name)
                 + EncodingHelper.Latin1.GetByteCount(value)
                 + 32;
        }

        /// <summary>
        /// Add a header to the dynamic table. Evicts oldest entries if needed.
        /// If the entry is larger than maxSize, the table is cleared and the entry is NOT added.
        /// RFC 7541 Section 4.4.
        /// </summary>
        public void Add(string name, string value)
        {
            int entrySize = EntrySize(name, value);

            if (entrySize > _maxSize)
            {
                _entries.Clear();
                _currentSize = 0;
                return;
            }

            while (_currentSize + entrySize > _maxSize && _entries.Count > 0)
            {
                var last = _entries.Last.Value;
                _currentSize -= EntrySize(last.Name, last.Value);
                _entries.RemoveLast();
            }

            _entries.AddFirst((name, value));
            _currentSize += entrySize;
        }

        /// <summary>
        /// Get a header by HPACK index (1-based). Indices 1–61 are static table,
        /// 62+ are dynamic table (62 = newest).
        /// </summary>
        public (string Name, string Value) Get(int index)
        {
            if (index < 1)
                throw new HpackDecodingException("Invalid HPACK index 0");

            if (index <= HpackStaticTable.Length)
                return HpackStaticTable.Get(index);

            int dynamicIndex = index - HpackStaticTable.Length - 1;
            if (dynamicIndex >= _entries.Count)
                throw new HpackDecodingException(
                    $"HPACK index {index} out of range (dynamic table has {_entries.Count} entries)");

            return GetEntryAt(dynamicIndex);
        }

        /// <summary>
        /// Search static + dynamic tables for a matching header.
        /// Returns FullMatch if both name and value match, NameMatch if only name matches.
        /// Prefers static table FullMatch over dynamic table NameMatch.
        /// </summary>
        public (int Index, HpackMatchType Match) FindMatch(string name, string value)
        {
            var (staticIndex, staticMatch) = HpackStaticTable.FindMatch(name, value);
            if (staticMatch == HpackMatchType.FullMatch)
                return (staticIndex, HpackMatchType.FullMatch);

            int nameMatchIndex = staticIndex;
            HpackMatchType bestMatch = staticMatch;

            int i = 0;
            var node = _entries.First;
            while (node != null)
            {
                var entry = node.Value;
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

                i++;
                node = node.Next;
            }

            return (nameMatchIndex, bestMatch);
        }

        /// <summary>
        /// Update the maximum table size. Evicts entries that no longer fit.
        /// Setting to 0 clears the table entirely.
        /// </summary>
        public void SetMaxSize(int newMaxSize)
        {
            _maxSize = newMaxSize;

            while (_currentSize > _maxSize && _entries.Count > 0)
            {
                var last = _entries.Last.Value;
                _currentSize -= EntrySize(last.Name, last.Value);
                _entries.RemoveLast();
            }
        }

        private (string Name, string Value) GetEntryAt(int index)
        {
            if (index < 0 || index >= _entries.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            // Fast path from nearest side to keep indexed lookup near O(n/2) worst-case.
            if (index <= _entries.Count / 2)
            {
                int i = 0;
                var node = _entries.First;
                while (node != null)
                {
                    if (i == index)
                        return node.Value;

                    i++;
                    node = node.Next;
                }
            }
            else
            {
                int i = _entries.Count - 1;
                var node = _entries.Last;
                while (node != null)
                {
                    if (i == index)
                        return node.Value;

                    i--;
                    node = node.Previous;
                }
            }

            throw new HpackDecodingException("Dynamic table index lookup failed unexpectedly");
        }
    }
}
