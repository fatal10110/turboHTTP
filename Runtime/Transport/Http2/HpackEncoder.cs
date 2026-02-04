using System;
using System.Collections.Generic;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// HPACK header encoder. RFC 7541 Sections 6.1, 6.2, 6.3.
    /// NOT thread-safe — accessed only under Http2Connection._writeLock.
    /// </summary>
    internal class HpackEncoder
    {
        private readonly HpackDynamicTable _dynamicTable;
        private bool _pendingSizeUpdate;

        public HpackEncoder(int maxDynamicTableSize = 4096)
        {
            _dynamicTable = new HpackDynamicTable(maxDynamicTableSize);
        }

        /// <summary>
        /// Encode a list of headers into HPACK binary format.
        /// Headers should include pseudo-headers (e.g., :method, :path) first.
        /// </summary>
        public byte[] Encode(IReadOnlyList<(string Name, string Value)> headers)
        {
            var output = new List<byte>();

            if (_pendingSizeUpdate)
            {
                HpackIntegerCodec.Encode(_dynamicTable.MaxSize, 5, 0x20, output);
                _pendingSizeUpdate = false;
            }

            foreach (var (name, value) in headers)
            {
                if (IsSensitiveHeader(name))
                {
                    EncodeLiteralNeverIndexed(name, value, output);
                    continue;
                }

                var (index, match) = _dynamicTable.FindMatch(name, value);

                switch (match)
                {
                    case HpackMatchType.FullMatch:
                        HpackIntegerCodec.Encode(index, 7, 0x80, output);
                        break;

                    case HpackMatchType.NameMatch:
                        HpackIntegerCodec.Encode(index, 6, 0x40, output);
                        EncodeString(value, output);
                        _dynamicTable.Add(name, value);
                        break;

                    case HpackMatchType.None:
                        output.Add(0x40);
                        EncodeString(name, output);
                        EncodeString(value, output);
                        _dynamicTable.Add(name, value);
                        break;
                }
            }

            return output.ToArray();
        }

        /// <summary>
        /// Emit a dynamic table size update instruction at the start of the next header block.
        /// </summary>
        public void SetMaxDynamicTableSize(int newSize)
        {
            _dynamicTable.SetMaxSize(newSize);
            _pendingSizeUpdate = true;
        }

        private void EncodeLiteralNeverIndexed(string name, string value, List<byte> output)
        {
            var (index, match) = HpackStaticTable.FindMatch(name, value);

            if (match != HpackMatchType.None)
            {
                HpackIntegerCodec.Encode(index, 4, 0x10, output);
            }
            else
            {
                output.Add(0x10);
                EncodeString(name, output);
            }
            EncodeString(value, output);
        }

        private void EncodeString(string s, List<byte> output)
        {
            byte[] raw = EncodingHelper.Latin1.GetBytes(s);
            int huffmanLength = HpackHuffman.GetEncodedLength(raw, 0, raw.Length);

            if (huffmanLength < raw.Length)
            {
                byte[] huffmanEncoded = HpackHuffman.Encode(raw, 0, raw.Length);
                HpackIntegerCodec.Encode(huffmanEncoded.Length, 7, 0x80, output);
                output.AddRange(huffmanEncoded);
            }
            else
            {
                HpackIntegerCodec.Encode(raw.Length, 7, 0x00, output);
                output.AddRange(raw);
            }
        }

        private static bool IsSensitiveHeader(string name)
        {
            return string.Equals(name, "authorization", StringComparison.Ordinal)
                || string.Equals(name, "cookie", StringComparison.Ordinal)
                || string.Equals(name, "set-cookie", StringComparison.Ordinal)
                || string.Equals(name, "proxy-authorization", StringComparison.Ordinal);
        }
    }
}
