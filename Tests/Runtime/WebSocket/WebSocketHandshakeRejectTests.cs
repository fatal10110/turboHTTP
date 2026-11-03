using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #83: a non-101 handshake response is read with the body framing its headers declare (RFC 9112
    /// section 6.3), so a keep-alive 401/403 fails at once with its status instead of waiting for EOF
    /// until HandshakeTimeout; the resilient client does not reconnect into a 4xx rejection.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketHandshakeRejectTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/reject");

        // Guard only: a correct validator returns without waiting; the old one never returned here.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

        [Test]
        public void ValidateAsync_ContentLengthBody_OnKeepAliveConnection_ReturnsWithStatus()
        {
            AssertAsync.Run(async () =>
            {
                using var stream = new ScriptedResponseStream(
                    endWithEof: false,
                    Ascii("HTTP/1.1 401 Unauthorized\r\n" +
                          "WWW-Authenticate: Bearer\r\n" +
                          "Content-Length: 12\r\n" +
                          "Connection: keep-alive\r\n\r\nunau"),
                    Ascii("thorized"));

                var result = await ValidateGuardedAsync(stream).ConfigureAwait(false);

                Assert.IsFalse(result.Success);
                Assert.AreEqual(401, result.StatusCode);
                Assert.AreEqual("unauthorized", result.ErrorBodyText);
                Assert.AreEqual(0, stream.BlockedReads, "The validator read past the declared body.");
            });
        }

        [Test]
        public void ValidateAsync_ChunkedBody_DecodesAndStopsAtLastChunk()
        {
            AssertAsync.Run(async () =>
            {
                using var stream = new ScriptedResponseStream(
                    endWithEof: false,
                    Ascii("HTTP/1.1 403 Forbidden\r\n" +
                          "Transfer-Encoding: chunked\r\n\r\n" +
                          "5;name=value\r\nhello\r\n"),
                    Ascii("7\r\n, world\r\n0\r\n\r\n"));

                var result = await ValidateGuardedAsync(stream).ConfigureAwait(false);

                Assert.AreEqual(403, result.StatusCode);
                Assert.AreEqual("hello, world", result.ErrorBodyText);
                Assert.AreEqual(0, stream.BlockedReads, "The validator read past the last chunk.");
            });
        }

        [Test]
        public void ValidateAsync_CloseDelimitedBody_ReadsUntilEof()
        {
            AssertAsync.Run(async () =>
            {
                using var stream = new ScriptedResponseStream(
                    endWithEof: true,
                    Ascii("HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\nforb"),
                    Ascii("idden"));

                var result = await ValidateGuardedAsync(stream).ConfigureAwait(false);

                Assert.AreEqual(403, result.StatusCode);
                Assert.AreEqual("forbidden", result.ErrorBodyText);
            });
        }

        [Test]
        public void ValidateAsync_BodyLargerThanLimit_StopsAtLimit()
        {
            AssertAsync.Run(async () =>
            {
                using var stream = new ScriptedResponseStream(
                    endWithEof: false,
                    Ascii("HTTP/1.1 401 Unauthorized\r\nContent-Length: 100000\r\n\r\n"),
                    new byte[2048]);

                var result = await ValidateGuardedAsync(stream, maxErrorBodyBytes: 1024).ConfigureAwait(false);

                Assert.AreEqual(401, result.StatusCode);
                Assert.AreEqual(1024, result.ErrorBody.Length);
                Assert.AreEqual(0, stream.BlockedReads);
            });
        }

        [Test]
        public void ValidateAsync_InvalidContentLength_SkipsBody()
        {
            AssertAsync.Run(async () =>
            {
                using var stream = new ScriptedResponseStream(
                    endWithEof: false,
                    Ascii("HTTP/1.1 401 Unauthorized\r\nContent-Length: 5, 6\r\n\r\n"));

                var result = await ValidateGuardedAsync(stream).ConfigureAwait(false);

                Assert.AreEqual(401, result.StatusCode);
                Assert.AreEqual(0, result.ErrorBody.Length);
                Assert.AreEqual(0, stream.BlockedReads);
            });
        }

        [Test]
        public void ConnectAsync_KeepAlive401_FailsImmediatelyWithStatus()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new ScriptedResponseStream(
                    endWithEof: false,
                    Ascii("HTTP/1.1 401 Unauthorized\r\nContent-Length: 6\r\n\r\ndenied"));
                using var transport = new SingleStreamTransport(stream);

                var options = new WebSocketConnectionOptions
                {
                    // Far beyond the guard: the old code waited for this and then lost the status.
                    HandshakeTimeout = TimeSpan.FromSeconds(60),
                    PingInterval = TimeSpan.Zero
                };

                var connection = new WebSocketConnection();
                try
                {
                    var connectTask = connection.ConnectAsync(TestUri, transport, options, CancellationToken.None);
                    var completed = await Task.WhenAny(connectTask, Task.Delay(Guard)).ConfigureAwait(false);
                    Assert.AreSame(connectTask, completed, "ConnectAsync waited for the handshake timeout.");

                    var error = AssertAsync.ThrowsAsync<WebSocketException>(() => connectTask);
                    Assert.AreEqual(WebSocketError.HandshakeFailed, error.Error);
                    Assert.AreEqual(401, error.HandshakeStatusCode);
                    StringAssert.Contains("401", error.Message);
                    Assert.AreEqual(WebSocketState.Closed, connection.State);
                }
                finally
                {
                    connection.Dispose();
                }
            });
        }

        [TestCase(401, true)]
        [TestCase(403, true)]
        [TestCase(404, true)]
        [TestCase(408, false)]
        [TestCase(429, false)]
        [TestCase(500, false)]
        [TestCase(503, false)]
        public void IsPermanentConnectFailure_FollowsHandshakeStatus(int status, bool expected)
        {
            var error = new WebSocketException(WebSocketError.HandshakeFailed, "rejected", null, status);

            Assert.AreEqual(expected, ResilientWebSocketClient.IsPermanentConnectFailure(error));
        }

        [Test]
        public void IsPermanentConnectFailure_ProxyAuthAndTransientErrors()
        {
            Assert.IsTrue(ResilientWebSocketClient.IsPermanentConnectFailure(
                new WebSocketException(WebSocketError.ProxyAuthenticationRequired, "407")));
            Assert.IsFalse(ResilientWebSocketClient.IsPermanentConnectFailure(
                new WebSocketException(WebSocketError.HandshakeFailed, "no status")));
            Assert.IsFalse(ResilientWebSocketClient.IsPermanentConnectFailure(
                new WebSocketException(WebSocketError.ConnectionClosed, "reset")));
        }

        [Test]
        public void ResilientClient_StopsReconnecting_OnUnauthorized()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(
                    new WebSocketTestServerOptions
                    {
                        DisconnectFirstConnectionAfterHandshake = true,
                        RejectHandshakeStatusCode = 401,
                        RejectHandshakeReason = "Unauthorized",
                        RejectHandshakeAfterConnectionCount = 1
                    });

                using var transport = new TestTcpWebSocketTransport();
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

                var options = new WebSocketConnectionOptions
                {
                    HandshakeTimeout = TimeSpan.FromSeconds(30),
                    CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                    PingInterval = TimeSpan.Zero
                };
                options.WithReconnection(new WebSocketReconnectPolicy(
                    maxRetries: 5,
                    initialDelay: TimeSpan.FromMilliseconds(10),
                    maxDelay: TimeSpan.FromMilliseconds(20),
                    backoffMultiplier: 1.0,
                    jitterFactor: 0.0));

                await client.ConnectAsync(server.CreateUri("/auth"), options, CancellationToken.None)
                    .ConfigureAwait(false);

                await WebSocketReceiveBackpressureTests.WaitWithTimeout(closed.Task, TimeSpan.FromSeconds(10),
                    "The resilient client did not close after the 401 rejection.").ConfigureAwait(false);

                Assert.AreEqual(1, Volatile.Read(ref reconnectEvents), "Reconnected again after a 401 rejection.");
                lock (errors)
                {
                    Assert.IsTrue(
                        errors.Exists(e => e.Error == WebSocketError.HandshakeFailed && e.HandshakeStatusCode == 401),
                        "OnError did not report the 401 handshake rejection.");
                }
            });
        }

        private static async Task<WebSocketHandshakeResult> ValidateGuardedAsync(
            ScriptedResponseStream stream,
            int maxErrorBodyBytes = 4096)
        {
            var request = WebSocketHandshake.BuildRequest(TestUri);
            var validate = WebSocketHandshakeValidator.ValidateAsync(
                stream,
                request,
                CancellationToken.None,
                maxErrorBodyBytes: maxErrorBodyBytes);

            var completed = await Task.WhenAny(validate, Task.Delay(Guard)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, validate))
            {
                stream.Dispose();
                Assert.Fail("The validator waited for more of the error body than the response declared.");
            }

            return await validate.ConfigureAwait(false);
        }

        private static byte[] Ascii(string text)
        {
            return Encoding.ASCII.GetBytes(text);
        }
    }
}
