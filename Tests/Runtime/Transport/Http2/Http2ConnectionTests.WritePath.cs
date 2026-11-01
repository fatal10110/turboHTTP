using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// HTTP/2 write path: everything that reaches the wire is encoded and written under one
    /// _writeLock hold, in order (trailers, stream ids, frames under cancellation, HPACK table size).
    /// </summary>
    public partial class Http2ConnectionTests
    {
        // --- #12: request trailers are encoded and written in one write-lock hold ---

        [Test]
        public void RequestTrailers_OtherStreamEncodesLargeHeadersMeanwhile_ServerDecodesExactTrailers()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                // Stream send windows start at 0: request A stops after its HEADERS (trailer provider
                // already run) until the server opens its window.
                var (conn, serverStream, _) = await CreateInitializedConnectionAsyncWithInitialWindow(0, cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var decoder = new HpackDecoder();

                var providerInvoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var requestA = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/a"))
                    .WithBody(Encoding.UTF8.GetBytes("abc"))
                    .WithRequestTrailers(
                        new[] { "X-Checksum" },
                        () =>
                        {
                            var trailers = new HttpHeaders();
                            trailers.Set("X-Checksum", "sha-256=trailer-a");
                            providerInvoked.TrySetResult(true);
                            return trailers;
                        });
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), cts.Token).AsTask();

                var headersA = await ReadHeaderBlockAsync(serverCodec, cts.Token);
                Assert.IsFalse(headersA.EndStream);
                decoder.Decode(headersA.Block, 0, headersA.Block.Length);
                await WithWatchdog(providerInvoked.Task, cts.Token);

                // Request B encodes a block larger than the encoder's initial output buffer (growth).
                var largeValue = new string('b', 600);
                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/b"));
                requestB.Headers.Set("x-large", largeValue);
                var taskB = conn.SendRequestAsync(requestB, new RequestContext(requestB), cts.Token).AsTask();

                var headersB = await ReadHeaderBlockAsync(serverCodec, cts.Token);
                Assert.Greater(headersB.StreamId, headersA.StreamId);
                var decodedB = decoder.Decode(headersB.Block, 0, headersB.Block.Length);
                Assert.IsTrue(decodedB.Any(h => h.Name == "x-large" && h.Value == largeValue));

                await serverCodec.WriteFrameAsync(BuildWindowUpdateFrame(headersA.StreamId, 1024), cts.Token);

                var data = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Data, data.Type);
                Assert.AreEqual(headersA.StreamId, data.StreamId);
                Assert.IsFalse(data.HasFlag(Http2FrameFlags.EndStream));
                Assert.AreEqual("abc", Encoding.UTF8.GetString(data.Payload, 0, data.Length));

                var trailerBlock = await ReadHeaderBlockAsync(serverCodec, cts.Token);
                Assert.AreEqual(headersA.StreamId, trailerBlock.StreamId);
                Assert.IsTrue(trailerBlock.EndStream);
                var decodedTrailers = decoder.Decode(trailerBlock.Block, 0, trailerBlock.Block.Length);
                CollectionAssert.AreEqual(
                    new List<(string Name, string Value)> { ("x-checksum", "sha-256=trailer-a") },
                    decodedTrailers);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersA.StreamId, 200, endStream: true), cts.Token);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersB.StreamId, 200, endStream: true), cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, (await taskA).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, (await taskB).StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void RequestTrailers_SkippedAfterPeerReset_DoNotDesyncLaterHeaderBlocks()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsyncWithInitialWindow(0, cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var decoder = new HpackDecoder();

                var providerInvoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var requestA = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/a"))
                    .WithBody(Encoding.UTF8.GetBytes("abc"))
                    .WithRequestTrailers(
                        new[] { "X-Trailer-Probe" },
                        () =>
                        {
                            var trailers = new HttpHeaders();
                            trailers.Set("X-Trailer-Probe", "probe-value");
                            providerInvoked.TrySetResult(true);
                            return trailers;
                        });
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), cts.Token).AsTask();

                var headersA = await ReadHeaderBlockAsync(serverCodec, cts.Token);
                decoder.Decode(headersA.Block, 0, headersA.Block.Length);
                await WithWatchdog(providerInvoked.Task, cts.Token);

                // The response completes while the request body is blocked on flow control, and the
                // server stops the upload with RST_STREAM(NO_ERROR) (RFC 9113 Section 8.1): the sender
                // stops and never sends (or encodes) the trailers. A complete response alone would not
                // stop the upload (#19).
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersA.StreamId, 200, endStream: true), cts.Token);
                await serverCodec.WriteFrameAsync(
                    BuildRstStreamFrame(headersA.StreamId, Http2ErrorCode.NoError), cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, (await taskA).StatusCode);

                // A later header block repeats the unsent trailer field: the server's decoder must
                // see exactly what the client's encoder meant.
                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/b"));
                requestB.Headers.Set("x-trailer-probe", "probe-value");
                var taskB = conn.SendRequestAsync(requestB, new RequestContext(requestB), cts.Token).AsTask();

                Http2Frame frame;
                while (true)
                {
                    frame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                    if (frame.Type == Http2FrameType.RstStream && frame.StreamId == headersA.StreamId)
                        continue;
                    break;
                }

                Assert.AreEqual(Http2FrameType.Headers, frame.Type, "no DATA or trailers may follow for the completed stream");
                Assert.AreNotEqual(headersA.StreamId, frame.StreamId);
                Assert.IsTrue(frame.HasFlag(Http2FrameFlags.EndHeaders));
                var decodedB = decoder.Decode(frame.Payload, 0, frame.Length);
                Assert.AreEqual(
                    1,
                    decodedB.Count(h => h.Name == "x-trailer-probe" && h.Value == "probe-value"));
                Assert.AreEqual("/b", decodedB.First(h => h.Name == ":path").Value);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(frame.StreamId, 200, endStream: true), cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, (await taskB).StatusCode);

                conn.Dispose();
            });
        }

        // --- #13: stream ids reach the wire in increasing order ---

        [Test]
        public void ConcurrentDispatches_StreamIdsReachTheWireInIncreasingOrder()
        {
            AssertAsync.Run(async () =>
            {
                const int requestCount = 200;
                using var cts = new CancellationTokenSource(30000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var decoder = new HpackDecoder();

                var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var tasks = new Task<UHttpResponse>[requestCount];
                for (int i = 0; i < requestCount; i++)
                {
                    int index = i;
                    tasks[i] = Task.Run(async () =>
                    {
                        await start.Task.ConfigureAwait(false);
                        var request = new UHttpRequest(
                            HttpMethod.GET,
                            new Uri("https://test.example.com/r/" + index));
                        return await conn.SendRequestAsync(request, new RequestContext(request), cts.Token)
                            .ConfigureAwait(false);
                    });
                }

                start.SetResult(true);

                int lastStreamId = 0;
                var paths = new HashSet<string>();
                for (int i = 0; i < requestCount; i++)
                {
                    var block = await ReadHeaderBlockAsync(serverCodec, cts.Token);
                    // RFC 9113 Section 5.1.1: a new stream id lower than one already used is a
                    // connection error (PROTOCOL_ERROR) at a strict peer.
                    Assert.Greater(block.StreamId, lastStreamId, $"HEADERS #{i} reached the wire out of order");
                    lastStreamId = block.StreamId;

                    var decoded = decoder.Decode(block.Block, 0, block.Block.Length);
                    Assert.IsTrue(paths.Add(decoded.First(h => h.Name == ":path").Value));

                    await serverCodec.WriteFrameAsync(
                        BuildResponseHeadersFrame(block.StreamId, 200, endStream: true), cts.Token);
                }

                var responses = await Task.WhenAll(tasks);
                Assert.IsTrue(responses.All(r => r.StatusCode == HttpStatusCode.OK));

                conn.Dispose();
            });
        }

        [Test]
        public void RequestCancelledWhileWaitingForWriteLock_ConsumesNoStreamIdAndSendsNoFrame()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var waitingCts = new CancellationTokenSource();
                var (conn, serverStream, gate) = await CreateGatedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var decoder = new HpackDecoder();

                // Request X holds the write lock: its DATA payload write is held mid-frame.
                var body = Enumerable.Repeat((byte)'x', 777).ToArray();
                var payloadHeld = gate.BlockWrite(count => count == body.Length);
                var requestX = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/x"))
                    .WithBody(body);
                var taskX = conn.SendRequestAsync(requestX, new RequestContext(requestX), cts.Token).AsTask();
                var headersX = await ReadHeaderBlockAsync(serverCodec, cts.Token);
                decoder.Decode(headersX.Block, 0, headersX.Block.Length);
                await WithWatchdog(payloadHeld, cts.Token);

                // Request A is cancelled while it waits for the write lock: it never opened a stream,
                // so it must neither consume a stream id nor send RST_STREAM for an idle stream
                // (a connection error, RFC 9113 Section 6.4).
                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), waitingCts.Token).AsTask();
                waitingCts.Cancel();
                await AssertCancelledAsync(taskA, cts.Token);

                gate.Release();
                var data = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Data, data.Type);
                Assert.AreEqual(headersX.StreamId, data.StreamId);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersX.StreamId, 200, endStream: true), cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, (await taskX).StatusCode);

                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/b"));
                var taskB = conn.SendRequestAsync(requestB, new RequestContext(requestB), cts.Token).AsTask();
                var next = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, next.Type, "no frame may be sent for the cancelled request");
                Assert.AreEqual(headersX.StreamId + 2, next.StreamId, "stream ids are allocated when HEADERS are written");
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(next.StreamId, 200, endStream: true), cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, (await taskB).StatusCode);

                conn.Dispose();
            });
        }

        // --- #14: request cancellation never leaves a partial frame or header block ---

        [Test]
        public void RequestCancelledMidDataFrame_FrameCompletes_ConnectionStaysUsable()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var requestCts = new CancellationTokenSource();
                var (conn, serverStream, gate) = await CreateGatedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var decoder = new HpackDecoder();
                int initialConnectionWindow = ReadConnectionSendWindow(conn);

                var body = Enumerable.Repeat((byte)'a', 777).ToArray();
                var payloadHeld = gate.BlockWrite(count => count == body.Length);
                var requestA = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/upload"))
                    .WithBody(body);
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), requestCts.Token).AsTask();

                var headersA = await ReadHeaderBlockAsync(serverCodec, cts.Token);
                decoder.Decode(headersA.Block, 0, headersA.Block.Length);

                // The DATA frame header is on the wire and its payload write is held: cancel now.
                await WithWatchdog(payloadHeld, cts.Token);
                requestCts.Cancel();
                gate.Release();

                var data = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Data, data.Type);
                Assert.AreEqual(headersA.StreamId, data.StreamId);
                Assert.AreEqual(body.Length, data.Length);
                Assert.IsTrue(data.Payload.Take(data.Length).All(b => b == (byte)'a'));

                await AssertCancelledAsync(taskA, cts.Token);

                await AssertNextRequestSucceedsAsync(conn, serverCodec, decoder, headersA.StreamId, cts.Token);

                // Flow control counts exactly the DATA that was written (RFC 9113 Section 6.9.1).
                Assert.AreEqual(initialConnectionWindow - body.Length, ReadConnectionSendWindow(conn));

                conn.Dispose();
            });
        }

        [Test]
        public void DataFrameOnWire_PeerReturnsCreditBeforeWriteCompletes_MaxWindowDoesNotOverflow()
        {
            AssertAsync.Run(async () =>
            {
                // The client has read the server's SETTINGS and SETTINGS ACK (two empty frames).
                const int frameHeaderSize = 9;
                const int windowUpdateFrameSize = frameHeaderSize + 4;
                long clientBytesRead = 2 * frameHeaderSize;

                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, gate) = await CreateGatedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var decoder = new HpackDecoder();

                // Raise the connection send window to the RFC 9113 Section 6.9.1 maximum, 2^31-1.
                int initialConnectionWindow = ReadConnectionSendWindow(conn);
                var processed = gate.WaitForReadStartedAfter(clientBytesRead += windowUpdateFrameSize);
                await serverCodec.WriteFrameAsync(
                    BuildWindowUpdateFrame(0, int.MaxValue - initialConnectionWindow), cts.Token);
                await WithWatchdog(processed, cts.Token);
                Assert.AreEqual(int.MaxValue, ReadConnectionSendWindow(conn));

                var body = Enumerable.Repeat((byte)'b', 100).ToArray();
                var payloadHeld = gate.BlockWrite(count => count == body.Length);
                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/upload"))
                    .WithBody(body);
                var task = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();

                var headers = await ReadHeaderBlockAsync(serverCodec, cts.Token);
                decoder.Decode(headers.Block, 0, headers.Block.Length);

                // The DATA frame is being written. The peer may return its credit as soon as it has
                // the frame, so the read loop can see the WINDOW_UPDATE before the write call returns.
                await WithWatchdog(payloadHeld, cts.Token);
                processed = gate.WaitForReadStartedAfter(clientBytesRead + windowUpdateFrameSize);
                await serverCodec.WriteFrameAsync(BuildWindowUpdateFrame(0, body.Length), cts.Token);
                await WithWatchdog(processed, cts.Token);

                Assert.IsTrue(conn.IsAlive, "a legal WINDOW_UPDATE must not overflow the send window");
                Assert.AreEqual(int.MaxValue, ReadConnectionSendWindow(conn));

                gate.Release();

                var data = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Data, data.Type);
                Assert.AreEqual(headers.StreamId, data.StreamId);
                Assert.AreEqual(body.Length, data.Length);
                Assert.IsTrue(data.HasFlag(Http2FrameFlags.EndStream));

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headers.StreamId, 200, endStream: true), cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, (await task).StatusCode);

                await AssertNextRequestSucceedsAsync(conn, serverCodec, decoder, headers.StreamId, cts.Token);
                Assert.AreEqual(int.MaxValue, ReadConnectionSendWindow(conn));

                conn.Dispose();
            });
        }

        [Test]
        public void RequestCancelledMidHeaderBlock_ContinuationCompletes_ConnectionStaysUsable()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var requestCts = new CancellationTokenSource();
                var (conn, serverStream, gate) = await CreateGatedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var decoder = new HpackDecoder();

                // ~26 KB after Huffman coding: HEADERS (16384) + one CONTINUATION.
                var bigValue = new string('x', 30000);
                bool sawFullFrame = false;
                var continuationHeld = gate.BlockWrite(count =>
                {
                    if (count == 16384)
                    {
                        sawFullFrame = true;
                        return false;
                    }

                    return sawFullFrame && count > 9;
                });

                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/big"));
                requestA.Headers.Set("x-big", bigValue);
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), requestCts.Token).AsTask();

                var first = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, first.Type);
                Assert.IsFalse(first.HasFlag(Http2FrameFlags.EndHeaders));

                // The CONTINUATION frame header is on the wire and its payload write is held.
                await WithWatchdog(continuationHeld, cts.Token);
                requestCts.Cancel();
                gate.Release();

                var continuation = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Continuation, continuation.Type);
                Assert.AreEqual(first.StreamId, continuation.StreamId);
                Assert.IsTrue(continuation.HasFlag(Http2FrameFlags.EndHeaders));

                var block = new byte[first.Length + continuation.Length];
                Buffer.BlockCopy(first.Payload, 0, block, 0, first.Length);
                Buffer.BlockCopy(continuation.Payload, 0, block, first.Length, continuation.Length);
                var decoded = decoder.Decode(block, 0, block.Length);
                Assert.AreEqual(bigValue, decoded.First(h => h.Name == "x-big").Value);

                await AssertCancelledAsync(taskA, cts.Token);

                await AssertNextRequestSucceedsAsync(conn, serverCodec, decoder, first.StreamId, cts.Token);

                conn.Dispose();
            });
        }

        // --- #27: HPACK table size changes signal the minimum, then the final size ---

        [Test]
        public void HeaderTableSize_LoweredThenRaised_EncoderSignalsMinimumThenFinal(
            [Values(false, true)] bool inOneSettingsFrame)
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var decoder = new HpackDecoder();

                // Populate both dynamic tables.
                await ExchangeProbeRequestAsync(conn, serverCodec, decoder, "/one", cts.Token);

                if (inOneSettingsFrame)
                {
                    var payload = BuildSettingsPayload(Http2SettingId.HeaderTableSize, 0)
                        .Concat(BuildSettingsPayload(Http2SettingId.HeaderTableSize, 4096))
                        .ToArray();
                    await WriteSettingsAndReadAckAsync(serverCodec, payload, cts.Token);
                }
                else
                {
                    await WriteSettingsAndReadAckAsync(
                        serverCodec, BuildSettingsPayload(Http2SettingId.HeaderTableSize, 0), cts.Token);
                    await WriteSettingsAndReadAckAsync(
                        serverCodec, BuildSettingsPayload(Http2SettingId.HeaderTableSize, 4096), cts.Token);
                }

                // The decoder now requires a size update at the start of the next block.
                decoder.SetMaxDynamicTableSize(4096);

                var block = await ExchangeProbeRequestAsync(conn, serverCodec, decoder, "/two", cts.Token);
                // RFC 7541 Section 4.2 / 6.3: size update 0 (001 00000), then 4096 (001 11111, 4065).
                CollectionAssert.AreEqual(new byte[] { 0x20, 0x3F, 0xE1, 0x1F }, block.Take(4).ToArray());

                // Tables stay in sync for later blocks.
                await ExchangeProbeRequestAsync(conn, serverCodec, decoder, "/three", cts.Token);

                conn.Dispose();
            });
        }

        // --- helpers ---

        private async Task<(Http2Connection conn, Stream serverStream, GatedWriteStream gate)>
            CreateGatedConnectionAsync(CancellationToken ct, Http2Options options = null)
        {
            var duplex = new TestDuplexStream();
            var gate = new GatedWriteStream(duplex.ClientStream);
            var conn = new Http2Connection(gate, "test.example.com", 443, options ?? new Http2Options());

            var serverCodec = new Http2FrameCodec(duplex.ServerStream);
            var serverTask = Task.Run(async () =>
            {
                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read, ct);
                    if (n == 0) throw new IOException("Unexpected end of stream");
                    read += n;
                }

                await serverCodec.ReadFrameAsync(16384, ct);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, ct);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, ct);
                await serverCodec.ReadFrameAsync(16384, ct);
            }, ct);

            await conn.InitializeAsync(ct);
            await serverTask;
            return (conn, duplex.ServerStream, gate);
        }

        private static async Task<(int StreamId, bool EndStream, byte[] Block)> ReadHeaderBlockAsync(
            Http2FrameCodec serverCodec,
            CancellationToken ct)
        {
            var first = await serverCodec.ReadFrameAsync(16384, ct);
            Assert.AreEqual(Http2FrameType.Headers, first.Type, "expected a HEADERS frame");

            var block = new List<byte>(first.Payload.Take(first.Length));
            var last = first;
            while (!last.HasFlag(Http2FrameFlags.EndHeaders))
            {
                last = await serverCodec.ReadFrameAsync(16384, ct);
                // RFC 9113 Section 6.10: nothing may interleave with a header block.
                Assert.AreEqual(Http2FrameType.Continuation, last.Type);
                Assert.AreEqual(first.StreamId, last.StreamId);
                block.AddRange(last.Payload.Take(last.Length));
            }

            return (first.StreamId, first.HasFlag(Http2FrameFlags.EndStream), block.ToArray());
        }

        private async Task<byte[]> ExchangeProbeRequestAsync(
            Http2Connection conn,
            Http2FrameCodec serverCodec,
            HpackDecoder decoder,
            string path,
            CancellationToken ct)
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com" + path));
            request.Headers.Set("x-probe", "probe-value");
            var task = conn.SendRequestAsync(request, new RequestContext(request), ct).AsTask();

            var block = await ReadHeaderBlockAsync(serverCodec, ct);
            var decoded = decoder.Decode(block.Block, 0, block.Block.Length);
            Assert.AreEqual(path, decoded.First(h => h.Name == ":path").Value);
            Assert.AreEqual("probe-value", decoded.First(h => h.Name == "x-probe").Value);

            await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(block.StreamId, 200, endStream: true), ct);
            Assert.AreEqual(HttpStatusCode.OK, (await task).StatusCode);
            return block.Block;
        }

        private async Task AssertNextRequestSucceedsAsync(
            Http2Connection conn,
            Http2FrameCodec serverCodec,
            HpackDecoder decoder,
            int cancelledStreamId,
            CancellationToken ct)
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/after"));
            var task = conn.SendRequestAsync(request, new RequestContext(request), ct).AsTask();

            Http2Frame frame;
            while (true)
            {
                frame = await serverCodec.ReadFrameAsync(16384, ct);
                if (frame.Type != Http2FrameType.RstStream)
                    break;

                // The cancelled stream's RST_STREAM(CANCEL) is the only other frame allowed.
                Assert.AreEqual(cancelledStreamId, frame.StreamId);
                Assert.AreEqual(4, frame.Length);
                Assert.AreEqual((byte)Http2ErrorCode.Cancel, frame.Payload[3]);
            }

            Assert.AreEqual(Http2FrameType.Headers, frame.Type);
            Assert.Greater(frame.StreamId, cancelledStreamId);
            Assert.IsTrue(frame.HasFlag(Http2FrameFlags.EndHeaders));
            var decoded = decoder.Decode(frame.Payload, 0, frame.Length);
            Assert.AreEqual("/after", decoded.First(h => h.Name == ":path").Value);

            await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(frame.StreamId, 200, endStream: true), ct);
            Assert.AreEqual(HttpStatusCode.OK, (await task).StatusCode);
            Assert.IsTrue(conn.IsAlive);
        }

        private static async Task WriteSettingsAndReadAckAsync(
            Http2FrameCodec serverCodec,
            byte[] payload,
            CancellationToken ct)
        {
            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Settings,
                Flags = Http2FrameFlags.None,
                StreamId = 0,
                Payload = payload,
                Length = payload.Length
            }, ct);

            var ack = await serverCodec.ReadFrameAsync(16384, ct);
            Assert.AreEqual(Http2FrameType.Settings, ack.Type);
            Assert.IsTrue(ack.HasFlag(Http2FrameFlags.Ack));
        }

        private static Http2Frame BuildWindowUpdateFrame(int streamId, int increment)
        {
            return new Http2Frame
            {
                Type = Http2FrameType.WindowUpdate,
                Flags = Http2FrameFlags.None,
                StreamId = streamId,
                Payload = new[]
                {
                    (byte)((increment >> 24) & 0x7F),
                    (byte)((increment >> 16) & 0xFF),
                    (byte)((increment >> 8) & 0xFF),
                    (byte)(increment & 0xFF)
                },
                Length = 4
            };
        }

        private static int ReadConnectionSendWindow(Http2Connection conn)
        {
            var field = typeof(Http2Connection).GetField(
                "_connectionSendWindow",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return (int)field.GetValue(conn);
        }

        private static async Task AssertCancelledAsync(Task task, CancellationToken watchdog)
        {
            try
            {
                await WithWatchdog(task, watchdog);
            }
            catch (OperationCanceledException) when (!watchdog.IsCancellationRequested)
            {
                return; // the expected outcome
            }

            Assert.Fail("Expected the cancelled request to fail with OperationCanceledException.");
        }

        private static async Task WithWatchdog(Task task, CancellationToken watchdog)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (watchdog.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, cancelled.Task) != task)
                    Assert.Fail("Test watchdog expired.");
            }

            await task;
        }
    }
}
