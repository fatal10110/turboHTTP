using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketConnectionTests
    {
        [Test]
        public void StateMachine_ConnectThenClose_TransitionsDeterministically()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var connection = new WebSocketConnection();

                var transitions = new List<WebSocketState>();
                connection.StateChanged += (_, state) => transitions.Add(state);

                await connection.ConnectAsync(
                    server.CreateUri("/connection-state"),
                    transport,
                    CreateOptions(),
                    CancellationToken.None).ConfigureAwait(false);

                await connection.CloseAsync(
                    WebSocketCloseCode.NormalClosure,
                    "done",
                    CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(WebSocketState.Closed, connection.State);
                CollectionAssert.Contains(transitions, WebSocketState.Connecting);
                CollectionAssert.Contains(transitions, WebSocketState.Open);
                CollectionAssert.Contains(transitions, WebSocketState.Closing);
                CollectionAssert.Contains(transitions, WebSocketState.Closed);
            });
        }

        [Test]
        public void CloseAsync_RejectsReservedCloseCodes1005And1006()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var connection = new WebSocketConnection();

                await connection.ConnectAsync(
                    server.CreateUri("/reserved-close"),
                    transport,
                    CreateOptions(),
                    CancellationToken.None).ConfigureAwait(false);

                var ex1005 = AssertAsync.ThrowsAsync<WebSocketException>(async () =>
                    await connection.CloseAsync(WebSocketCloseCode.NoStatusReceived, string.Empty, CancellationToken.None)
                        .ConfigureAwait(false));
                Assert.AreEqual(WebSocketError.InvalidCloseCode, ex1005.Error);

                var ex1006 = AssertAsync.ThrowsAsync<WebSocketException>(async () =>
                    await connection.CloseAsync(WebSocketCloseCode.AbnormalClosure, string.Empty, CancellationToken.None)
                        .ConfigureAwait(false));
                Assert.AreEqual(WebSocketError.InvalidCloseCode, ex1006.Error);
            });
        }

        [Test]
        public void InvalidUtf8FromServer_ClosesWithInvalidPayloadStatus()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(
                    new WebSocketTestServerOptions
                    {
                        SendInvalidUtf8TextOnFirstMessage = true
                    });

                using var transport = new TestTcpWebSocketTransport();
                await using var connection = new WebSocketConnection();

                await connection.ConnectAsync(
                    server.CreateUri("/invalid-utf8"),
                    transport,
                    CreateOptions(),
                    CancellationToken.None).ConfigureAwait(false);

                await connection.SendTextAsync("trigger-invalid-utf8", CancellationToken.None).ConfigureAwait(false);

                AssertAsync.ThrowsAsync<WebSocketException>(async () =>
                {
                    using var _ = await connection.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                });

                await Task.Delay(50).ConfigureAwait(false);

                Assert.AreEqual(WebSocketState.Closed, connection.State);
                Assert.IsTrue(connection.CloseStatus.HasValue);
                Assert.AreEqual(WebSocketCloseCode.InvalidPayload, connection.CloseStatus.Value.Code);
            });
        }

        [Test]
        public void Abort_BeforeConnect_TransitionsToClosed()
        {
            var connection = new WebSocketConnection();
            try
            {
                connection.Abort();
                Assert.AreEqual(WebSocketState.Closed, connection.State);
            }
            finally
            {
                connection.Dispose();
            }
        }

        [Test]
        public void ConnectAsync_RequiredExtensionMissing_FailsWithMandatoryExtension()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var connection = new WebSocketConnection();

                var options = CreateOptions().WithRequiredCompression();

                var ex = AssertAsync.ThrowsAsync<WebSocketException>(async () =>
                    await connection.ConnectAsync(
                        server.CreateUri("/required-extension-missing"),
                        transport,
                        options,
                        CancellationToken.None).ConfigureAwait(false));

                Assert.AreEqual(WebSocketError.ExtensionNegotiationFailed, ex.Error);
                Assert.AreEqual(WebSocketCloseCode.MandatoryExtension, ex.CloseCode);
                Assert.AreEqual(WebSocketState.Closed, connection.State);
                Assert.IsTrue(connection.CloseStatus.HasValue);
                Assert.AreEqual(WebSocketCloseCode.MandatoryExtension, connection.CloseStatus.Value.Code);
            });
        }

        [Test]
        public void ConnectAsync_OptionalExtensionNegotiationFailure_FallsBackToNoExtensions()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(
                    new WebSocketTestServerOptions
                    {
                        HandshakeExtensionsHeader = "x-unknown-extension"
                    });

                using var transport = new TestTcpWebSocketTransport();
                await using var connection = new WebSocketConnection();

                var options = CreateOptions().WithCompression();
                await connection.ConnectAsync(
                    server.CreateUri("/optional-extension-fallback"),
                    transport,
                    options,
                    CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(WebSocketState.Open, connection.State);
                Assert.AreEqual(0, connection.NegotiatedExtensions.Count);

                await connection.CloseAsync(
                    WebSocketCloseCode.NormalClosure,
                    "done",
                    CancellationToken.None).ConfigureAwait(false);
            });
        }

        [Test]
        public void ConnectAsync_RequiredExtensionNegotiated_Succeeds()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(
                    new WebSocketTestServerOptions
                    {
                        HandshakeExtensionsHeader =
                            "permessage-deflate; server_no_context_takeover; client_no_context_takeover"
                    });

                using var transport = new TestTcpWebSocketTransport();
                await using var connection = new WebSocketConnection();

                var options = CreateOptions().WithRequiredCompression();
                await connection.ConnectAsync(
                    server.CreateUri("/required-extension-negotiated"),
                    transport,
                    options,
                    CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(WebSocketState.Open, connection.State);
                Assert.AreEqual(1, connection.NegotiatedExtensions.Count);
                Assert.AreEqual("permessage-deflate", connection.NegotiatedExtensions[0]);

                await connection.CloseAsync(
                    WebSocketCloseCode.NormalClosure,
                    "done",
                    CancellationToken.None).ConfigureAwait(false);
            });
        }

        [Test]
        public void Compression_EndToEndRoundTrip_WithNegotiatedPerMessageDeflate()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(
                    new WebSocketTestServerOptions
                    {
                        EnablePerMessageDeflate = true
                    });

                using var transport = new TestTcpWebSocketTransport();
                await using var connection = new WebSocketConnection();

                var options = CreateOptions().WithRequiredCompression();
                options.FragmentationThreshold = 128;

                string payload = new string('a', 4096) + "|turbohttp-compression-e2e|";

                await connection.ConnectAsync(
                    server.CreateUri("/compression-e2e"),
                    transport,
                    options,
                    CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(1, connection.NegotiatedExtensions.Count);
                Assert.AreEqual("permessage-deflate", connection.NegotiatedExtensions[0]);

                await connection.SendTextAsync(payload, CancellationToken.None).ConfigureAwait(false);
                using var message = await connection.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(payload, message.Text);

                var metrics = connection.Metrics;
                Assert.Greater(metrics.UncompressedBytesSent, 0);
                Assert.Greater(metrics.CompressedBytesSent, 0);
                Assert.Greater(metrics.CompressionRatio, 1.0d);

                await connection.CloseAsync(
                    WebSocketCloseCode.NormalClosure,
                    "done",
                    CancellationToken.None).ConfigureAwait(false);
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
    }
}
