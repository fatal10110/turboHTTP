using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Tests.Transport;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #226/#345: the WebSocket upgrade-response read, the error-body read and the proxy CONNECT reply
    /// read must end on cancellation or handshake timeout even when the platform ends a pending read
    /// neither on its token nor on Dispose (Mono, IL2CPP). <see cref="AbandonedReadStream"/> models
    /// that read; disposing the stream does not help, only racing the token does.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketHandshakeReadCancellationTests
    {
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromMilliseconds(300);

        // Guard only: the connect must finish long before this, it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

        [Test]
        public void Upgrade_ReadIgnoresTokenAndDispose_CallerCancel_ThrowsOperationCanceled()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream();
                await using var client = new WebSocketClient(new StreamTransport(stream));
                using var cts = new CancellationTokenSource();

                var connect = client.ConnectAsync(Origin, CreateOptions(TimeSpan.FromSeconds(30)), cts.Token);
                await AssertCompletesAsync(stream.ReadPending, "the upgrade response read never started");
                cts.Cancel();

                var ex = await CaptureFailureAsync(connect);
                Assert.IsInstanceOf<OperationCanceledException>(ex, ex.ToString());
                Assert.IsTrue(stream.IsDisposed, "the handshake stream was not disposed");
                stream.EndRead(0);
            }, Guard);
        }

        [Test]
        public void Upgrade_ReadIgnoresTokenAndDispose_HandshakeTimeout_FailsAsTimeout()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream();
                await using var client = new WebSocketClient(new StreamTransport(stream));

                var ex = await CaptureFailureAsync(
                    client.ConnectAsync(Origin, CreateOptions(HandshakeTimeout), CancellationToken.None));

                var wsEx = ex as WebSocketException;
                Assert.IsNotNull(wsEx, "expected a WebSocketException, got " + ex);
                Assert.AreEqual(WebSocketError.ConnectionClosed, wsEx.Error, wsEx.ToString());
                StringAssert.Contains("timed out", wsEx.Message);
                Assert.IsTrue(stream.IsDisposed, "the handshake stream was not disposed");
                stream.EndRead(0);
            }, Guard);
        }

        [Test]
        public void ErrorBody_ReadIgnoresTokenAndDispose_HandshakeTimeout_StatusCodeWins()
        {
            AssertAsync.Run(async () =>
            {
                // The 401 head arrives, then its declared body never does.
                var stream = new AbandonedReadStream(
                    "HTTP/1.1 401 Unauthorized\r\nContent-Length: 64\r\n\r\n");
                await using var client = new WebSocketClient(new StreamTransport(stream));

                var ex = await CaptureFailureAsync(
                    client.ConnectAsync(Origin, CreateOptions(HandshakeTimeout), CancellationToken.None));

                var wsEx = ex as WebSocketException;
                Assert.IsNotNull(wsEx, "expected a WebSocketException, got " + ex);
                Assert.AreEqual(WebSocketError.HandshakeFailed, wsEx.Error, wsEx.ToString());
                stream.EndRead(0);
            }, Guard);
        }

        [Test]
        public void ProxyConnect_ReplyReadIgnoresTokenAndDispose_CancelIsNotATunnelFailure()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream();
                using var cts = new CancellationTokenSource();

                var establish = ProxyTunnelConnector.EstablishAsync(
                    stream,
                    new Uri("wss://origin.test/socket"),
                    new WebSocketProxySettings(new Uri("http://proxy.test:8080")),
                    allowCleartextCredentials: false,
                    cts.Token);
                await AssertCompletesAsync(stream.ReadPending, "the CONNECT reply read never started");
                cts.Cancel();

                var ex = await CaptureFailureAsync(establish);
                Assert.IsInstanceOf<OperationCanceledException>(ex, ex.ToString());
                stream.EndRead(0);
            }, Guard);
        }

        [Test]
        public void ProxyConnect_407RetryReplyReadIgnoresTokenAndDispose_CancelIsNotATunnelFailure()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream(
                    "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                    "Proxy-Authenticate: Basic realm=\"test\"\r\n" +
                    "Content-Length: 0\r\n\r\n");
                using var cts = new CancellationTokenSource();

                var establish = ProxyTunnelConnector.EstablishAsync(
                    stream,
                    new Uri("wss://origin.test/socket"),
                    new WebSocketProxySettings(
                        new Uri("http://proxy.test:8080"),
                        new ProxyCredentials("user", "pass")),
                    allowCleartextCredentials: true,
                    cts.Token);
                await AssertCompletesAsync(stream.ReadPending, "the authenticated CONNECT reply read never started");
                cts.Cancel();

                var ex = await CaptureFailureAsync(establish);
                Assert.IsInstanceOf<OperationCanceledException>(ex, ex.ToString());
                stream.EndRead(0);
            }, Guard);
        }

        [Test]
        public void Upgrade_AbandonedReadFaultsLater_FaultIsObserved()
        {
            using var probe = new UnobservedTaskProbe();
            var marker = new IOException("abandoned-upgrade-read");

            Exception failure = CancelThenFaultUpgradeRead(marker);
            probe.CollectWithControl();

            if (!probe.ControlReported)
            {
                TestRequirements.IgnoreEnvironment("This runtime did not report a known unobserved faulted task.");
                return;
            }

            Assert.IsInstanceOf<OperationCanceledException>(failure);
            Assert.IsFalse(probe.WasReported(marker),
                "The abandoned upgrade read faulted after cancellation and nobody observed it.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception CancelThenFaultUpgradeRead(Exception marker)
        {
            var stream = new AbandonedReadStream();
            Exception failure = null;
            AssertAsync.Run(async () =>
            {
                await using var client = new WebSocketClient(new StreamTransport(stream));
                using var cts = new CancellationTokenSource();
                var connect = client.ConnectAsync(Origin, CreateOptions(TimeSpan.FromSeconds(30)), cts.Token);
                await AssertCompletesAsync(stream.ReadPending, "the upgrade response read never started");
                cts.Cancel();
                failure = await CaptureFailureAsync(connect);
            }, Guard);

            // The platform ends the abandoned read late, with a fault.
            stream.FailRead(marker);
            return failure;
        }

        private static Uri Origin => new Uri("ws://127.0.0.1/abandoned-read");

        private static async Task<Exception> CaptureFailureAsync(Task task)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Guard));
            Assert.AreSame(task, winner, "the handshake outlived its timeout / cancellation");

            try
            {
                await task;
            }
            catch (Exception ex)
            {
                return ex;
            }

            Assert.Fail("the handshake succeeded against a silent peer");
            return null;
        }

        private static async Task AssertCompletesAsync(Task task, string message)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Guard));
            Assert.AreSame(task, winner, message);
        }

        private static WebSocketConnectionOptions CreateOptions(TimeSpan handshakeTimeout)
        {
            return new WebSocketConnectionOptions
            {
                HandshakeTimeout = handshakeTimeout,
                CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                PingInterval = TimeSpan.Zero,
                PongTimeout = TimeSpan.FromMilliseconds(200),
                ReceiveQueueCapacity = 4,
                AllowCleartext = true
            };
        }

        private sealed class StreamTransport : IWebSocketTransport
        {
            private readonly Stream _stream;

            public StreamTransport(Stream stream)
            {
                _stream = stream;
            }

            public Task<Stream> ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct) =>
                Task.FromResult(_stream);

            public void Dispose() => _stream.Dispose();
        }
    }
}
