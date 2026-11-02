using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Http2;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// Fixed-seed mutation tests for the HTTP/1.1 response parser and chunked body source (RFC 9112),
    /// the HPACK decoder (RFC 7541) and the HTTP/2 frame codec with SETTINGS validation (RFC 9113)
    /// (#112). Each parser may fail only with the typed errors it documents; any other exception is
    /// a defect. The inputs are a subset of the Tools~/fuzz corpus, which the coverage-guided runs use.
    /// </summary>
    [TestFixture]
    public class ParserFuzzRegressionTests
    {
        private const int Iterations = 3000;
        private const int MaxLength = 2048;

        private static readonly byte[][] Http11Responses =
        {
            SeededMutator.Ascii("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nContent-Type: text/plain\r\n\r\nhello"),
            SeededMutator.Ascii("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nTrailer: X-Trailer\r\n\r\n5\r\nhello\r\n6;ext=1\r\n world\r\n0\r\nX-Trailer: t\r\n\r\n"),
            SeededMutator.Ascii("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"),
            SeededMutator.Ascii("HTTP/1.1 200 OK\r\nX-Folded: a\r\n b\r\nContent-Length: 0\r\n\r\n"),
            SeededMutator.Ascii("HTTP/1.1 200 OK\nContent-Length: 3\n\nabc"),
            SeededMutator.Ascii("HTTP/1.1 204 No Content\r\nContent-Length: 10\r\n\r\n"),
            SeededMutator.Ascii("HTTP/1.0 200 OK\r\nConnection: close\r\n\r\nread to end body"),
            SeededMutator.Ascii("HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n0\r\n\r\n"),
            SeededMutator.Ascii("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 3\r\n\r\n3\r\nabc\r\n0\r\n\r\n"),
        };

        // First byte: the caller's read size, (byte % 64) + 1.
        private static readonly byte[][] ChunkedBodies =
        {
            SeededMutator.Ascii("\u00045\r\nhello\r\n0\r\n\r\n"),
            SeededMutator.Ascii("\u00001;name=value\r\na\r\n0\r\nTrailer-A: 1\r\nTrailer-B: two\r\n\r\n"),
            SeededMutator.Ascii("?a\r\n0123456789\r\nA\r\n0123456789\r\n0\r\n\r\n"),
            SeededMutator.Ascii("\u0002000005\r\nhello\r\n0\r\n\r\n"),
            SeededMutator.Ascii("\u00093;a=\"x;y\"\r\nabc\r\n0;last\r\n\r\n"),
        };

        // [u16 BE length][header block] repeated, one decoder: RFC 7541 C.3 and C.4 and size updates.
        private static readonly byte[][] HpackSequences =
        {
            SeededMutator.Hex("0014828684410f7777772e6578616d706c652e636f6d000e828684be58086e6f2d6361636865001d828785bf400a637573746f6d2d6b65790c637573746f6d2d76616c7565"),
            SeededMutator.Hex("0011828684418cf1e3c2e5f23a6ba0ab90f4ff000c828684be5886a8eb10649cbf0018828785bf408825a849e95ba97d7f8925a849e95bb8e8b4bf"),
            SeededMutator.Hex("00043fe11f82000720400161016282"),
        };

        // RFC 9113 Section 4.1 frames: SETTINGS, control frames, padded and prioritized HEADERS,
        // padded DATA, HEADERS + CONTINUATION.
        private static readonly byte[][] Http2FrameStreams =
        {
            Frame(0x4, 0, 0, "000100001000" + "000200000000" + "000300000064" + "00040000ffff" + "000500004000" + "000600002000"),
            Concat(Frame(0x4, 0x1, 0, ""), Frame(0x6, 0, 0, "0102030405060708"), Frame(0x8, 0, 0, "000003e8"),
                Frame(0x3, 0, 1, "00000008"), Frame(0x7, 0, 0, "0000000000000000")),
            Frame(0x1, 0x4 | 0x8 | 0x20, 1, "02" + "0000000010" + "88" + "0000"),
            Frame(0x0, 0x1 | 0x8, 1, "03" + "6869" + "000000"),
            Concat(Frame(0x1, 0, 1, "88"), Frame(0x9, 0x4, 1, "")),
        };

        private static byte[] Frame(byte type, int flags, int streamId, string payloadHex)
        {
            var payload = SeededMutator.Hex(payloadHex);
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

        private static byte[] Concat(params byte[][] parts)
        {
            var output = new MemoryStream();
            foreach (var part in parts)
                output.Write(part, 0, part.Length);
            return output.ToArray();
        }

        [Test]
        public void Http11ResponseParser_MutatedResponses_FailOnlyWithTypedErrors()
        {
            AssertAsync.Run(() => SeededMutator.RunAsync("h1-response", 112, Iterations, MaxLength, Http11Responses, async data =>
            {
                using var stream = new MemoryStream(data, writable: false);
                try
                {
                    var parsed = await Http11ResponseParser.ParseAsync(stream, HttpMethod.GET, CancellationToken.None);
                    try
                    {
                        Assert.IsNotNull(parsed.Headers);
                        if (parsed.SegmentedBody == null)
                            Assert.LessOrEqual(parsed.Body.Length, data.Length, "body longer than the input");
                    }
                    finally
                    {
                        parsed.ReleaseBodyBuffers();
                    }
                }
                catch (Exception ex) when (IsHttp11Failure(ex))
                {
                }
            }));
        }

        [Test]
        public void Http11ResponseBodySource_MutatedChunkedBodies_FailOnlyWithTypedErrors()
        {
            AssertAsync.Run(() => SeededMutator.RunAsync("h1-chunked-body", 112, Iterations, MaxLength, ChunkedBodies, async data =>
            {
                if (data.Length == 0)
                    return;

                int readSize = (data[0] % 64) + 1;
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var stream = new MemoryStream(data, 1, data.Length - 1, writable: false);
                using var head = new ParsedResponseHead(new Http11ResponseParser.BufferedStreamReader(stream))
                {
                    StatusCode = HttpStatusCode.OK,
                    Headers = HttpHeaders.Empty,
                    KeepAlive = false,
                    BodyKind = Http11ResponseBodyKind.Chunked
                };
                var lease = new ConnectionLease(
                    null,
                    new SemaphoreSlim(0, 1),
                    new PooledConnection(socket, stream, "example.test", 80, false));

                await using var source = new Http11ResponseBodySource(head, lease, CancellationToken.None, TimeSpan.FromSeconds(30));
                try
                {
                    var buffer = new byte[readSize];
                    long total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, CancellationToken.None)) > 0)
                    {
                        total += read;
                        Assert.LessOrEqual(total, data.Length, "decoded more body bytes than the input holds");
                    }

                    Assert.IsNotNull(await source.GetTrailersAsync(CancellationToken.None));
                }
                catch (Exception ex) when (IsHttp11Failure(ex))
                {
                }
            }));
        }

        [Test]
        public void HpackDecoder_MutatedBlockSequences_FailOnlyWithCompressionErrors()
        {
            AssertAsync.Run(() => SeededMutator.RunAsync("hpack", 112, Iterations, MaxLength, HpackSequences, data =>
            {
                var decoder = new HpackDecoder();
                var headers = new List<(string Name, string Value)>();
                int pos = 0;
                try
                {
                    while (pos < data.Length)
                    {
                        int length = data.Length - pos;
                        if (length >= 2)
                        {
                            length = Math.Min((data[pos] << 8) | data[pos + 1], length - 2);
                            pos += 2;
                        }

                        decoder.Decode(data, pos, length, headers);
                        pos += length;
                    }
                }
                catch (HpackDecodingException)
                {
                }

                return Task.CompletedTask;
            }));
        }

        [Test]
        public void Http2FrameCodec_MutatedFrameStreams_FailOnlyWithProtocolOrEofErrors()
        {
            AssertAsync.Run(() => SeededMutator.RunAsync("h2-frames", 112, Iterations, MaxLength, Http2FrameStreams, async data =>
            {
                using var stream = new MemoryStream(data, writable: false);
                var codec = new Http2FrameCodec(stream);
                var settings = new Http2Settings();
                try
                {
                    while (true)
                    {
                        using var lease = await codec.ReadFrameLeaseAsync(16384, CancellationToken.None);
                        var frame = lease.Frame;
                        Assert.That(frame.Length, Is.InRange(0, 16384));
                        Assert.GreaterOrEqual(frame.StreamId, 0, "reserved stream-id bit leaked");
                        if (frame.Type == Http2FrameType.Settings && !frame.HasFlag(Http2FrameFlags.Ack))
                        {
                            foreach (var (id, value) in Http2Settings.ParsePayload(frame.Payload, frame.Length))
                                settings.Apply(id, value);
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (Http2ProtocolException)
                {
                }
            }));
        }

        /// <summary>
        /// IOException (truncated input, limits), FormatException (syntax), NotSupportedException
        /// (a transfer coding other than chunked), raw or wrapped in UHttpException.
        /// </summary>
        private static bool IsHttp11Failure(Exception ex)
        {
            if (ex is AssertionException)
                return false;
            while (ex is UHttpException http && http.HttpError?.InnerException != null)
                ex = http.HttpError.InnerException;
            return ex is IOException || ex is FormatException || ex is NotSupportedException ||
                (ex is UHttpException && ex.InnerException == null);
        }
    }
}
