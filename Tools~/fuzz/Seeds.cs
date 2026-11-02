using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tools.Fuzz
{
    /// <summary>
    /// The seed corpus, built from the inputs the parser tests already use (Tests/Runtime/Transport,
    /// Tests/Runtime/WebSocket, Tests/Runtime/JSON) and the RFC examples (RFC 7541 Appendix C,
    /// RFC 7692 Section 7.2.3). <c>TurboHTTP.Fuzz seeds &lt;dir&gt;</c> writes it to
    /// Tools~/fuzz/corpus; the committed files are that output plus any regression inputs.
    /// </summary>
    internal static class Seeds
    {
        public static IReadOnlyDictionary<string, List<(string Name, byte[] Data)>> Build()
        {
            return new SortedDictionary<string, List<(string, byte[])>>(StringComparer.Ordinal)
            {
                ["h1-response"] = H1Response(),
                ["h1-chunked-body"] = H1ChunkedBody(),
                ["hpack"] = Hpack(),
                ["h2-frames"] = H2Frames(),
                ["h2-connection"] = H2Connection(),
                ["ws-frames"] = WsFrames(),
                ["ws-inflate"] = WsInflate(),
                ["json"] = Json(),
            };
        }

        private static List<(string, byte[])> H1Response() => Ascii(
            ("content-length", "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nContent-Type: text/plain\r\n\r\nhello"),
            ("chunked-trailers", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nTrailer: X-Trailer\r\n\r\n5\r\nhello\r\n6;ext=1\r\n world\r\n0\r\nX-Trailer: t\r\n\r\n"),
            ("100-continue", "HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"),
            ("103-then-404", "HTTP/1.1 103 Early Hints\r\nLink: </a.css>; rel=preload\r\n\r\nHTTP/1.1 404 Not Found\r\nSet-Cookie: a=1\r\nSet-Cookie: b=2\r\nContent-Length: 4\r\n\r\nnope"),
            ("obs-fold", "HTTP/1.1 200 OK\r\nX-Folded: a\r\n b\r\nContent-Length: 0\r\n\r\n"),
            ("bare-lf", "HTTP/1.1 200 OK\nContent-Length: 3\n\nabc"),
            ("204-with-length", "HTTP/1.1 204 No Content\r\nContent-Length: 10\r\n\r\n"),
            ("304", "HTTP/1.1 304 Not Modified\r\nETag: \"x\"\r\nContent-Length: 7\r\n\r\n"),
            ("read-to-end", "HTTP/1.0 200 OK\r\nConnection: close\r\n\r\nread to end body"),
            ("te-gzip-chunked", "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n0\r\n\r\n"),
            ("duplicate-content-length", "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nContent-Length: 5\r\n\r\nhello"),
            ("te-and-content-length", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 3\r\n\r\n3\r\nabc\r\n0\r\n\r\n"),
            ("upper-hex-chunk", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nA\r\n0123456789\r\n0\r\n\r\n"),
            ("101", "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n"),
            ("empty-reason", "HTTP/1.1 500 \r\nContent-Length: 1\r\n\r\nx"),
            ("keep-alive-http10", "HTTP/1.0 200 OK\r\nConnection: keep-alive\r\nContent-Length: 2\r\n\r\nhi"));

        private static List<(string, byte[])> H1ChunkedBody()
        {
            // First byte: the caller's read size is (byte % 64) + 1.
            var seeds = new List<(string, byte[])>();
            void Add(string name, byte readSize, string body) =>
                seeds.Add((name, Concat(new[] { readSize }, Encoding.ASCII.GetBytes(body))));

            Add("simple", 4, "5\r\nhello\r\n0\r\n\r\n");
            Add("extension-trailers", 0, "1;name=value\r\na\r\n0\r\nTrailer-A: 1\r\nTrailer-B: two\r\n\r\n");
            Add("two-chunks", 63, "a\r\n0123456789\r\nA\r\n0123456789\r\n0\r\n\r\n");
            Add("empty", 7, "0\r\n\r\n");
            Add("bare-lf", 16, "5\n12345\n0\n\n");
            Add("leading-zeros", 2, "000005\r\nhello\r\n0\r\n\r\n");
            Add("quoted-extension", 9, "3;a=\"x;y\"\r\nabc\r\n0;last\r\n\r\n");
            return seeds;
        }

        private static List<(string, byte[])> Hpack()
        {
            var encoder = new HpackEncoder();
            var block1 = Encode(encoder, (":status", "200"), ("content-type", "text/html"), ("cache-control", "private"));
            var block2 = Encode(encoder, (":status", "200"), ("content-type", "text/html"), ("cache-control", "private"));
            var block3 = Encode(encoder, (":status", "302"), ("location", "https://www.example.com"), ("set-cookie", "a=b; Path=/"));

            return new List<(string, byte[])>
            {
                ("encoder-sequence", Blocks(block1, block2, block3)),
                // RFC 7541 C.3: requests without Huffman coding, one dynamic table.
                ("rfc7541-c3", Blocks(
                    Hex("828684410f7777772e6578616d706c652e636f6d"),
                    Hex("828684be58086e6f2d6361636865"),
                    Hex("828785bf400a637573746f6d2d6b65790c637573746f6d2d76616c7565"))),
                // RFC 7541 C.4: the same requests with Huffman coding.
                ("rfc7541-c4", Blocks(
                    Hex("828684418cf1e3c2e5f23a6ba0ab90f4ff"),
                    Hex("828684be5886a8eb10649cbf"),
                    Hex("828785bf408825a849e95ba97d7f8925a849e95bb8e8b4bf"))),
                // Dynamic table size updates (RFC 7541 Section 6.3), then a lookup.
                ("table-size-update", Blocks(Hex("3fe11f82"), Hex("20400161016282"))),
                ("indexed-only", Blocks(Hex("82868488"))),
            };
        }

        private static List<(string, byte[])> H2Frames()
        {
            var settings = Frame(0x4, 0, 0, Concat(
                Setting(1, 4096), Setting(2, 0), Setting(3, 100), Setting(4, 65535), Setting(5, 16384), Setting(6, 8192)));
            var headersBlock = Encode(new HpackEncoder(), (":status", "200"));
            return new List<(string, byte[])>
            {
                ("settings", settings),
                ("control-mix", Concat(
                    settings,
                    Frame(0x4, 0x1, 0, Array.Empty<byte>()),
                    Frame(0x6, 0, 0, Hex("0102030405060708")),
                    Frame(0x8, 0, 0, Hex("000003e8")),
                    Frame(0x8, 0, 1, Hex("00000010")),
                    Frame(0x3, 0, 1, Hex("00000008")),
                    Frame(0x2, 0, 3, Hex("0000000110")),
                    Frame(0x7, 0, 0, Concat(Hex("0000000000000000"), Encoding.ASCII.GetBytes("bye"))))),
                ("headers-padded-priority", Frame(0x1, 0x4 | 0x8 | 0x20, 1,
                    Concat(new byte[] { 2 }, Hex("0000000010"), headersBlock, new byte[2]))),
                ("data-padded", Frame(0x0, 0x1 | 0x8, 1, Concat(new byte[] { 3 }, Encoding.ASCII.GetBytes("hi"), new byte[3]))),
                ("headers-continuation", Concat(
                    Frame(0x1, 0, 1, Slice(headersBlock, 0, 1)),
                    Frame(0x9, 0x4, 1, Slice(headersBlock, 1, headersBlock.Length - 1)))),
                ("unknown-type", Frame(0x0a, 0xff, 5, Hex("deadbeef"))),
            };
        }

        private static List<(string, byte[])> H2Connection()
        {
            // The client already sent its preface, SETTINGS and a GET on stream 1 (H2FuzzStream); each
            // seed is what the server sends next. One encoder per seed: the HPACK table is per connection.
            var seeds = new List<(string, byte[])>();
            const byte EndStream = 0x1, EndHeaders = 0x4, Padded = 0x8, Priority = 0x20;

            {
                var e = new HpackEncoder();
                seeds.Add(("headers-end-stream", Frame(0x1, EndHeaders | EndStream, 1, Encode(e, (":status", "204")))));
            }
            {
                var e = new HpackEncoder();
                seeds.Add(("headers-data", Concat(
                    Frame(0x1, EndHeaders, 1, Encode(e, (":status", "200"), ("content-length", "5"))),
                    Frame(0x0, EndStream, 1, Encoding.ASCII.GetBytes("hello")))));
            }
            {
                var e = new HpackEncoder();
                seeds.Add(("padded-priority-headers-padded-data", Concat(
                    Frame(0x1, EndHeaders | Padded | Priority, 1,
                        Concat(new byte[] { 3 }, Hex("0000000010"), Encode(e, (":status", "200")), new byte[3])),
                    Frame(0x0, EndStream | Padded, 1, Concat(new byte[] { 2 }, Encoding.ASCII.GetBytes("hi"), new byte[2])))));
            }
            {
                var e = new HpackEncoder();
                var block = Encode(e, (":status", "200"), ("content-type", "application/json"), ("x-long", new string('v', 40)));
                seeds.Add(("headers-continuation", Concat(
                    Frame(0x1, Padded, 1, Concat(new byte[] { 1 }, Slice(block, 0, 3), new byte[1])),
                    Frame(0x9, 0, 1, Slice(block, 3, 10)),
                    Frame(0x9, EndHeaders, 1, Slice(block, 13, block.Length - 13)),
                    Frame(0x0, EndStream, 1, Encoding.ASCII.GetBytes("{}")))));
            }
            {
                var e = new HpackEncoder();
                seeds.Add(("trailers", Concat(
                    Frame(0x1, EndHeaders, 1, Encode(e, (":status", "200"))),
                    Frame(0x0, 0, 1, Encoding.ASCII.GetBytes("body")),
                    Frame(0x1, EndHeaders | EndStream, 1, Encode(e, ("grpc-status", "0"))))));
            }
            {
                var e = new HpackEncoder();
                seeds.Add(("informational-then-final", Concat(
                    Frame(0x1, EndHeaders, 1, Encode(e, (":status", "103"), ("link", "</a.css>; rel=preload"))),
                    Frame(0x1, EndHeaders | EndStream, 1, Encode(e, (":status", "200"))))));
            }
            {
                var e = new HpackEncoder();
                seeds.Add(("control-frames-then-response", Concat(
                    Frame(0x4, 0, 0, Concat(Setting(1, 0), Setting(4, 1), Setting(5, 32768))),
                    Frame(0x6, 0, 0, Hex("0102030405060708")),
                    Frame(0x8, 0, 0, Hex("00010000")),
                    Frame(0x8, 0, 1, Hex("00000100")),
                    Frame(0x2, 0, 1, Hex("0000000020")),
                    Frame(0x1, EndHeaders | EndStream, 1, Encode(e, (":status", "200"))))));
            }
            seeds.Add(("rst-stream", Frame(0x3, 0, 1, Hex("00000008"))));
            {
                var e = new HpackEncoder();
                seeds.Add(("goaway-then-response", Concat(
                    Frame(0x7, 0, 0, Hex("0000000100000000")),
                    Frame(0x1, EndHeaders | EndStream, 1, Encode(e, (":status", "200"))))));
            }
            {
                var e = new HpackEncoder();
                seeds.Add(("push-promise", Frame(0x5, EndHeaders, 1,
                    Concat(Hex("00000002"), Encode(e, (":method", "GET"), (":path", "/p"))))));
            }
            seeds.Add(("ping-ack-window-update", Concat(
                Frame(0x6, 0x1, 0, Hex("0000000000000000")),
                Frame(0x8, 0, 0, Hex("7fffffff")))));
            return seeds;
        }

        private static List<(string, byte[])> WsFrames() => new List<(string, byte[])>
        {
            ("text", Concat(Hex("8105"), Encoding.ASCII.GetBytes("hello"))),
            ("fragmented", Concat(Hex("0103"), Encoding.ASCII.GetBytes("hel"), Hex("8002"), Encoding.ASCII.GetBytes("lo"))),
            ("ping-between-fragments", Concat(Hex("0102"), Encoding.ASCII.GetBytes("he"), Hex("8900"), Hex("8003"), Encoding.ASCII.GetBytes("llo"))),
            ("binary-16bit-length", Concat(Hex("827e0080"), new byte[128])),
            ("binary-64bit-length", Concat(Hex("827f0000000000000004"), Encoding.ASCII.GetBytes("abcd"))),
            ("close-1000", Hex("880203e8")),
            ("close-reason", Concat(Hex("880603e9"), Encoding.ASCII.GetBytes("bye!"))),
            ("pong", Hex("8a00")),
            ("ping-payload", Concat(Hex("8905"), Encoding.ASCII.GetBytes("hello"))),
            // RFC 7692 Section 7.2.3.1: "Hello" compressed, RSV1 set.
            ("compressed", Hex("c107f248cdc9c90700")),
            // RFC 7692 Section 7.2.3.1: the same message fragmented; RSV1 only on the first frame.
            ("compressed-fragmented", Hex("4103f248cd8004c9c90700")),
            ("utf8-text", Concat(Hex("8106"), Encoding.UTF8.GetBytes("hé€"))),
        };

        private static List<(string, byte[])> WsInflate()
        {
            var seeds = new List<(string, byte[])>
            {
                // RFC 7692 Section 7.2.3.1 and 7.2.3.3: "Hello" compressed, and as a stored block.
                ("rfc7692-hello", Blocks(Hex("f248cdc9c90700"))),
                ("rfc7692-stored", Blocks(Hex("000500faff48656c6c6f00"))),
                ("empty", Blocks(Hex("00"))),
            };

            // Messages compressed independently: the client negotiates server_no_context_takeover.
            var messages = new List<byte[]>();
            foreach (var text in new[] { "Hello", "Hello, Hello, Hello world", new string('a', 300), "{\"k\":[1,2,3]}" })
            {
                using var output = new MemoryStream();
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    deflate.Write(bytes, 0, bytes.Length);
                    deflate.Flush();
                }

                var message = output.ToArray();
                int end = message.Length;
                for (int i = 0; i + 4 <= message.Length; i++)
                {
                    // Cut at the first sync-flush marker (RFC 7692 Section 7.2.1 strips 00 00 ff ff).
                    if (message[i] == 0 && message[i + 1] == 0 && message[i + 2] == 0xff && message[i + 3] == 0xff)
                    {
                        end = i;
                        break;
                    }
                }

                messages.Add(Slice(message, 0, end));
            }

            seeds.Add(("independent-messages", Blocks(messages.ToArray())));

            return seeds;
        }

        private static List<(string, byte[])> Json()
        {
            var deep = new StringBuilder();
            for (int i = 0; i < 63; i++)
                deep.Append('[');
            for (int i = 0; i < 63; i++)
                deep.Append(']');

            var seeds = Ascii(
                ("object", "{\"a\":1,\"b\":[true,false,null],\"c\":\"x\\u00e9\\n\",\"d\":-1.5e3}"),
                ("empty-array", "[]"),
                ("empty-object", "{ }"),
                ("string", "\"str\\\"\\\\\\/\\b\\f\\n\\r\\t\""),
                ("numbers", "[0,-0,1E+2,0.1,9007199254740993,-1.0e-10,2147483648]"),
                ("big-integer", "123456789012345678901234567890"),
                ("nested", "{\"nested\":{\"deep\":[1,{\"x\":\"y\"}]}}"),
                ("surrogate-pair", "\"\\ud83d\\ude00\""),
                ("deep-63", deep.ToString()),
                ("whitespace", " \t\r\n{ \"k\" : [ 1 , 2 ] } \n"));
            seeds.Add(("utf8", Encoding.UTF8.GetBytes("{\"name\":\"Jörg € 😀\"}")));
            return seeds;
        }

        // --- helpers ---

        private static byte[] Encode(HpackEncoder encoder, params (string Name, string Value)[] headers) =>
            encoder.Encode(headers).ToArray();

        /// <summary>Length-prefixed blocks: [u16 BE length][bytes] each (hpack, ws-inflate inputs).</summary>
        private static byte[] Blocks(params byte[][] blocks)
        {
            var output = new MemoryStream();
            foreach (var block in blocks)
            {
                output.WriteByte((byte)(block.Length >> 8));
                output.WriteByte((byte)block.Length);
                output.Write(block, 0, block.Length);
            }

            return output.ToArray();
        }

        /// <summary>An HTTP/2 frame: 24-bit length, type, flags, 31-bit stream id, payload (RFC 9113 Section 4.1).</summary>
        private static byte[] Frame(byte type, int flags, int streamId, byte[] payload)
        {
            var frame = new byte[9 + payload.Length];
            frame[0] = (byte)(payload.Length >> 16);
            frame[1] = (byte)(payload.Length >> 8);
            frame[2] = (byte)payload.Length;
            frame[3] = type;
            frame[4] = (byte)flags;
            frame[5] = (byte)((streamId >> 24) & 0x7f);
            frame[6] = (byte)(streamId >> 16);
            frame[7] = (byte)(streamId >> 8);
            frame[8] = (byte)streamId;
            Buffer.BlockCopy(payload, 0, frame, 9, payload.Length);
            return frame;
        }

        private static byte[] Setting(ushort id, uint value) => new[]
        {
            (byte)(id >> 8), (byte)id, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value,
        };

        private static List<(string, byte[])> Ascii(params (string Name, string Text)[] items)
        {
            var list = new List<(string, byte[])>();
            foreach (var (name, text) in items)
                list.Add((name, Encoding.ASCII.GetBytes(text)));
            return list;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var output = new MemoryStream();
            foreach (var part in parts)
                output.Write(part, 0, part.Length);
            return output.ToArray();
        }

        private static byte[] Slice(byte[] data, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(data, offset, result, 0, count);
            return result;
        }

        private static byte[] Hex(string hex) => Convert.FromHexString(hex);
    }
}
