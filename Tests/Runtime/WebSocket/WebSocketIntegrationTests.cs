using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketIntegrationTests
    {
        [Test]
        public void ConnectAsync_Succeeds_AgainstInProcessServer()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                using var client = new WebSocketClient(transport);

                var options = CreateFastOptions();
                await client.ConnectAsync(server.CreateUri("/connect"), options, CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(WebSocketState.Open, client.State);

                await client.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);

                Assert.AreEqual(WebSocketState.Closed, client.State);
            });
        }

        [Test]
        public void ConnectAsync_HandshakeRejected_ThrowsHandshakeFailed()
        {
            using var server = new WebSocketTestServer(
                new WebSocketTestServerOptions
                {
                    RejectHandshakeStatusCode = 403,
                    RejectHandshakeReason = "Forbidden"
                });

            using var transport = new TestTcpWebSocketTransport();
            using var client = new WebSocketClient(transport);

            var ex = AssertAsync.ThrowsAsync<WebSocketException>(async () =>
                await client.ConnectAsync(server.CreateUri("/reject"), CreateFastOptions(), CancellationToken.None)
                    .ConfigureAwait(false));

            Assert.AreEqual(WebSocketError.HandshakeFailed, ex.Error);
        }

        [Test]
        public void SendReceive_TextAndBinary_EchoRoundTrip()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/echo"), CreateFastOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                await client.SendAsync("hello websocket", CancellationToken.None).ConfigureAwait(false);
                using (var text = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    Assert.IsTrue(text.IsText);
                    Assert.AreEqual("hello websocket", text.Text);
                }

                var payload = new byte[] { 1, 2, 3, 4, 5 };
                await client.SendAsync(payload, CancellationToken.None).ConfigureAwait(false);
                using (var binary = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    Assert.IsTrue(binary.IsBinary);
                    CollectionAssert.AreEqual(payload, binary.Data.ToArray());
                }

                await client.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
            });
        }

        [Test]
        public void ServerInitiatedClose_RaisesOnClosed_AndReceiveFails()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(
                    new WebSocketTestServerOptions
                    {
                        CloseAfterMessageCount = 1
                    });

                using var transport = new TestTcpWebSocketTransport();
                using var client = new WebSocketClient(transport);

                var closedTcs = new TaskCompletionSource<WebSocketCloseCode>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                client.OnClosed += (code, reason) => closedTcs.TrySetResult(code);

                await client.ConnectAsync(server.CreateUri("/server-close"), CreateFastOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                await client.SendAsync("close-me", CancellationToken.None).ConfigureAwait(false);
                using (var echoed = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    Assert.AreEqual("close-me", echoed.Text);
                }

                var closedCode = await WaitWithTimeout(
                    closedTcs.Task,
                    TimeSpan.FromSeconds(3),
                    "Timed out waiting for OnClosed callback.").ConfigureAwait(false);

                Assert.AreEqual(WebSocketCloseCode.NormalClosure, closedCode);

                var ex = AssertAsync.ThrowsAsync<WebSocketException>(async () =>
                {
                    using var _ = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                });
                Assert.AreEqual(WebSocketError.ConnectionClosed, ex.Error);
            });
        }

        [Test]
        public void ResilientClient_Reconnects_AfterUnexpectedDisconnect()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(
                    new WebSocketTestServerOptions
                    {
                        DisconnectFirstConnectionAfterHandshake = true
                    });

                using var transport = new TestTcpWebSocketTransport();
                await using var client = new ResilientWebSocketClient(transport);

                var reconnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnReconnected += () => reconnectedTcs.TrySetResult(true);

                var options = CreateFastOptions();
                options.WithReconnection(new WebSocketReconnectPolicy(
                    maxRetries: 5,
                    initialDelay: TimeSpan.FromMilliseconds(25),
                    maxDelay: TimeSpan.FromMilliseconds(100),
                    backoffMultiplier: 2.0,
                    jitterFactor: 0.0));

                await client.ConnectAsync(server.CreateUri("/reconnect"), options, CancellationToken.None)
                    .ConfigureAwait(false);

                await WaitWithTimeout(
                    reconnectedTcs.Task,
                    TimeSpan.FromSeconds(3),
                    "Timed out waiting for reconnect event.").ConfigureAwait(false);

                await client.SendAsync("after-reconnect", CancellationToken.None).ConfigureAwait(false);
                using var message = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("after-reconnect", message.Text);
            });
        }

        [Test]
        public void ResilientClient_ExhaustsRetries_AndCloses()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(
                    new WebSocketTestServerOptions
                    {
                        DisconnectFirstConnectionAfterHandshake = true,
                        RejectHandshakeStatusCode = 503,
                        RejectHandshakeReason = "Service Unavailable",
                        RejectHandshakeAfterConnectionCount = 1
                    });

                using var transport = new TestTcpWebSocketTransport();
                await using var client = new ResilientWebSocketClient(transport);

                int reconnectEvents = 0;
                client.OnReconnecting += (attempt, delay) => Interlocked.Increment(ref reconnectEvents);

                var closedTcs = new TaskCompletionSource<WebSocketCloseCode>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnClosed += (code, reason) => closedTcs.TrySetResult(code);

                var options = CreateFastOptions();
                options.WithReconnection(new WebSocketReconnectPolicy(
                    maxRetries: 2,
                    initialDelay: TimeSpan.FromMilliseconds(20),
                    maxDelay: TimeSpan.FromMilliseconds(50),
                    backoffMultiplier: 1.5,
                    jitterFactor: 0.0));

                await client.ConnectAsync(server.CreateUri("/retries"), options, CancellationToken.None)
                    .ConfigureAwait(false);

                var code = await WaitWithTimeout(
                    closedTcs.Task,
                    TimeSpan.FromSeconds(3),
                    "Timed out waiting for terminal close after reconnect exhaustion.").ConfigureAwait(false);

                Assert.AreEqual(WebSocketCloseCode.AbnormalClosure, code);
                Assert.GreaterOrEqual(Volatile.Read(ref reconnectEvents), 2);
            });
        }

        private static WebSocketConnectionOptions CreateFastOptions()
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

        private static async Task<T> WaitWithTimeout<T>(Task<T> task, TimeSpan timeout, string errorMessage)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, task))
                throw new TimeoutException(errorMessage);

            return await task.ConfigureAwait(false);
        }
    }
}
