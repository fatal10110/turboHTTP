using System;
using System.Collections.Generic;
using System.Text;

namespace TurboHTTP.Tools.Fuzz
{
    /// <summary>
    /// The mutation driver's byte mutator: a fixed-seed xorshift64* generator and a libFuzzer-like
    /// set of edits (bit flips, interesting bytes and integers, protocol tokens, inserts, deletes,
    /// duplications, splices with another corpus entry). Deterministic for a given seed and corpus.
    /// It has no coverage feedback: it is the fallback driver, not a replacement for libFuzzer.
    /// Tests/Runtime/Fuzzing/SeededMutator.cs is the same algorithm for the Unity regression tests.
    /// </summary>
    internal sealed class Mutator
    {
        private static readonly byte[] InterestingBytes = { 0x00, 0x01, 0x09, 0x0A, 0x0D, 0x20, 0x22, 0x2C, 0x3A, 0x3B, 0x7E, 0x7F, 0x80, 0xC0, 0xFE, 0xFF };

        private static readonly uint[] InterestingInts = { 0, 1, 0x7F, 0x80, 0xFF, 0x100, 0x3FFF, 0x4000, 0x7FFF, 0xFFFF, 0x10000, 0xFFFFFF, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF };

        private static readonly byte[][] Tokens = BuildTokens(
            "\r\n", "\n", "\r\n\r\n", ": ", " ", "\t", "0\r\n\r\n", ";", "=", "\"", "HTTP/1.1 ", "HTTP/1.0 ", "100 ", "200 ",
            "Content-Length", "Transfer-Encoding", "chunked", "gzip, chunked", "Trailer", "Connection: close", "ffffffff",
            "{", "}", "[", "]", ",", "\\u", "\\ud800", "1e999", "-0", "true", "null", "\"\\\"");

        private ulong _state;

        public Mutator(ulong seed)
        {
            _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        }

        public ulong NextULong()
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            return _state * 0x2545F4914F6CDD1DUL;
        }

        public int Next(int maxExclusive) => maxExclusive <= 1 ? 0 : (int)(NextULong() % (ulong)maxExclusive);

        /// <summary>Returns a mutated copy of <paramref name="input"/>, at most <paramref name="maxLength"/> bytes.</summary>
        public byte[] Mutate(byte[] input, IReadOnlyList<byte[]> corpus, int maxLength)
        {
            var buf = new List<byte>(input);
            int edits = 1 + Next(8);
            for (int i = 0; i < edits; i++)
                Apply(buf, corpus);

            if (buf.Count > maxLength)
                buf.RemoveRange(maxLength, buf.Count - maxLength);
            return buf.ToArray();
        }

        private void Apply(List<byte> buf, IReadOnlyList<byte[]> corpus)
        {
            int op = buf.Count == 0 ? 3 : Next(11);
            switch (op)
            {
                case 0: // flip one bit
                    {
                        int at = Next(buf.Count);
                        buf[at] ^= (byte)(1 << Next(8));
                        break;
                    }
                case 1: // random byte
                    buf[Next(buf.Count)] = (byte)NextULong();
                    break;
                case 2: // interesting byte
                    buf[Next(buf.Count)] = InterestingBytes[Next(InterestingBytes.Length)];
                    break;
                case 3: // insert 1..8 random bytes
                    {
                        int at = Next(buf.Count + 1);
                        int n = 1 + Next(8);
                        for (int k = 0; k < n; k++)
                            buf.Insert(at, (byte)NextULong());
                        break;
                    }
                case 4: // delete a range
                    {
                        int at = Next(buf.Count);
                        int n = 1 + Next(Math.Min(16, buf.Count - at));
                        buf.RemoveRange(at, n);
                        break;
                    }
                case 5: // duplicate a range
                    {
                        int at = Next(buf.Count);
                        int n = 1 + Next(Math.Min(64, buf.Count - at));
                        var copy = buf.GetRange(at, n);
                        buf.InsertRange(Next(buf.Count + 1), copy);
                        break;
                    }
                case 6: // insert a protocol token
                    buf.InsertRange(Next(buf.Count + 1), Tokens[Next(Tokens.Length)]);
                    break;
                case 7: // overwrite with a protocol token
                    {
                        var token = Tokens[Next(Tokens.Length)];
                        int at = Next(buf.Count);
                        for (int k = 0; k < token.Length && at + k < buf.Count; k++)
                            buf[at + k] = token[k];
                        break;
                    }
                case 8: // small arithmetic on a byte
                    {
                        int at = Next(buf.Count);
                        buf[at] = (byte)(buf[at] + Next(35) - 17);
                        break;
                    }
                case 9: // interesting big-endian integer (1, 2, 3 or 4 bytes: lengths, window sizes, ids)
                    {
                        uint value = InterestingInts[Next(InterestingInts.Length)];
                        int width = 1 + Next(4);
                        int at = Next(buf.Count);
                        for (int k = 0; k < width && at + k < buf.Count; k++)
                            buf[at + k] = (byte)(value >> (8 * (width - 1 - k)));
                        break;
                    }
                default: // splice: keep a head of this input, append a tail of another corpus entry
                    {
                        if (corpus.Count == 0)
                            break;
                        var other = corpus[Next(corpus.Count)];
                        int head = Next(buf.Count + 1);
                        buf.RemoveRange(head, buf.Count - head);
                        if (other.Length > 0)
                        {
                            int from = Next(other.Length);
                            for (int k = from; k < other.Length; k++)
                                buf.Add(other[k]);
                        }

                        break;
                    }
            }
        }

        private static byte[][] BuildTokens(params string[] tokens)
        {
            var result = new byte[tokens.Length][];
            for (int i = 0; i < tokens.Length; i++)
                result[i] = Encoding.ASCII.GetBytes(tokens[i]);
            return result;
        }
    }
}
