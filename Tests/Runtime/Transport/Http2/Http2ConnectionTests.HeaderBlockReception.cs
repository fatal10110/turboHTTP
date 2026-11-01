using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// Field-block reception: blocks of closed/cancelled streams and PUSH_PROMISE are still
    /// HPACK-decoded (#11), CONTINUATION accumulation is bounded (#20), and malformed response
    /// fields are a stream error (#31).
    /// </summary>
    public partial class Http2ConnectionTests
    {
        // --- #11: blocks for cancelled/closed streams keep the HPACK dynamic table in sync ---

        [Test]
        public void HeaderBlockForCancelledStream_IsDecoded_LaterResponseResolvesItsIndex(
            [Values(false, true)] bool splitAcrossContinuation)
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                using var requestCts = new CancellationTokenSource();
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var serverEncoder = new HpackEncoder();

                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), requestCts.Token).AsTask();
                var headersA = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headersA.Type);

                requestCts.Cancel();
                await AssertCancelledAsync(taskA, cts.Token);
                var rst = await ReadMatchingFrameAsync(serverCodec, f => f.Type == Http2FrameType.RstStream, 5000);
                Assert.AreEqual(headersA.StreamId, rst.StreamId);

                // RFC 9113 Section 5.1: the response already in flight still arrives. Its block
                // inserts x-session into the dynamic table (literal with incremental indexing).
                var fields = new List<(string, string)> { (":status", "200"), ("x-session", "abc123") };
                var blockA = serverEncoder.Encode(fields).ToArray();
                await WriteFieldBlockAsync(serverCodec, Http2FrameType.Headers, headersA.StreamId,
                    Array.Empty<byte>(), blockA, endStream: true, splitAcrossContinuation, cts.Token);

                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/b"));
                var taskB = conn.SendRequestAsync(requestB, new RequestContext(requestB), cts.Token).AsTask();
                var headersB = await ReadMatchingFrameAsync(serverCodec, f => f.Type == Http2FrameType.Headers, 5000);

                var blockB = serverEncoder.Encode(fields).ToArray();
                // :status 200 (static 8) + x-session (dynamic 62): only valid if block A was decoded.
                CollectionAssert.AreEqual(new byte[] { 0x88, 0xBE }, blockB);
                await WriteFieldBlockAsync(serverCodec, Http2FrameType.Headers, headersB.StreamId,
                    Array.Empty<byte>(), blockB, endStream: true, split: false, cts.Token);

                var responseB = await taskB;
                Assert.AreEqual(HttpStatusCode.OK, responseB.StatusCode);
                Assert.AreEqual("abc123", responseB.Headers.Get("x-session"));
                Assert.IsTrue(conn.IsAlive);
                Assert.IsNull(GetConnectionHeaderBlockBuffer(conn), "the block buffer is returned at END_HEADERS");

                conn.Dispose();
            });
        }

        [Test]
        public void StreamRemovedMidHeaderBlock_ContinuationCompletes_NoProtocolError()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                using var requestCts = new CancellationTokenSource();
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var serverEncoder = new HpackEncoder();

                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), requestCts.Token).AsTask();
                var headersA = await serverCodec.ReadFrameAsync(16384, cts.Token);

                var fields = new List<(string, string)> { (":status", "200"), ("x-mid", "m1") };
                var block = serverEncoder.Encode(fields).ToArray();
                int cut = block.Length / 2;

                // HEADERS without END_HEADERS, then the stream is cancelled and removed, then the
                // CONTINUATION that completes the block arrives.
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = headersA.StreamId,
                    Payload = Slice(block, 0, cut),
                    Length = cut
                }, cts.Token);

                requestCts.Cancel();
                await AssertCancelledAsync(taskA, cts.Token);
                await ReadMatchingFrameAsync(serverCodec, f => f.Type == Http2FrameType.RstStream, 5000);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Continuation,
                    Flags = Http2FrameFlags.EndHeaders,
                    StreamId = headersA.StreamId,
                    Payload = Slice(block, cut, block.Length - cut),
                    Length = block.Length - cut
                }, cts.Token);

                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/b"));
                var taskB = conn.SendRequestAsync(requestB, new RequestContext(requestB), cts.Token).AsTask();
                var headersB = await ReadMatchingFrameAsync(serverCodec, f => f.Type == Http2FrameType.Headers, 5000);
                var blockB = serverEncoder.Encode(fields).ToArray();
                await WriteFieldBlockAsync(serverCodec, Http2FrameType.Headers, headersB.StreamId,
                    Array.Empty<byte>(), blockB, endStream: true, split: false, cts.Token);

                var responseB = await taskB;
                Assert.AreEqual(HttpStatusCode.OK, responseB.StatusCode);
                Assert.AreEqual("m1", responseB.Headers.Get("x-mid"));
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            });
        }

        [Test]
        public void PushPromiseWithContinuation_BlockIsDecoded_PushRefused()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var serverEncoder = new HpackEncoder();

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var task = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var headers = await serverCodec.ReadFrameAsync(16384, cts.Token);

                const int promisedStreamId = 2;
                var promised = serverEncoder.Encode(new List<(string, string)>
                {
                    (":method", "GET"),
                    (":scheme", "https"),
                    (":authority", "test.example.com"),
                    (":path", "/pushed"),
                    ("x-push", "p1")
                }).ToArray();
                await WriteFieldBlockAsync(serverCodec, Http2FrameType.PushPromise, headers.StreamId,
                    new byte[] { 0, 0, 0, promisedStreamId }, promised, endStream: false, split: true, cts.Token);

                var refused = await ReadMatchingFrameAsync(serverCodec,
                    f => f.Type == Http2FrameType.RstStream && f.StreamId == promisedStreamId, 5000);
                Assert.AreEqual((uint)Http2ErrorCode.RefusedStream, ReadErrorCode(refused));

                // The response references the entry the PUSH_PROMISE block inserted.
                var response = serverEncoder.Encode(new List<(string, string)> { (":status", "200"), ("x-push", "p1") }).ToArray();
                await WriteFieldBlockAsync(serverCodec, Http2FrameType.Headers, headers.StreamId,
                    Array.Empty<byte>(), response, endStream: true, split: false, cts.Token);

                var result = await task;
                Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
                Assert.AreEqual("p1", result.Headers.Get("x-push"));
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            });
        }

        // --- #20: CONTINUATION accumulation is bounded by frame count and bytes ---

        [Test]
        public void ContinuationFlood_TerminatesConnectionWithEnhanceYourCalm(
            [Values(true, false)] bool zeroLengthFrames)
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                // Byte limit = max(MAX_FRAME_SIZE 16384, min(MaxHeaderListSize, 256 KB)) = 16384.
                var options = new Http2Options { MaxHeaderListSize = 1024 };
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token, options);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/flood"));
                var task = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var headers = await serverCodec.ReadFrameAsync(16384, cts.Token);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.None,
                    StreamId = headers.StreamId,
                    Payload = new byte[] { 0x88 },
                    Length = 1
                }, cts.Token);

                if (zeroLengthFrames)
                {
                    for (int i = 0; i <= Http2Connection.MaxContinuationFramesPerHeaderBlock; i++)
                        await WriteContinuationAsync(serverCodec, headers.StreamId, Array.Empty<byte>(), cts.Token);
                }
                else
                {
                    var chunk = new byte[4096];
                    for (int accumulated = 1; accumulated <= 16384; accumulated += chunk.Length)
                        await WriteContinuationAsync(serverCodec, headers.StreamId, chunk, cts.Token);
                }

                var goaway = await ReadMatchingFrameAsync(serverCodec, f => f.Type == Http2FrameType.GoAway, 5000);
                uint errorCode = ((uint)goaway.Payload[4] << 24)
                    | ((uint)goaway.Payload[5] << 16)
                    | ((uint)goaway.Payload[6] << 8)
                    | goaway.Payload[7];
                Assert.AreEqual((uint)Http2ErrorCode.EnhanceYourCalm, errorCode);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await task);
                Assert.IsInstanceOf<Http2ProtocolException>(ex.HttpError.InnerException);

                conn.Dispose();
            });
        }

        [Test]
        public void PooledHttp2Stream_HoldsNoHeaderBlockBuffer()
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var field in typeof(Http2Stream).GetFields(all))
            {
                Assert.IsFalse(typeof(Stream).IsAssignableFrom(field.FieldType),
                    $"Http2Stream.{field.Name} would keep a grown buffer alive in the stream pool");
            }
        }

        // --- #31: malformed response fields are a stream error, the connection survives ---

        [TestCase("X-Upper", "q7", false)]
        [TestCase("connection", "close", false)]
        [TestCase("keep-alive", "timeout=5", false)]
        [TestCase("proxy-connection", "keep-alive", false)]
        [TestCase("transfer-encoding", "chunked", false)]
        [TestCase("upgrade", "websocket", false)]
        [TestCase("x-bad-value", "a\r\nb", false)]
        [TestCase("x-padded-value", " q7", false)]
        [TestCase("Content-MD5", "abc", true)]
        [TestCase("transfer-encoding", "chunked", true)]
        public void MalformedResponseField_FailsStream_ConnectionStaysUsable(string name, string value, bool inTrailers)
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var serverEncoder = new HpackEncoder();
                var requestDecoder = new HpackDecoder();

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var task = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var headers = await serverCodec.ReadFrameAsync(16384, cts.Token);
                requestDecoder.Decode(headers.Payload, 0, headers.Length);

                if (inTrailers)
                {
                    var head = serverEncoder.Encode(new List<(string, string)> { (":status", "200") }).ToArray();
                    await WriteFieldBlockAsync(serverCodec, Http2FrameType.Headers, headers.StreamId,
                        Array.Empty<byte>(), head, endStream: false, split: false, cts.Token);
                }

                var fields = inTrailers
                    ? new List<(string, string)> { (name, value) }
                    : new List<(string, string)> { (":status", "200"), (name, value) };
                var block = serverEncoder.Encode(fields).ToArray();
                await WriteFieldBlockAsync(serverCodec, Http2FrameType.Headers, headers.StreamId,
                    Array.Empty<byte>(), block, endStream: true, split: false, cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await task);
                StringAssert.Contains("Malformed HTTP/2 response", ex.HttpError.Message);
                Assert.IsFalse(ex.HttpError.Message.Contains(value), "the field value is never echoed");

                // #28: a malformed response is a stream error of type PROTOCOL_ERROR (RFC 9113 Section 8.1.1).
                var reset = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.RstStream, reset.Type);
                Assert.AreEqual(headers.StreamId, reset.StreamId);
                Assert.AreEqual((byte)Http2ErrorCode.ProtocolError, reset.Payload[3]);

                await AssertNextRequestSucceedsAsync(conn, serverCodec, requestDecoder, headers.StreamId, cts.Token);

                conn.Dispose();
            });
        }

        [TestCase("content-type", "text/plain")]
        [TestCase("te", "trailers")]
        [TestCase("x-empty", "")]
        [TestCase("x-inner-space", "a b")]
        public void GetMalformedResponseFieldReason_ValidField_ReturnsNull(string name, string value)
        {
            Assert.IsNull(Http2Connection.GetMalformedResponseFieldReason(name, value));
        }

        private static async Task WriteFieldBlockAsync(
            Http2FrameCodec codec,
            Http2FrameType type,
            int streamId,
            byte[] prefix,
            byte[] block,
            bool endStream,
            bool split,
            CancellationToken ct)
        {
            int cut = split ? block.Length / 2 : block.Length;
            var first = new byte[prefix.Length + cut];
            Buffer.BlockCopy(prefix, 0, first, 0, prefix.Length);
            Buffer.BlockCopy(block, 0, first, prefix.Length, cut);

            var flags = split ? Http2FrameFlags.None : Http2FrameFlags.EndHeaders;
            if (endStream)
                flags |= Http2FrameFlags.EndStream;

            await codec.WriteFrameAsync(new Http2Frame
            {
                Type = type,
                Flags = flags,
                StreamId = streamId,
                Payload = first,
                Length = first.Length
            }, ct);

            if (split)
            {
                await codec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Continuation,
                    Flags = Http2FrameFlags.EndHeaders,
                    StreamId = streamId,
                    Payload = Slice(block, cut, block.Length - cut),
                    Length = block.Length - cut
                }, ct);
            }
        }

        private static Task WriteContinuationAsync(Http2FrameCodec codec, int streamId, byte[] payload, CancellationToken ct)
        {
            return codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Continuation,
                Flags = Http2FrameFlags.None,
                StreamId = streamId,
                Payload = payload,
                Length = payload.Length
            }, ct);
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(source, offset, slice, 0, count);
            return slice;
        }

        private static object GetConnectionHeaderBlockBuffer(Http2Connection conn)
        {
            return typeof(Http2Connection)
                .GetField("_headerBlockBuffer", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(conn);
        }
    }
}
