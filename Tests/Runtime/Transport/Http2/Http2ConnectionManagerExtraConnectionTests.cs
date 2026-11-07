using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
    /// Http2Options.MaxConnectionsPerOrigin (#238): another connection opens only when every connection
    /// of the origin is saturated (peer SETTINGS_MAX_CONCURRENT_STREAMS reached, RFC 9113 Section 5.1.2),
    /// never above the maximum; the default keeps one connection per origin (RFC 9113 Section 9.1).
    /// </summary>
    [TestFixture]
    public class Http2ConnectionManagerExtraConnectionTests
    {
        private const string Host = "api.example.com";
        private const int Port = 443;

        [Test]
        public void MaxConnectionsTwo_SaturatedConnection_OpensSecond_NeverAboveMax()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var manager = CreateManager(maxConnectionsPerOrigin: 2);

                var first = await CreateConnectionAsync(manager, cts.Token);
                Assert.AreSame(first.Connection, manager.GetIfExists(Host, Port), "a free slot is reused");

                var firstStream = await OpenStreamAsync(first, cts.Token);

                // The only slot is taken: dial another connection instead of queueing.
                Assert.IsNull(manager.GetIfExists(Host, Port));
                Assert.IsTrue(manager.HasConnection(Host, Port), "the saturated connection still exists");

                var second = await CreateConnectionAsync(manager, cts.Token);
                Assert.AreNotSame(first.Connection, second.Connection);
                Assert.AreEqual(2, GetConnections(manager, Host + ":" + Port).Length);

                var secondStream = await OpenStreamAsync(second, cts.Token);

                // At the maximum: the existing connections are used (the request queues for a slot).
                var atMax = manager.GetIfExists(Host, Port);
                Assert.IsNotNull(atMax);
                Assert.IsTrue(atMax == first.Connection || atMax == second.Connection);

                // A dial that raced in anyway is not published: its stream is closed, an existing
                // connection is returned.
                var spare = new TestDuplexStream();
                var reused = await manager.GetOrCreateAsync(Host, Port, spare.ClientStream, cts.Token);
                Assert.IsTrue(reused == first.Connection || reused == second.Connection);
                Assert.AreEqual(2, GetConnections(manager, Host + ":" + Port).Length);
                Assert.AreEqual(0, await spare.ServerStream.ReadAsync(new byte[1], 0, 1, cts.Token),
                    "the extra dial must be closed unused");

                await CompleteAsync(first, firstStream, cts.Token);
                await CompleteAsync(second, secondStream, cts.Token);

                // With a slot free again the first connection is preferred.
                while (!first.Connection.HasFreeStreamSlot)
                    await Task.Delay(1, cts.Token);
                Assert.AreSame(first.Connection, manager.GetIfExists(Host, Port));
            });
        }

        [Test]
        public void MaxConnectionsTwo_ConcurrentDialsWhileSaturated_PublishOneExtraConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var manager = CreateManager(maxConnectionsPerOrigin: 2);

                var first = await CreateConnectionAsync(manager, cts.Token);
                var firstStream = await OpenStreamAsync(first, cts.Token);
                Assert.IsNull(manager.GetIfExists(Host, Port));

                // Two requests both saw saturation and dialed. The second waits for the per-key lock, then
                // finds the new connection with a free slot and closes its own stream.
                var duplexA = new TestDuplexStream();
                var duplexB = new TestDuplexStream();
                var serverA = CompleteHandshakeAsync(duplexA.ServerStream, cts.Token);
                var taskA = manager.GetOrCreateAsync(Host, Port, duplexA.ClientStream, cts.Token).AsTask();
                var taskB = manager.GetOrCreateAsync(Host, Port, duplexB.ClientStream, cts.Token).AsTask();
                await serverA;
                var connectionA = await taskA;
                var connectionB = await taskB;

                Assert.AreNotSame(first.Connection, connectionA);
                Assert.AreSame(connectionA, connectionB);
                Assert.AreEqual(2, GetConnections(manager, Host + ":" + Port).Length);
                Assert.AreEqual(0, await duplexB.ServerStream.ReadAsync(new byte[1], 0, 1, cts.Token));

                await CompleteAsync(first, firstStream, cts.Token);
            });
        }

        [Test]
        public void DefaultOptions_SaturatedConnection_KeepsOneConnectionPerOrigin()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var manager = new Http2ConnectionManager(
                    new Http2Options { PingInterval = TimeSpan.Zero },
                    new StreamingOptions());

                var first = await CreateConnectionAsync(manager, cts.Token);
                var firstStream = await OpenStreamAsync(first, cts.Token);

                // Saturated, but one connection per origin: requests queue on it.
                Assert.AreSame(first.Connection, manager.GetIfExists(Host, Port));

                var spare = new TestDuplexStream();
                var reused = await manager.GetOrCreateAsync(Host, Port, spare.ClientStream, cts.Token);
                Assert.AreSame(first.Connection, reused);
                Assert.AreEqual(1, GetConnections(manager, Host + ":" + Port).Length);

                await CompleteAsync(first, firstStream, cts.Token);
            });
        }

        [Test]
        public void MaxConnectionsPerOrigin_DefaultIsOne_RejectsBelowOne_CopiedByClone()
        {
            var options = new Http2Options();
            Assert.AreEqual(1, options.MaxConnectionsPerOrigin);
            Assert.IsTrue(options.IsDefault());
            Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxConnectionsPerOrigin = 0);

            options.MaxConnectionsPerOrigin = 3;
            Assert.IsFalse(options.IsDefault());
            Assert.AreEqual(3, options.Clone().MaxConnectionsPerOrigin);
        }

        private sealed class ServerConnection
        {
            public Http2Connection Connection;
            public Http2FrameCodec Codec;
        }

        private static Http2ConnectionManager CreateManager(int maxConnectionsPerOrigin)
        {
            return new Http2ConnectionManager(
                new Http2Options
                {
                    MaxConnectionsPerOrigin = maxConnectionsPerOrigin,
                    PingInterval = TimeSpan.Zero
                },
                new StreamingOptions());
        }

        private static async Task<ServerConnection> CreateConnectionAsync(
            Http2ConnectionManager manager,
            CancellationToken ct)
        {
            var duplex = new TestDuplexStream();
            var serverTask = CompleteHandshakeAsync(duplex.ServerStream, ct);
            var connection = await manager.GetOrCreateAsync(Host, Port, duplex.ClientStream, ct);
            return new ServerConnection { Connection = connection, Codec = await serverTask };
        }

        /// <summary>Server handshake whose SETTINGS allow one concurrent stream.</summary>
        private static async Task<Http2FrameCodec> CompleteHandshakeAsync(Stream serverStream, CancellationToken ct)
        {
            var preface = new byte[Http2Constants.ConnectionPreface.Length];
            int read = 0;
            while (read < preface.Length)
            {
                int n = await serverStream.ReadAsync(preface, read, preface.Length - read, ct);
                if (n == 0)
                    throw new IOException("closed before the preface");
                read += n;
            }

            var codec = new Http2FrameCodec(serverStream);
            await codec.ReadFrameAsync(16384, ct);
            var settings = new byte[] { 0x00, 0x03, 0x00, 0x00, 0x00, 0x01 }; // MAX_CONCURRENT_STREAMS = 1
            await codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Settings,
                StreamId = 0,
                Payload = settings,
                Length = settings.Length
            }, ct);
            await codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Settings,
                Flags = Http2FrameFlags.Ack,
                StreamId = 0,
                Payload = Array.Empty<byte>(),
                Length = 0
            }, ct);

            // The client's ACK of our SETTINGS: MAX_CONCURRENT_STREAMS is applied from here on.
            while (true)
            {
                var frame = await codec.ReadFrameAsync(16384, ct);
                if (frame.Type == Http2FrameType.Settings && frame.HasFlag(Http2FrameFlags.Ack))
                    return codec;
            }
        }

        /// <summary>Starts a request and returns once its HEADERS reached the server (the slot is taken).</summary>
        private static async Task<(Task<UHttpResponse> Response, int StreamId)> OpenStreamAsync(
            ServerConnection server,
            CancellationToken ct)
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri($"https://{Host}/slow"));
            var response = server.Connection.SendRequestAsync(request, new RequestContext(request), ct).AsTask();
            while (true)
            {
                var frame = await server.Codec.ReadFrameAsync(16384, ct);
                if (frame.Type == Http2FrameType.Headers)
                {
                    Assert.IsFalse(server.Connection.HasFreeStreamSlot);
                    return (response, frame.StreamId);
                }
            }
        }

        private static async Task CompleteAsync(
            ServerConnection server,
            (Task<UHttpResponse> Response, int StreamId) open,
            CancellationToken ct)
        {
            var headerBlock = new HpackEncoder().Encode(new List<(string, string)> { (":status", "200") }).ToArray();
            await server.Codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Headers,
                Flags = Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
                StreamId = open.StreamId,
                Payload = headerBlock,
                Length = headerBlock.Length
            }, ct);

            using var response = await open.Response;
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        private static Http2Connection[] GetConnections(Http2ConnectionManager manager, string key)
        {
            var field = typeof(Http2ConnectionManager).GetField(
                "_connections",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field);

            var connections = (ConcurrentDictionary<string, Http2Connection[]>)field.GetValue(manager);
            return connections.TryGetValue(key, out var set) ? set : Array.Empty<Http2Connection>();
        }
    }
}
