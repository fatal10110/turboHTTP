using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace TurboHTTP.Tests
{
    /// <summary>
    /// Fixed-seed byte mutator for the parser fuzz regression tests (#112). The same edits as the
    /// out-of-Unity harness (Tools~/fuzz/Mutator.cs): bit flips, interesting bytes and big-endian
    /// integers, protocol tokens, inserts, deletes, duplications and splices. Deterministic for a
    /// given seed, no coverage feedback; the coverage-guided runs live in Tools~/fuzz.
    /// </summary>
    internal sealed class SeededMutator
    {
        private static readonly byte[] InterestingBytes = { 0x00, 0x01, 0x09, 0x0A, 0x0D, 0x20, 0x22, 0x2C, 0x3A, 0x3B, 0x7E, 0x7F, 0x80, 0xC0, 0xFE, 0xFF };

        private static readonly uint[] InterestingInts = { 0, 1, 0x7F, 0x80, 0xFF, 0x100, 0x3FFF, 0x4000, 0x7FFF, 0xFFFF, 0x10000, 0xFFFFFF, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF };

        private static readonly string[] Tokens =
        {
            "\r\n", "\n", "\r\n\r\n", ": ", " ", "\t", "0\r\n\r\n", ";", "=", "\"", "HTTP/1.1 ", "100 ", "200 ",
            "Content-Length", "Transfer-Encoding", "chunked", "Trailer", "ffffffff",
            "{", "}", "[", "]", ",", "\\u", "\\ud800", "1e999", "-0", "true", "null",
        };

        private ulong _state;

        public SeededMutator(ulong seed)
        {
            _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        }

        public int Next(int maxExclusive)
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            ulong value = _state * 0x2545F4914F6CDD1DUL;
            return maxExclusive <= 1 ? 0 : (int)(value % (ulong)maxExclusive);
        }

        public byte[] Mutate(byte[] input, IList<byte[]> corpus, int maxLength)
        {
            var buf = new List<byte>(input);
            int edits = 1 + Next(8);
            for (int i = 0; i < edits; i++)
                Apply(buf, corpus);
            if (buf.Count > maxLength)
                buf.RemoveRange(maxLength, buf.Count - maxLength);
            return buf.ToArray();
        }

        private void Apply(List<byte> buf, IList<byte[]> corpus)
        {
            int op = buf.Count == 0 ? 3 : Next(11);
            switch (op)
            {
                case 0:
                    buf[Next(buf.Count)] ^= (byte)(1 << Next(8));
                    break;
                case 1:
                    buf[Next(buf.Count)] = (byte)Next(256);
                    break;
                case 2:
                    buf[Next(buf.Count)] = InterestingBytes[Next(InterestingBytes.Length)];
                    break;
                case 3:
                    {
                        int at = Next(buf.Count + 1);
                        int n = 1 + Next(8);
                        for (int k = 0; k < n; k++)
                            buf.Insert(at, (byte)Next(256));
                        break;
                    }
                case 4:
                    {
                        int at = Next(buf.Count);
                        buf.RemoveRange(at, 1 + Next(Math.Min(16, buf.Count - at)));
                        break;
                    }
                case 5:
                    {
                        int at = Next(buf.Count);
                        var copy = buf.GetRange(at, 1 + Next(Math.Min(64, buf.Count - at)));
                        buf.InsertRange(Next(buf.Count + 1), copy);
                        break;
                    }
                case 6:
                    buf.InsertRange(Next(buf.Count + 1), Encoding.ASCII.GetBytes(Tokens[Next(Tokens.Length)]));
                    break;
                case 7:
                    {
                        var token = Encoding.ASCII.GetBytes(Tokens[Next(Tokens.Length)]);
                        int at = Next(buf.Count);
                        for (int k = 0; k < token.Length && at + k < buf.Count; k++)
                            buf[at + k] = token[k];
                        break;
                    }
                case 8:
                    {
                        int at = Next(buf.Count);
                        buf[at] = (byte)(buf[at] + Next(35) - 17);
                        break;
                    }
                case 9:
                    {
                        uint value = InterestingInts[Next(InterestingInts.Length)];
                        int width = 1 + Next(4);
                        int at = Next(buf.Count);
                        for (int k = 0; k < width && at + k < buf.Count; k++)
                            buf[at + k] = (byte)(value >> (8 * (width - 1 - k)));
                        break;
                    }
                default:
                    {
                        var other = corpus[Next(corpus.Count)];
                        int head = Next(buf.Count + 1);
                        buf.RemoveRange(head, buf.Count - head);
                        if (other.Length > 0)
                        {
                            for (int k = Next(other.Length); k < other.Length; k++)
                                buf.Add(other[k]);
                        }

                        break;
                    }
            }
        }

        /// <summary>
        /// Feeds every seed unmutated, then <paramref name="iterations"/> mutations, to
        /// <paramref name="target"/>. The target swallows the failures its parser documents; any
        /// other exception fails the test with the seed, iteration and input bytes to reproduce it.
        /// </summary>
        public static async Task RunAsync(
            string name, ulong seed, int iterations, int maxLength, IList<byte[]> corpus, Func<byte[], Task> target)
        {
            var mutator = new SeededMutator(seed);
            for (int i = -corpus.Count; i < iterations; i++)
            {
                var input = i < 0 ? corpus[corpus.Count + i] : mutator.Mutate(corpus[mutator.Next(corpus.Count)], corpus, maxLength);
                try
                {
                    await target(input);
                }
                catch (Exception ex)
                {
                    Assert.Fail($"{name}: seed {seed}, iteration {i}: unexpected {ex.GetType().FullName}: {ex.Message}\n" +
                        $"input ({input.Length} bytes): {BitConverter.ToString(input).Replace("-", string.Empty)}\n{ex}");
                }
            }
        }

        public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

        public static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
