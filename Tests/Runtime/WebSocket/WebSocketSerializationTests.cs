using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketSerializationTests
    {
        [Test]
        public void JsonSerializer_RoundTrip_WorksForComplexObject()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/serialization-json"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                var serializer = new JsonWebSocketSerializer<ChatEnvelope>();
                var outbound = new ChatEnvelope
                {
                    Room = "lobby",
                    User = "alice",
                    Sequence = 42,
                    Tags = new List<string> { "a", "b", "c" }
                };

                await client.SendAsync(outbound, serializer, CancellationToken.None).ConfigureAwait(false);
                var inbound = await client.ReceiveAsync(serializer, CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(outbound.Room, inbound.Room);
                Assert.AreEqual(outbound.User, inbound.User);
                Assert.AreEqual(outbound.Sequence, inbound.Sequence);
                CollectionAssert.AreEqual(outbound.Tags, inbound.Tags);
            });
        }

        [Test]
        public void RawStringSerializer_Passthrough_Works()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/serialization-raw"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                await client.SendAsync("plain-text", RawStringSerializer.Instance, CancellationToken.None)
                    .ConfigureAwait(false);

                string message = await client.ReceiveAsync(RawStringSerializer.Instance, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.AreEqual("plain-text", message);
            });
        }

        [Test]
        public void JsonDeserializer_Failure_IsWrappedAsSerializationFailed()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/serialization-error"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                await client.SendAsync("not-json", CancellationToken.None).ConfigureAwait(false);
                var serializer = new JsonWebSocketSerializer<ChatEnvelope>();

                var ex = AssertAsync.ThrowsAsync<WebSocketException>(async () =>
                    _ = await client.ReceiveAsync(serializer, CancellationToken.None).ConfigureAwait(false));

                Assert.AreEqual(WebSocketError.SerializationFailed, ex.Error);
            });
        }

        [Test]
        public void TypedReceiveAllAsync_StreamsTypedValues()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/serialization-stream"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                await using var enumerator = client
                    .ReceiveAllAsync(RawStringSerializer.Instance, CancellationToken.None)
                    .GetAsyncEnumerator();

                await client.SendAsync("one", CancellationToken.None).ConfigureAwait(false);
                await client.SendAsync("two", CancellationToken.None).ConfigureAwait(false);

                Assert.IsTrue(await enumerator.MoveNextAsync().AsTask().ConfigureAwait(false));
                Assert.AreEqual("one", enumerator.Current);

                Assert.IsTrue(await enumerator.MoveNextAsync().AsTask().ConfigureAwait(false));
                Assert.AreEqual("two", enumerator.Current);
            });
        }

        private static WebSocketConnectionOptions CreateOptions()
        {
            return new WebSocketConnectionOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(2),
                CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                PingInterval = TimeSpan.Zero,
                PongTimeout = TimeSpan.FromMilliseconds(200),
                ReceiveQueueCapacity = 32
            };
        }

        private sealed class ChatEnvelope
        {
            public string Room { get; set; }
            public string User { get; set; }
            public int Sequence { get; set; }
            public List<string> Tags { get; set; }
        }
    }
}
