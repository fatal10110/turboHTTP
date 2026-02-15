using System.Collections.Generic;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// HPACK header decoder. RFC 7541 Sections 6.1, 6.2, 6.3.
    /// NOT thread-safe — accessed only from the single background read loop thread.
    /// </summary>
    internal class HpackDecoder
    {
        private readonly HpackDynamicTable _dynamicTable;
        private int _maxTableSizeFromSettings;
        private bool _expectingSizeUpdate;

        /// <summary>
        /// Maximum total decoded header bytes per header block (names + values).
        /// Protects against decompression bombs where a small HPACK payload decodes
        /// into a massive header set (e.g., via Huffman expansion or indexed references).
        /// 128KB matches the common server limit (e.g., Apache, nginx).
        /// </summary>
        private const int MaxDecodedHeaderBytes = 128 * 1024;

        public HpackDecoder(int maxDynamicTableSize = 4096)
        {
            _dynamicTable = new HpackDynamicTable(maxDynamicTableSize);
            _maxTableSizeFromSettings = maxDynamicTableSize;
        }

        /// <summary>
        /// Decode HPACK binary data into a list of headers.
        /// Returns pseudo-headers mixed with regular headers — the caller
        /// separates :status from the rest.
        /// All bounds checks use headerBlockEnd (offset + length), NOT data.Length.
        /// </summary>
        public List<(string Name, string Value)> Decode(byte[] data, int offset, int length)
        {
            var headers = new List<(string, string)>();
            int end = offset + length;
            bool seenHeaderField = false;
            bool sawSizeUpdate = false;
            long totalDecodedBytes = 0;

            while (offset < end)
            {
                byte b = data[offset];
                int countBefore = headers.Count;

                if ((b & 0x80) != 0)
                {
                    DecodeIndexedHeaderField(data, ref offset, end, headers);
                    seenHeaderField = true;
                }
                else if ((b & 0xC0) == 0x40)
                {
                    DecodeLiteralIncrementalIndexing(data, ref offset, end, headers);
                    seenHeaderField = true;
                }
                else if ((b & 0xF0) == 0x00)
                {
                    DecodeLiteralWithoutIndexing(data, ref offset, end, headers);
                    seenHeaderField = true;
                }
                else if ((b & 0xF0) == 0x10)
                {
                    DecodeLiteralNeverIndexed(data, ref offset, end, headers);
                    seenHeaderField = true;
                }
                else if ((b & 0xE0) == 0x20)
                {
                    // Fix 6: Enforce size-update ordering per RFC 7541 Section 4.2.
                    // Dynamic table size updates MUST occur at the beginning of the
                    // first header block following a change. They MUST NOT appear
                    // after a header field representation has been decoded.
                    if (seenHeaderField)
                        throw new HpackDecodingException(
                            "Dynamic table size update after header field (COMPRESSION_ERROR)");
                    DecodeDynamicTableSizeUpdate(data, ref offset, end);
                    sawSizeUpdate = true;
                }
                else
                {
                    throw new HpackDecodingException($"Invalid HPACK representation byte: 0x{b:X2}");
                }

                // Decompression bomb protection: track total decoded header bytes
                if (headers.Count > countBefore)
                {
                    var last = headers[headers.Count - 1];
                    totalDecodedBytes += last.Name.Length + last.Value.Length;
                    if (totalDecodedBytes > MaxDecodedHeaderBytes)
                        throw new HpackDecodingException(
                            $"Decoded header block exceeds {MaxDecodedHeaderBytes} bytes (decompression bomb protection)");
                }
            }

            // RFC 7541 Section 4.2: If the decoder receives a change to the maximum
            // dynamic table size via SETTINGS, the peer MUST emit a size update as
            // the first instruction in the next header block. If it doesn't, the
            // header block is invalid (COMPRESSION_ERROR).
            if (_expectingSizeUpdate && !sawSizeUpdate)
                throw new HpackDecodingException(
                    "Expected dynamic table size update after SETTINGS change (COMPRESSION_ERROR)");
            if (sawSizeUpdate)
                _expectingSizeUpdate = false;

            return headers;
        }

        /// <summary>
        /// Update the decoder's max dynamic table size limit.
        /// Called when we send SETTINGS_HEADER_TABLE_SIZE to the server.
        /// </summary>
        public void SetMaxDynamicTableSize(int newSize)
        {
            _maxTableSizeFromSettings = newSize;
            _expectingSizeUpdate = true;
        }

        private void DecodeIndexedHeaderField(byte[] data, ref int offset, int headerBlockEnd,
            List<(string, string)> headers)
        {
            int index = HpackIntegerCodec.Decode(data, ref offset, 7, headerBlockEnd);
            if (index == 0)
                throw new HpackDecodingException("HPACK index 0 is invalid (COMPRESSION_ERROR)");

            var (name, value) = _dynamicTable.Get(index);
            headers.Add((name, value));
        }

        private void DecodeLiteralIncrementalIndexing(byte[] data, ref int offset, int headerBlockEnd,
            List<(string, string)> headers)
        {
            int nameIndex = HpackIntegerCodec.Decode(data, ref offset, 6, headerBlockEnd);
            string name;

            if (nameIndex > 0)
            {
                name = _dynamicTable.Get(nameIndex).Name;
            }
            else
            {
                name = DecodeString(data, ref offset, headerBlockEnd);
            }

            string value = DecodeString(data, ref offset, headerBlockEnd);

            _dynamicTable.Add(name, value);
            headers.Add((name, value));
        }

        private void DecodeLiteralWithoutIndexing(byte[] data, ref int offset, int headerBlockEnd,
            List<(string, string)> headers)
        {
            int nameIndex = HpackIntegerCodec.Decode(data, ref offset, 4, headerBlockEnd);
            string name;

            if (nameIndex > 0)
            {
                name = _dynamicTable.Get(nameIndex).Name;
            }
            else
            {
                name = DecodeString(data, ref offset, headerBlockEnd);
            }

            string value = DecodeString(data, ref offset, headerBlockEnd);
            headers.Add((name, value));
        }

        private void DecodeLiteralNeverIndexed(byte[] data, ref int offset, int headerBlockEnd,
            List<(string, string)> headers)
        {
            int nameIndex = HpackIntegerCodec.Decode(data, ref offset, 4, headerBlockEnd);
            string name;

            if (nameIndex > 0)
            {
                name = _dynamicTable.Get(nameIndex).Name;
            }
            else
            {
                name = DecodeString(data, ref offset, headerBlockEnd);
            }

            string value = DecodeString(data, ref offset, headerBlockEnd);
            headers.Add((name, value));
        }

        private void DecodeDynamicTableSizeUpdate(byte[] data, ref int offset, int headerBlockEnd)
        {
            int newSize = HpackIntegerCodec.Decode(data, ref offset, 5, headerBlockEnd);

            if (newSize > _maxTableSizeFromSettings)
                throw new HpackDecodingException(
                    $"Dynamic table size update {newSize} exceeds SETTINGS limit {_maxTableSizeFromSettings}");

            _dynamicTable.SetMaxSize(newSize);
            _expectingSizeUpdate = false;
        }

        private string DecodeString(byte[] data, ref int offset, int headerBlockEnd)
        {
            if (offset >= headerBlockEnd)
                throw new HpackDecodingException("Unexpected end of header block in string");

            byte firstByte = data[offset];
            bool isHuffman = (firstByte & 0x80) != 0;
            int stringLength = HpackIntegerCodec.Decode(data, ref offset, 7, headerBlockEnd);

            if (stringLength == 0)
                return "";

            if (offset + stringLength > headerBlockEnd)
                throw new HpackDecodingException("String length exceeds header block boundary");

            string result;
            if (isHuffman)
            {
                byte[] decoded = HpackHuffman.Decode(data, offset, stringLength);
                result = EncodingHelper.Latin1.GetString(decoded);
            }
            else
            {
                result = EncodingHelper.Latin1.GetString(data, offset, stringLength);
            }

            offset += stringLength;
            return result;
        }
    }
}
