using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport;
using TurboHTTP.Tests.Transport.Tls;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #1139: connect failures are classified by cause, as the HTTP transport classifies them: a rejected
    /// server certificate is a non-retryable CertificateError the resilient client does not reconnect into,
    /// an expired handshake is a retryable Timeout, a non-ws scheme an InvalidRequest, a resolver failure a
    /// DNS failure; a handshake rejection carries its status in UHttpError.StatusCode, and the receive path
    /// keeps the specific protocol error.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketConnectErrorClassificationTests
    {
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        [Test]
        public void Wss_UntrustedServerCertificate_IsCertificateError_NotRetryable()
        {
            AssertAsync.Run(async () =>
            {
                using var certificate = LocalTlsTestServer.TryCreateLocalhostCertificate();
                if (certificate == null)
                    TestRequirements.IgnoreEnvironment("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");

                using var server = new WebSocketTestServer(new WebSocketTestServerOptions
                {
                    ServerCertificate = certificate
                });
                using var transport = new RawSocketWebSocketTransport();
                var options = CreateOptions(TimeSpan.FromSeconds(10));
                // No trust override: the self-signed test certificate is not anchored anywhere.
                options.TlsBackend = TlsBackend.SslStream;

                var error = await ConnectExpectingFailureAsync(
                    server.CreateSecureUri("/untrusted"), transport, options).ConfigureAwait(false);

                Assert.AreEqual(WebSocketError.CertificateError, error.Error, error.ToString());
                Assert.AreEqual(UHttpErrorType.CertificateError, error.HttpError.Type);
                Assert.IsInstanceOf<AuthenticationException>(error.InnerException, error.ToString());
                Assert.IsFalse(error.IsRetryable());
                Assert.IsFalse(error.HttpError.IsRetryable());
                Assert.IsTrue(ResilientWebSocketClient.IsPermanentConnectFailure(error));
            });
        }

        [Test]
        public void Handshake_ServerNeverAnswers_IsTimeout_Retryable()
        {
            AssertAsync.Run(async () =>
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var accept = listener.AcceptTcpClientAsync();
                try
                {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    using var transport = new RawSocketWebSocketTransport();
                    var options = CreateOptions(TimeSpan.FromMilliseconds(300));
                    options.AllowCleartext = true;

                    var error = await ConnectExpectingFailureAsync(
                        new Uri("ws://127.0.0.1:" + port + "/silent"), transport, options).ConfigureAwait(false);

                    Assert.AreEqual(WebSocketError.Timeout, error.Error, error.ToString());
                    Assert.AreEqual(UHttpErrorType.Timeout, error.HttpError.Type);
                    StringAssert.Contains("timed out", error.Message);
                    Assert.IsTrue(error.IsRetryable());
                    Assert.IsFalse(ResilientWebSocketClient.IsPermanentConnectFailure(error));
                }
                finally
                {
                    listener.Stop();
                    try { (await accept.ConfigureAwait(false)).Close(); } catch { }
                }
            });
        }

        [Test]
        public void Connect_NonWebSocketScheme_IsInvalidRequest_NotRetryable()
        {
            AssertAsync.Run(async () =>
            {
                using var transport = new RawSocketWebSocketTransport();

                var error = await ConnectExpectingFailureAsync(
                    new Uri("https://127.0.0.1:1/not-ws"), transport, CreateOptions(Guard)).ConfigureAwait(false);

                Assert.AreEqual(WebSocketError.InvalidRequest, error.Error, error.ToString());
                Assert.AreEqual(UHttpErrorType.InvalidRequest, error.HttpError.Type);
                Assert.IsInstanceOf<ArgumentException>(error.InnerException, error.ToString());
                Assert.IsFalse(error.IsRetryable());
                Assert.IsTrue(ResilientWebSocketClient.IsPermanentConnectFailure(error));
            });
        }

        [Test]
        public void Connect_HostNotFound_IsDnsResolutionFailed_Retryable()
        {
            AssertAsync.Run(async () =>
            {
                using var transport = new RawSocketWebSocketTransport(
                    TlsBackend.Auto,
                    null,
                    host => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)));

                var error = await ConnectExpectingFailureAsync(
                    new Uri("wss://unresolvable.invalid/"), transport, CreateOptions(Guard)).ConfigureAwait(false);

                Assert.AreEqual(WebSocketError.DnsResolutionFailed, error.Error, error.ToString());
                Assert.AreEqual(UHttpErrorType.NetworkError, error.HttpError.Type);
                Assert.IsTrue(error.IsRetryable());
                Assert.IsFalse(ResilientWebSocketClient.IsPermanentConnectFailure(error));
            });
        }

        [Test]
        public void Connect_DnsTimeout_IsDnsResolutionFailed()
        {
            AssertAsync.Run(async () =>
            {
                var never = new TaskCompletionSource<IPAddress[]>();
                using var transport = new RawSocketWebSocketTransport(TlsBackend.Auto, null, host => never.Task);
                var options = CreateOptions(Guard);
                options.DnsTimeoutMs = 50;

                var error = await ConnectExpectingFailureAsync(
                    new Uri("wss://slow-resolver.invalid/"), transport, options).ConfigureAwait(false);

                Assert.AreEqual(WebSocketError.DnsResolutionFailed, error.Error, error.ToString());
                Assert.IsInstanceOf<TimeoutException>(error.InnerException, error.ToString());
                Assert.IsTrue(error.IsRetryable());
                never.TrySetResult(Array.Empty<IPAddress>());
            });
        }

        [Test]
        public void HandshakeRejected_CarriesStatusInUHttpError()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(new WebSocketTestServerOptions
                {
                    RejectHandshakeStatusCode = 403,
                    RejectHandshakeReason = "Forbidden"
                });
                using var transport = new TestTcpWebSocketTransport();

                var error = await ConnectExpectingFailureAsync(
                    server.CreateUri("/forbidden"), transport, CreateOptions(Guard)).ConfigureAwait(false);

                Assert.AreEqual(WebSocketError.HandshakeFailed, error.Error, error.ToString());
                Assert.AreEqual(403, error.HandshakeStatusCode);
                Assert.AreEqual(HttpStatusCode.Forbidden, error.HttpError.StatusCode);
                Assert.AreEqual(UHttpErrorType.HttpError, error.HttpError.Type);
                Assert.IsFalse(error.IsRetryable());
            });
        }

        [Test]
        public void ReceivePath_ReservedOpcode_KeepsSpecificError()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(
                        new Uri("ws://localhost/protocol"), peer, CreateOptions(Guard), CancellationToken.None)
                        .ConfigureAwait(false);

                    // Reserved data opcode 0x3 (RFC 6455 section 5.2).
                    await peer.SendRawAsync(new byte[] { 0x83, 0x00 }).ConfigureAwait(false);
                    await WaitUntilAsync(() => connection.State == WebSocketState.Closed).ConfigureAwait(false);

                    var terminal = connection.TerminalError as WebSocketException;
                    Assert.IsNotNull(terminal, "Expected a WebSocketException, got " + connection.TerminalError);
                    Assert.AreEqual(WebSocketError.ReservedOpcode, terminal.Error);
                    Assert.AreEqual(WebSocketCloseCode.ProtocolError, connection.CloseStatus?.Code);
                    Assert.IsFalse(terminal.IsRetryable());
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [TestCase(WebSocketError.ProxyTunnelFailed, true)]
        [TestCase(WebSocketError.ProxyConnectionFailed, true)]
        [TestCase(WebSocketError.Timeout, true)]
        [TestCase(WebSocketError.DnsResolutionFailed, true)]
        [TestCase(WebSocketError.CertificateError, false)]
        [TestCase(WebSocketError.InvalidRequest, false)]
        [TestCase(WebSocketError.FrameTooLarge, false)]
        public void IsRetryable_MatchesUHttpErrorModel(WebSocketError error, bool expected)
        {
            var ex = new WebSocketException(error, "failure");

            Assert.AreEqual(expected, ex.IsRetryable());
            Assert.AreEqual(expected, ex.HttpError.IsRetryable(), "UHttpError type " + ex.HttpError.Type);
        }

        [TestCase(401, false, true)]
        [TestCase(403, false, true)]
        [TestCase(408, true, false)]
        [TestCase(429, true, false)]
        [TestCase(503, true, false)]
        public void HandshakeStatus_RetryableAndPermanentRulesAgree(int status, bool retryable, bool permanent)
        {
            var ex = new WebSocketException(WebSocketError.HandshakeFailed, "rejected", null, status);

            Assert.AreEqual(retryable, ex.IsRetryable());
            Assert.AreEqual(permanent, ResilientWebSocketClient.IsPermanentConnectFailure(ex));
            Assert.AreEqual((HttpStatusCode)status, ex.HttpError.StatusCode);
        }

        [Test]
        public void ResilientClient_StopsReconnecting_OnCertificateFailure()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(new WebSocketTestServerOptions
                {
                    DisconnectFirstConnectionAfterHandshake = true
                });
                using var transport = new CertificateFailingAfterFirstConnectTransport();
                await using var client = new ResilientWebSocketClient(transport);

                int reconnectEvents = 0;
                client.OnReconnecting += (attempt, delay) => Interlocked.Increment(ref reconnectEvents);
                var errors = new List<WebSocketException>();
                client.OnError += error =>
                {
                    lock (errors)
                        errors.Add(error);
                };
                var closed = new TaskCompletionSource<WebSocketCloseCode>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnClosed += (code, reason) => closed.TrySetResult(code);

                var options = CreateOptions(TimeSpan.FromSeconds(30));
                options.WithReconnection(new WebSocketReconnectPolicy(
                    maxRetries: 5,
                    initialDelay: TimeSpan.FromMilliseconds(10),
                    maxDelay: TimeSpan.FromMilliseconds(20),
                    backoffMultiplier: 1.0,
                    jitterFactor: 0.0));

                await client.ConnectAsync(server.CreateUri("/cert"), options, CancellationToken.None)
                    .ConfigureAwait(false);

                await WebSocketReceiveBackpressureTests.WaitWithTimeout(closed.Task, Guard,
                    "The resilient client did not close after the certificate failure.").ConfigureAwait(false);

                Assert.AreEqual(1, Volatile.Read(ref reconnectEvents), "Reconnected again after a certificate failure.");
                Assert.AreEqual(2, transport.ConnectCalls);
                lock (errors)
                {
                    Assert.IsTrue(
                        errors.Exists(e => e.Error == WebSocketError.CertificateError &&
                                           e.HttpError.Type == UHttpErrorType.CertificateError),
                        "OnError did not report the certificate failure.");
                }
            });
        }

        private static async Task<WebSocketException> ConnectExpectingFailureAsync(
            Uri uri,
            IWebSocketTransport transport,
            WebSocketConnectionOptions options)
        {
            var connection = new WebSocketConnection();
            try
            {
                using var guard = new CancellationTokenSource(Guard + Guard);
                Exception failure = null;
                try
                {
                    await connection.ConnectAsync(uri, transport, options, guard.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Assert.IsNotNull(failure, "ConnectAsync succeeded.");
                Assert.IsFalse(guard.IsCancellationRequested, "The guard fired: " + failure);
                Assert.IsInstanceOf<WebSocketException>(failure, failure.ToString());
                Assert.AreEqual(WebSocketState.Closed, connection.State);
                return (WebSocketException)failure;
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Guard;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail("The condition was not met within the guard.");
                await Task.Delay(10).ConfigureAwait(false);
            }
        }

        private static WebSocketConnectionOptions CreateOptions(TimeSpan handshakeTimeout)
        {
            return new WebSocketConnectionOptions
            {
                HandshakeTimeout = handshakeTimeout,
                CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                PingInterval = TimeSpan.Zero,
                IdleTimeout = TimeSpan.Zero
            };
        }

        /// <summary>First connect over loopback TCP; every later one fails TLS authentication.</summary>
        private sealed class CertificateFailingAfterFirstConnectTransport : IWebSocketTransport
        {
            private readonly TestTcpWebSocketTransport _inner = new TestTcpWebSocketTransport();
            private int _connectCalls;

            public int ConnectCalls => Volatile.Read(ref _connectCalls);

            public Task<Stream> ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct)
            {
                if (Interlocked.Increment(ref _connectCalls) == 1)
                    return _inner.ConnectAsync(uri, options, ct);

                return Task.FromException<Stream>(
                    new AuthenticationException("The remote certificate is invalid according to the validation procedure."));
            }

            public void Dispose()
            {
                _inner.Dispose();
            }
        }
    }
}
