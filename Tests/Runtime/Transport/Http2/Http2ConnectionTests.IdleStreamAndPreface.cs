using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    // RFC 9113 Section 5.1 (frames on idle streams) and Section 3.4 (the server preface is SETTINGS).
    public partial class Http2ConnectionTests
    {
        public enum IdleFrameKind
        {
            Data,
            Headers,
            RstStream,
            WindowUpdate,
            PushPromise
        }

        [TestCase(IdleFrameKind.Data, false)]
        [TestCase(IdleFrameKind.Data, true)]
        [TestCase(IdleFrameKind.Headers, false)]
        [TestCase(IdleFrameKind.Headers, true)]
        [TestCase(IdleFrameKind.RstStream, false)]
        [TestCase(IdleFrameKind.RstStream, true)]
        [TestCase(IdleFrameKind.WindowUpdate, false)]
        [TestCase(IdleFrameKind.WindowUpdate, true)]
        [TestCase(IdleFrameKind.PushPromise, false)]
        public void IdleStream_FrameOnUnopenedId_IsConnectionProtocolError(IdleFrameKind kind, bool evenId)
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var headers = await serverCodec.ReadFrameAsync(16384, cts.Token);

                // Odd: the next client id, never opened. Even: no PUSH_PROMISE reserved it.
                int idleId = evenId ? 2 : headers.StreamId + 2;
                await serverCodec.WriteFrameAsync(BuildFrameOnStream(kind, idleId), cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                var pex = ex.HttpError.InnerException as Http2ProtocolException;
                Assert.IsNotNull(pex, ex.ToString());
                Assert.AreEqual(Http2ErrorCode.ProtocolError, pex.ErrorCode);
                Assert.IsFalse(conn.IsAlive);

                var goaway = await ReadMatchingFrameAsync(serverCodec, f => f.Type == Http2FrameType.GoAway, 5000);
                Assert.AreEqual((uint)Http2ErrorCode.ProtocolError, ReadGoAwayErrorCode(goaway));

                conn.Dispose();
            });
        }

        [Test]
        public void IdleStream_PriorityOnUnopenedIds_KeepsConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var headers = await serverCodec.ReadFrameAsync(16384, cts.Token);

                // RFC 9113 Section 5.1: PRIORITY may be sent on an idle stream.
                await serverCodec.WriteFrameAsync(BuildPriorityFrame(headers.StreamId + 2), cts.Token);
                await serverCodec.WriteFrameAsync(BuildPriorityFrame(2), cts.Token);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headers.StreamId, 200, endStream: true), cts.Token);

                var response = await responseTask;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            });
        }

        [Test]
        public void ClosedStream_FramesOnCompletedId_AreTolerated()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var first = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var firstTask = conn.SendRequestAsync(first, new RequestContext(first), cts.Token).AsTask();
                var firstHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(firstHeaders.StreamId, 200, endStream: true), cts.Token);
                await firstTask;

                // The stream was opened and completed: it is closed, not idle (RFC 9113 Section 5.1).
                await serverCodec.WriteFrameAsync(BuildFrameOnStream(IdleFrameKind.WindowUpdate, firstHeaders.StreamId), cts.Token);
                await serverCodec.WriteFrameAsync(BuildFrameOnStream(IdleFrameKind.RstStream, firstHeaders.StreamId), cts.Token);
                await serverCodec.WriteFrameAsync(BuildFrameOnStream(IdleFrameKind.Data, firstHeaders.StreamId), cts.Token);

                var second = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/b"));
                var secondTask = conn.SendRequestAsync(second, new RequestContext(second), cts.Token).AsTask();
                var secondHeaders = await ReadMatchingFrameAsync(serverCodec,
                    f => f.Type == Http2FrameType.Headers, 5000);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(secondHeaders.StreamId, 200, endStream: true), cts.Token);

                var response = await secondTask;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            });
        }

        [Test]
        public void PromisedStream_FramesAfterRefusal_AreTolerated()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var serverEncoder = new HpackEncoder();

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var headers = await serverCodec.ReadFrameAsync(16384, cts.Token);

                await WritePushPromiseAsync(serverCodec, serverEncoder, headers.StreamId, promisedStreamId: 2, cts.Token);
                var refused = await ReadMatchingFrameAsync(serverCodec,
                    f => f.Type == Http2FrameType.RstStream && f.StreamId == 2, 5000);
                Assert.AreEqual((uint)Http2ErrorCode.RefusedStream, ReadErrorCode(refused));

                // Stream 2 was reserved, then closed by the refusal: the pushed response is not on an idle id.
                var pushed = serverEncoder.Encode(new List<(string, string)> { (":status", "200") }).ToArray();
                await WriteFieldBlockAsync(serverCodec, Http2FrameType.Headers, 2,
                    Array.Empty<byte>(), pushed, endStream: false, split: false, cts.Token);
                await serverCodec.WriteFrameAsync(BuildFrameOnStream(IdleFrameKind.Data, 2), cts.Token);

                var response = serverEncoder.Encode(new List<(string, string)> { (":status", "200") }).ToArray();
                await WriteFieldBlockAsync(serverCodec, Http2FrameType.Headers, headers.StreamId,
                    Array.Empty<byte>(), response, endStream: true, split: false, cts.Token);

                var result = await responseTask;
                Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            });
        }

        [TestCase(3)]
        [TestCase(2)]
        public void PushPromise_IllegalPromisedId_IsConnectionProtocolError(int secondPromisedId)
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var serverEncoder = new HpackEncoder();

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var headers = await serverCodec.ReadFrameAsync(16384, cts.Token);

                // RFC 9113 Sections 5.1.1 and 6.6: a promised id must be even and above every earlier one.
                await WritePushPromiseAsync(serverCodec, serverEncoder, headers.StreamId, promisedStreamId: 2, cts.Token);
                await WritePushPromiseAsync(serverCodec, serverEncoder, headers.StreamId, secondPromisedId, cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                var pex = ex.HttpError.InnerException as Http2ProtocolException;
                Assert.IsNotNull(pex, ex.ToString());
                Assert.AreEqual(Http2ErrorCode.ProtocolError, pex.ErrorCode);
                Assert.IsFalse(conn.IsAlive);

                var goaway = await ReadMatchingFrameAsync(serverCodec, f => f.Type == Http2FrameType.GoAway, 5000);
                Assert.AreEqual((uint)Http2ErrorCode.ProtocolError, ReadGoAwayErrorCode(goaway));

                conn.Dispose();
            });
        }

        public enum PrefaceFrameKind
        {
            Ping,
            SettingsAck,
            Headers,
            WindowUpdate
        }

        [TestCase(PrefaceFrameKind.Ping)]
        [TestCase(PrefaceFrameKind.SettingsAck)]
        [TestCase(PrefaceFrameKind.Headers)]
        [TestCase(PrefaceFrameKind.WindowUpdate)]
        public void ServerPreface_FirstFrameNotSettings_FailsInitializeWithProtocolError(PrefaceFrameKind kind)
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var duplex = new TestDuplexStream();
                var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443,
                    new Http2Options(), new StreamingOptions());
                var serverCodec = new Http2FrameCodec(duplex.ServerStream);

                var initTask = conn.InitializeAsync(cts.Token);
                await ReadClientPrefaceAsync(duplex.ServerStream, serverCodec, cts.Token);

                Http2Frame first;
                switch (kind)
                {
                    case PrefaceFrameKind.Ping:
                        first = new Http2Frame
                        {
                            Type = Http2FrameType.Ping,
                            Flags = Http2FrameFlags.None,
                            StreamId = 0,
                            Payload = new byte[8],
                            Length = 8
                        };
                        break;
                    case PrefaceFrameKind.SettingsAck:
                        first = new Http2Frame
                        {
                            Type = Http2FrameType.Settings,
                            Flags = Http2FrameFlags.Ack,
                            StreamId = 0,
                            Payload = Array.Empty<byte>(),
                            Length = 0
                        };
                        break;
                    case PrefaceFrameKind.Headers:
                        first = BuildResponseHeadersFrame(1, 200, endStream: true);
                        break;
                    default:
                        first = BuildWindowUpdateFrameOn(0, 1);
                        break;
                }

                await serverCodec.WriteFrameAsync(first, cts.Token);

                // Fails with the protocol error, not at the 5 s SETTINGS ACK timeout.
                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await initTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type, ex.ToString());
                var pex = ex.HttpError.InnerException as Http2ProtocolException;
                Assert.IsNotNull(pex, ex.ToString());
                Assert.AreEqual(Http2ErrorCode.ProtocolError, pex.ErrorCode);
                Assert.IsFalse(conn.IsAlive);

                var goaway = await ReadMatchingFrameAsync(serverCodec, f => f.Type == Http2FrameType.GoAway, 5000);
                Assert.AreEqual((uint)Http2ErrorCode.ProtocolError, ReadGoAwayErrorCode(goaway));

                conn.Dispose();
            });
        }

        [Test]
        public void ServerPreface_SettingsThenPing_Initializes()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var duplex = new TestDuplexStream();
                var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443,
                    new Http2Options(), new StreamingOptions());
                var serverCodec = new Http2FrameCodec(duplex.ServerStream);

                var initTask = conn.InitializeAsync(cts.Token);
                await ReadClientPrefaceAsync(duplex.ServerStream, serverCodec, cts.Token);

                // Once the SETTINGS preface arrived, any frame may follow, even before the ACK.
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, cts.Token);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Ping,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = new byte[8],
                    Length = 8
                }, cts.Token);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, cts.Token);

                await initTask;
                var pingAck = await ReadMatchingFrameAsync(serverCodec,
                    f => f.Type == Http2FrameType.Ping && (f.Flags & Http2FrameFlags.Ack) != 0, 5000);
                Assert.AreEqual(8, pingAck.Length);
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            });
        }

        private static async Task ReadClientPrefaceAsync(Stream serverStream, Http2FrameCodec serverCodec, CancellationToken ct)
        {
            var preface = new byte[24];
            int read = 0;
            while (read < 24)
            {
                int n = await serverStream.ReadAsync(preface, read, 24 - read, ct);
                if (n == 0) throw new IOException("Unexpected end of stream");
                read += n;
            }

            await serverCodec.ReadFrameAsync(16384, ct); // client SETTINGS
        }

        private static async Task WritePushPromiseAsync(
            Http2FrameCodec codec, HpackEncoder encoder, int streamId, int promisedStreamId, CancellationToken ct)
        {
            var block = encoder.Encode(new List<(string, string)>
            {
                (":method", "GET"),
                (":scheme", "https"),
                (":authority", "test.example.com"),
                (":path", "/pushed")
            }).ToArray();
            var prefix = new byte[]
            {
                (byte)(promisedStreamId >> 24), (byte)(promisedStreamId >> 16),
                (byte)(promisedStreamId >> 8), (byte)promisedStreamId
            };
            await WriteFieldBlockAsync(codec, Http2FrameType.PushPromise, streamId, prefix, block,
                endStream: false, split: false, ct);
        }

        private Http2Frame BuildFrameOnStream(IdleFrameKind kind, int streamId)
        {
            switch (kind)
            {
                case IdleFrameKind.Data:
                    return new Http2Frame
                    {
                        Type = Http2FrameType.Data,
                        Flags = Http2FrameFlags.EndStream,
                        StreamId = streamId,
                        Payload = new byte[] { 0x78 },
                        Length = 1
                    };
                case IdleFrameKind.Headers:
                    return BuildResponseHeadersFrame(streamId, 200, endStream: true);
                case IdleFrameKind.RstStream:
                    return new Http2Frame
                    {
                        Type = Http2FrameType.RstStream,
                        Flags = Http2FrameFlags.None,
                        StreamId = streamId,
                        Payload = new byte[] { 0, 0, 0, (byte)Http2ErrorCode.Cancel },
                        Length = 4
                    };
                case IdleFrameKind.WindowUpdate:
                    return BuildWindowUpdateFrameOn(streamId, 1);
                default:
                    // PUSH_PROMISE on an idle associated stream, promising a legal id (2). Static-table-only
                    // field block, so the client's decoder state is irrelevant.
                    var block = new HpackEncoder().Encode(new List<(string, string)>
                    {
                        (":method", "GET"), (":scheme", "https"), (":path", "/")
                    }).ToArray();
                    var payload = new byte[4 + block.Length];
                    payload[3] = 2;
                    Array.Copy(block, 0, payload, 4, block.Length);
                    return new Http2Frame
                    {
                        Type = Http2FrameType.PushPromise,
                        Flags = Http2FrameFlags.EndHeaders,
                        StreamId = streamId,
                        Payload = payload,
                        Length = payload.Length
                    };
            }
        }

        private static Http2Frame BuildWindowUpdateFrameOn(int streamId, int increment) => new Http2Frame
        {
            Type = Http2FrameType.WindowUpdate,
            Flags = Http2FrameFlags.None,
            StreamId = streamId,
            Payload = new[]
            {
                (byte)((increment >> 24) & 0x7F), (byte)(increment >> 16), (byte)(increment >> 8), (byte)increment
            },
            Length = 4
        };

        private static Http2Frame BuildPriorityFrame(int streamId) => new Http2Frame
        {
            Type = Http2FrameType.Priority,
            Flags = Http2FrameFlags.None,
            StreamId = streamId,
            Payload = new byte[] { 0, 0, 0, 0, 15 },
            Length = 5
        };

        private static uint ReadGoAwayErrorCode(Http2Frame goaway) =>
            ((uint)goaway.Payload[4] << 24) | ((uint)goaway.Payload[5] << 16)
            | ((uint)goaway.Payload[6] << 8) | goaway.Payload[7];
    }
}
