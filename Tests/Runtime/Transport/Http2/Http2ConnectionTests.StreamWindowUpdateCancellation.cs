using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    // #687, #689: the stream WINDOW_UPDATE for a consumed chunk is sent under the connection token, so
    // a body read whose token is cancelled while the write lock is held never drops it (RFC 9113
    // Section 6.9.1: the peer's window only grows through WINDOW_UPDATE).
    public partial class Http2ConnectionTests
    {
        private const int WindowTestChunkBytes = 16384;
        private const int WindowTestInitialWindow = 65535;
        private const int MissingWindowUpdateTimeoutMs = 5000;

        [Test]
        public void BodyReadCancelledWhileWriteLockHeld_StillSendsStreamWindowUpdate()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(30000);
                await RunCancelledReadDownloadAsync(rounds: 1, cts.Token);
            });
        }

        [Test]
        public void BodyReadsRepeatedlyCancelledWhileWriteLockHeld_DownloadContinuesPastInitialStreamWindow()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(30000);

                // 5 x 16384 bytes exceeds the initial 65535-byte stream window, so the download only
                // finishes when every cancelled read still advertised its credit.
                await RunCancelledReadDownloadAsync(rounds: 5, cts.Token);
            });
        }

        /// <summary>
        /// Plays a peer that honours flow control: each round sends one DATA chunk within the stream and
        /// connection windows it was given, holds the client's write lock with a held HEADERS write,
        /// reads the chunk with a token that is cancelled while the read waits for that lock, then
        /// releases the write and expects the stream WINDOW_UPDATE for the chunk.
        /// </summary>
        private async Task RunCancelledReadDownloadAsync(int rounds, CancellationToken ct)
        {
            var options = new Http2Options { InitialWindowSize = WindowTestInitialWindow };
            var (conn, serverStream, gate) = await CreateGatedConnectionAsync(ct, options);
            var serverCodec = new Http2FrameCodec(serverStream);
            var lockHolders = new List<Task>();
            var lockHolderRequests = new List<UHttpRequest>();

            // The gated-connection server wrote an empty SETTINGS and a SETTINGS ACK.
            long bytesSentToClient = 2 * FrameHeaderBytes;
            long peerStreamWindow = WindowTestInitialWindow;
            long peerConnectionWindow = WindowTestInitialWindow;
            long delivered = 0;

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/download"));
            try
            {
                var responseTask = conn.SendStreamingRequestAsync(request, new RequestContext(request), ct).AsTask();
                var requestHeaders = await serverCodec.ReadFrameAsync(16384, ct);
                int streamId = requestHeaders.StreamId;

                var responseHeaders = BuildResponseHeadersFrame(streamId, 200);
                await serverCodec.WriteFrameAsync(responseHeaders, ct);
                bytesSentToClient += FrameHeaderBytes + responseHeaders.Length;

                await using var response = await responseTask;
                var payload = new byte[WindowTestChunkBytes];
                var readBuffer = new byte[WindowTestChunkBytes];

                for (int round = 0; round < rounds; round++)
                {
                    Assert.GreaterOrEqual(peerStreamWindow, WindowTestChunkBytes,
                        $"The peer's stream window is exhausted after {delivered} bytes.");
                    Assert.GreaterOrEqual(peerConnectionWindow, WindowTestChunkBytes,
                        $"The peer's connection window is exhausted after {delivered} bytes.");

                    // Registered before the write: the read loop's next read starts once it has
                    // queued the chunk (and sent any connection WINDOW_UPDATE it needed).
                    bytesSentToClient += FrameHeaderBytes + WindowTestChunkBytes;
                    var chunkQueued = gate.WaitForReadStartedAfter(bytesSentToClient);
                    await serverCodec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.Data,
                        Flags = Http2FrameFlags.None,
                        StreamId = streamId,
                        Payload = payload,
                        Length = payload.Length
                    }, ct);
                    peerStreamWindow -= WindowTestChunkBytes;
                    peerConnectionWindow -= WindowTestChunkBytes;
                    await WithWatchdog(chunkQueued, ct);

                    // A held HEADERS write of another request owns the write lock.
                    var writeHeld = gate.BlockWrite(count => count > FrameHeaderBytes, holdUntilReleased: true);
                    var holder = new UHttpRequest(
                        HttpMethod.GET,
                        new Uri($"https://test.example.com/hold-the-write-lock-round-{round}"));
                    lockHolderRequests.Add(holder);
                    lockHolders.Add(conn.SendRequestAsync(holder, new RequestContext(holder), CancellationToken.None).AsTask());
                    await WithWatchdog(writeHeld, ct);

                    using (var readCts = new CancellationTokenSource())
                    {
                        // The chunk is queued, so the read consumes it synchronously and then waits
                        // for the write lock to advertise the freed window.
                        var read = response.Body.ReadAsync(readBuffer, 0, readBuffer.Length, readCts.Token);
                        Assert.IsFalse(read.IsCompleted, "the read must wait behind the held write");

                        readCts.Cancel();
                        gate.Release();

                        await WithWatchdog(read, ct);
                        Assert.AreEqual(WindowTestChunkBytes, read.Result, "the consumed bytes are still returned");
                    }

                    delivered += WindowTestChunkBytes;
                    peerStreamWindow += await ReadStreamWindowUpdateAsync(
                        serverCodec,
                        streamId,
                        increment => peerConnectionWindow += increment,
                        delivered);

                    // RFC 9113 Section 6.9.1: the client consumed everything, so the peer's view of
                    // the stream window must be back at the full window the client credited locally.
                    Assert.AreEqual(WindowTestInitialWindow, peerStreamWindow,
                        $"The peer's stream window is short after {delivered} bytes.");
                }

                Assert.AreEqual((long)rounds * WindowTestChunkBytes, delivered);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, ct);
                Assert.AreEqual(0, await response.Body.ReadAsync(readBuffer, 0, readBuffer.Length, ct));
            }
            finally
            {
                conn.Dispose();
                foreach (var holder in lockHolders)
                {
                    try
                    {
                        await holder;
                    }
                    catch (Exception)
                    {
                        // The lock holders never get a response; disposing the connection fails them.
                    }
                }

                foreach (var holder in lockHolderRequests)
                    holder.Dispose();
                request.Dispose();
            }
        }

        private static async Task<long> ReadStreamWindowUpdateAsync(
            Http2FrameCodec serverCodec,
            int streamId,
            Action<long> onConnectionWindowUpdate,
            long delivered)
        {
            using var timeoutCts = new CancellationTokenSource(MissingWindowUpdateTimeoutMs);
            try
            {
                while (true)
                {
                    var frame = await serverCodec.ReadFrameAsync(16384, timeoutCts.Token);
                    if (frame.Type != Http2FrameType.WindowUpdate)
                        continue; // The lock holder's HEADERS.

                    long increment = ReadWindowIncrement(frame);
                    if (frame.StreamId == 0)
                    {
                        onConnectionWindowUpdate(increment);
                        continue;
                    }

                    Assert.AreEqual(streamId, frame.StreamId);
                    return increment;
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                Assert.Fail($"No stream WINDOW_UPDATE after a cancelled read ({delivered} bytes delivered): the consumed credit was never advertised.");
                return 0;
            }
        }
    }
}
