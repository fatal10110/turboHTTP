using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// Proxy 407 handling: the CONNECT retry reuses a connection only when the 407 is safely framed
    /// (#229), and forwarded <c>http://</c> requests answer a Basic challenge instead of sending
    /// Proxy-Authorization preemptively (#236).
    /// </summary>
    [TestFixture]
    public class RawSocketTransportProxy407Tests
    {
        private const string ExpectedBasic = "Basic dXNlcjpwYXNz"; // user:pass
        private const string BasicChallenge = "Proxy-Authenticate: Basic realm=\"proxy\"\r\n";

        // ---------------------------------------------------------------- #229: CONNECT 407 reuse

        private static IEnumerable<TestCaseData> UnsafeConnect407Replies()
        {
            yield return new TestCaseData(
                "HTTP/1.1 407 Proxy Authentication Required\r\n" + BasicChallenge +
                "Content-Length: 0\r\nConnection: close\r\n\r\n",
                true).SetName("Connect407_ConnectionClose_RetriesOnFreshConnection");
            yield return new TestCaseData(
                "HTTP/1.1 407 Proxy Authentication Required\r\n" + BasicChallenge +
                "Connection: keep-alive\r\n\r\nbody-runs-to-close",
                false).SetName("Connect407_NoFraming_RetriesOnFreshConnection");
            yield return new TestCaseData(
                "HTTP/1.1 407 Proxy Authentication Required\r\n" + BasicChallenge +
                "Transfer-Encoding: chunked\r\nContent-Length: 5\r\nConnection: keep-alive\r\n\r\n0\r\n\r\n",
                false).SetName("Connect407_TransferEncodingAndContentLength_RetriesOnFreshConnection");
            yield return new TestCaseData(
                "HTTP/1.1 407 Proxy Authentication Required\r\n" + BasicChallenge +
                "Transfer-Encoding: chunked\r\nConnection: keep-alive\r\n\r\n0\r\n\r\n",
                false).SetName("Connect407_Chunked_RetriesOnFreshConnection");
            yield return new TestCaseData(
                "HTTP/1.0 407 Proxy Authentication Required\r\n" + BasicChallenge +
                "Content-Length: 0\r\n\r\n",
                true).SetName("Connect407_Http10WithoutKeepAlive_RetriesOnFreshConnection");
        }

        [TestCaseSource(nameof(UnsafeConnect407Replies))]
        public void Connect407_NotReusable_SendsAuthenticatedConnectOnSecondConnection(string reply407, bool closeAfter)
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new ScriptedProxy(request =>
                {
                    if (request.ProxyAuthorization == null)
                        return new ProxyReply(reply407, closeAfter);

                    return new ProxyReply("HTTP/1.1 200 Connection Established\r\n\r\n", close: true);
                });

                using var client = CreateClient(proxy.Port, allowPlaintextProxyAuth: true, sslStreamOnly: true);

                // The tunnel is established; the TLS handshake then fails against the plain test proxy.
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get("https://origin.example.com/secure").SendBufferedAsync());
                StringAssert.DoesNotContain("407", ex.Message);
                StringAssert.DoesNotContain("CONNECT", ex.Message);

                var requests = proxy.Requests;
                Assert.GreaterOrEqual(requests.Count, 2);
                Assert.AreEqual("CONNECT origin.example.com:443 HTTP/1.1", requests[0].RequestLine);
                Assert.AreEqual(1, requests[0].Connection);
                Assert.IsNull(requests[0].ProxyAuthorization);

                Assert.AreEqual("CONNECT origin.example.com:443 HTTP/1.1", requests[1].RequestLine);
                Assert.AreEqual(2, requests[1].Connection, "the authenticated CONNECT must use a new connection");
                Assert.AreEqual(1, requests[1].IndexOnConnection);
                Assert.AreEqual(ExpectedBasic, requests[1].ProxyAuthorization);

                Assert.IsFalse(
                    requests.Any(r => r.Connection == 1 && r.IndexOnConnection > 1),
                    "no request may follow an unsafely framed 407 on the same connection");
            });
        }

        [Test]
        public void Connect407_ContentLengthBody_RetriesOnSameConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new ScriptedProxy(request =>
                {
                    if (request.ProxyAuthorization == null)
                    {
                        return new ProxyReply(
                            "HTTP/1.1 407 Proxy Authentication Required\r\n" + BasicChallenge +
                            "Content-Length: 6\r\nConnection: keep-alive\r\n\r\ndenied",
                            close: false);
                    }

                    return new ProxyReply("HTTP/1.1 200 Connection Established\r\n\r\n", close: true);
                });

                using var client = CreateClient(proxy.Port, allowPlaintextProxyAuth: true, sslStreamOnly: true);

                AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get("https://origin.example.com/secure").SendBufferedAsync());

                var requests = proxy.Requests;
                Assert.GreaterOrEqual(requests.Count, 2);
                Assert.IsNull(requests[0].ProxyAuthorization);
                Assert.AreEqual(1, requests[1].Connection, "a Content-Length 407 is drained and the connection reused");
                Assert.AreEqual(2, requests[1].IndexOnConnection);
                Assert.AreEqual(ExpectedBasic, requests[1].ProxyAuthorization);
            });
        }

        [TestCase("HTTP/1.1 407 X\r\nContent-Length: 0\r\n\r\n", true)]
        [TestCase("HTTP/1.1 407 X\r\nContent-Length: 3\r\nConnection: keep-alive\r\n\r\n", true)]
        [TestCase("HTTP/1.0 407 X\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n", true)]
        [TestCase("HTTP/1.1 407 X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", false)]
        [TestCase("HTTP/1.1 407 X\r\nConnection: keep-alive\r\n\r\n", false)]
        [TestCase("HTTP/1.1 407 X\r\nTransfer-Encoding: chunked\r\n\r\n", false)]
        [TestCase("HTTP/1.1 407 X\r\nTransfer-Encoding: chunked\r\nContent-Length: 3\r\n\r\n", false)]
        [TestCase("HTTP/1.0 407 X\r\nContent-Length: 0\r\n\r\n", false)]
        public void ConnectReply_CanRetryOnSameConnection_FollowsFraming(string head, bool expected)
        {
            AssertAsync.Run(async () =>
            {
                using var stream = new MemoryStream(Encoding.ASCII.GetBytes(head), writable: false);
                var reply = await ConnectReplyReader.ReadAsync(stream, CancellationToken.None);
                Assert.AreEqual(expected, reply.CanRetryOnSameConnection);
            });
        }

        // ------------------------------------------------- #236: forward proxy 407 challenge retry

        [Test]
        public void ForwardProxy_BasicChallenge_FirstAttemptHasNoCredentialsAndRetriesOnceWithBasic()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new ScriptedProxy(request =>
                {
                    if (request.ProxyAuthorization == null)
                    {
                        return new ProxyReply(
                            "HTTP/1.1 407 Proxy Authentication Required\r\n" + BasicChallenge +
                            "Content-Length: 6\r\nConnection: keep-alive\r\n\r\ndenied",
                            close: false);
                    }

                    return new ProxyReply("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok", close: true);
                });

                using var client = CreateClient(proxy.Port, allowPlaintextProxyAuth: true);

                using var response = await client
                    .Get("http://origin.example.com/resource?id=1")
                    .WithHeader("Proxy-Authorization", "Basic caller-supplied")
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("ok", response.GetBodyAsString());

                var requests = proxy.Requests;
                Assert.AreEqual(2, requests.Count);
                Assert.AreEqual("GET http://origin.example.com/resource?id=1 HTTP/1.1", requests[0].RequestLine);
                Assert.IsNull(requests[0].ProxyAuthorization, "the first forwarded attempt carries no credentials");
                Assert.AreEqual("GET http://origin.example.com/resource?id=1 HTTP/1.1", requests[1].RequestLine);
                Assert.AreEqual(ExpectedBasic, requests[1].ProxyAuthorization);
                Assert.AreEqual(1, requests[1].Connection, "a drained keep-alive 407 leaves the connection reusable");
            });
        }

        [Test]
        public void ForwardProxy_BasicChallengeWithConnectionClose_RetriesOnFreshConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new ScriptedProxy(request =>
                {
                    if (request.ProxyAuthorization == null)
                    {
                        return new ProxyReply(
                            "HTTP/1.1 407 Proxy Authentication Required\r\n" + BasicChallenge +
                            "Content-Length: 0\r\nConnection: close\r\n\r\n",
                            close: true);
                    }

                    return new ProxyReply("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok", close: true);
                });

                using var client = CreateClient(proxy.Port, allowPlaintextProxyAuth: true);
                using var response = await client.Get("http://origin.example.com/a").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var requests = proxy.Requests;
                Assert.AreEqual(2, requests.Count);
                Assert.IsNull(requests[0].ProxyAuthorization);
                Assert.AreEqual(2, requests[1].Connection);
                Assert.AreEqual(ExpectedBasic, requests[1].ProxyAuthorization);
            });
        }

        [TestCase("Digest realm=\"proxy\", nonce=\"abc\", qop=\"auth\"")]
        [TestCase("Negotiate")]
        public void ForwardProxy_NonBasicChallenge_ReturnsResponseWithoutSendingCredentials(string challenge)
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new ScriptedProxy(_ => new ProxyReply(
                    "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: " + challenge + "\r\n" +
                    "Content-Length: 6\r\nConnection: close\r\n\r\ndenied",
                    close: true));

                using var client = CreateClient(proxy.Port, allowPlaintextProxyAuth: true);
                using var response = await client.Get("http://origin.example.com/a").SendBufferedAsync();

                Assert.AreEqual((HttpStatusCode)407, response.StatusCode);
                Assert.AreEqual("denied", response.GetBodyAsString());
                Assert.AreEqual(challenge, response.Headers.Get("Proxy-Authenticate"));

                var requests = proxy.Requests;
                Assert.AreEqual(1, requests.Count);
                Assert.IsNull(requests[0].ProxyAuthorization);
            });
        }

        [Test]
        public void ForwardProxy_BasicRejectedTwice_ReturnsSecond407WithoutFurtherRetry()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new ScriptedProxy(request => new ProxyReply(
                    "HTTP/1.1 407 Proxy Authentication Required\r\n" + BasicChallenge +
                    "Content-Length: 6\r\nConnection: keep-alive\r\n\r\ndenied",
                    close: false));

                using var client = CreateClient(proxy.Port, allowPlaintextProxyAuth: true);
                using var response = await client.Get("http://origin.example.com/a").SendBufferedAsync();

                Assert.AreEqual((HttpStatusCode)407, response.StatusCode);
                Assert.AreEqual("denied", response.GetBodyAsString());

                var requests = proxy.Requests;
                Assert.AreEqual(2, requests.Count);
                Assert.IsNull(requests[0].ProxyAuthorization);
                Assert.AreEqual(ExpectedBasic, requests[1].ProxyAuthorization);
            });
        }

        [Test]
        public void ForwardProxy_NonReplayableBody_Returns407WithoutRetry()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new ScriptedProxy(_ => new ProxyReply(
                    "HTTP/1.1 407 Proxy Authentication Required\r\n" + BasicChallenge +
                    "Content-Length: 6\r\nConnection: close\r\n\r\ndenied",
                    close: true));

                using var client = CreateClient(proxy.Port, allowPlaintextProxyAuth: true);
                using var body = new NonSeekableStream(Encoding.UTF8.GetBytes("payload"));
                using var response = await client
                    .Post("http://origin.example.com/upload")
                    .WithStreamBody(body, 7, leaveOpen: true)
                    .SendBufferedAsync();

                Assert.AreEqual((HttpStatusCode)407, response.StatusCode);
                var requests = proxy.Requests;
                Assert.AreEqual(1, requests.Count);
                Assert.IsNull(requests[0].ProxyAuthorization);
                Assert.AreEqual("payload", Encoding.UTF8.GetString(requests[0].Body));
            });
        }

        [Test]
        public void ForwardProxy_ReplayableBody_IsResentWithBasic()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new ScriptedProxy(request =>
                {
                    if (request.ProxyAuthorization == null)
                    {
                        return new ProxyReply(
                            "HTTP/1.1 407 Proxy Authentication Required\r\n" + BasicChallenge +
                            "Content-Length: 0\r\n\r\n",
                            close: false);
                    }

                    return new ProxyReply("HTTP/1.1 201 Created\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", close: true);
                });

                using var client = CreateClient(proxy.Port, allowPlaintextProxyAuth: true);
                using var response = await client
                    .Post("http://origin.example.com/upload")
                    .WithBody("payload")
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
                var requests = proxy.Requests;
                Assert.AreEqual(2, requests.Count);
                Assert.AreEqual("payload", Encoding.UTF8.GetString(requests[0].Body));
                Assert.AreEqual("payload", Encoding.UTF8.GetString(requests[1].Body));
                Assert.AreEqual(ExpectedBasic, requests[1].ProxyAuthorization);
            });
        }

        [Test]
        public void ForwardProxy_ExpectContinueEarly407_ResendsOnFreshConnectionWithBody()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new ScriptedProxy(request =>
                {
                    if (request.ProxyAuthorization == null)
                    {
                        // Final response before the body (RFC 9110 section 10.1.1); no body byte was sent.
                        return new ProxyReply(
                            "HTTP/1.1 407 Proxy Authentication Required\r\n" + BasicChallenge +
                            "Content-Length: 0\r\n\r\n",
                            close: false);
                    }

                    return new ProxyReply(
                        "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                        close: true,
                        sendContinue: true);
                });

                using var client = CreateClient(proxy.Port, allowPlaintextProxyAuth: true);
                using var response = await client
                    .Post("http://origin.example.com/upload")
                    .WithHeader("Expect", "100-continue")
                    .WithBody("payload")
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var requests = proxy.Requests;
                Assert.AreEqual(2, requests.Count);
                Assert.IsNull(requests[0].ProxyAuthorization);
                Assert.IsNull(requests[0].Body, "no body byte follows an early final response");
                Assert.AreEqual(2, requests[1].Connection, "an early final response ends the connection");
                Assert.AreEqual(ExpectedBasic, requests[1].ProxyAuthorization);
                Assert.AreEqual("payload", Encoding.UTF8.GetString(requests[1].Body));
            });
        }

        [Test]
        public void ForwardProxy_PlaintextProxyAuthDisabled_Returns407WithoutCredentials()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new ScriptedProxy(_ => new ProxyReply(
                    "HTTP/1.1 407 Proxy Authentication Required\r\n" + BasicChallenge +
                    "Content-Length: 0\r\nConnection: close\r\n\r\n",
                    close: true));

                using var client = CreateClient(proxy.Port, allowPlaintextProxyAuth: false);
                using var response = await client.Get("http://origin.example.com/a").SendBufferedAsync();

                Assert.AreEqual((HttpStatusCode)407, response.StatusCode);
                var requests = proxy.Requests;
                Assert.AreEqual(1, requests.Count);
                Assert.IsNull(requests[0].ProxyAuthorization);
            });
        }

        // ------------------------------------------------------------------------------ helpers

        private static UHttpClient CreateClient(int proxyPort, bool allowPlaintextProxyAuth, bool sslStreamOnly = false)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = sslStreamOnly
                    ? new RawSocketTransport(tlsBackend: TlsBackend.SslStream)
                    : new RawSocketTransport(),
                DisposeTransport = true,
                Proxy = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyPort}"),
                    Credentials = new NetworkCredential("user", "pass"),
                    AllowPlaintextProxyAuth = allowPlaintextProxyAuth,
                    UseEnvironmentVariables = false
                }
            });
        }

        private sealed class CapturedRequest
        {
            public int Connection;
            public int IndexOnConnection;
            public string RequestLine;
            public Dictionary<string, string> Headers;
            public byte[] Body;

            public string ProxyAuthorization =>
                Headers.TryGetValue("Proxy-Authorization", out var value) ? value : null;
        }

        // A keep-alive Basic 407 whose body the transport will not drain (chunked, or larger than
        // MaxProxyChallengeDrainBytes = 64 KiB) must not be pooled: the retry goes on a fresh
        // connection. The scripted proxy keeps connection 1 open, so pooling it would put the
        // retry on connection 1.
        [TestCase("Content-Length: 65537\r\nConnection: keep-alive\r\n\r\n")]
        [TestCase("Transfer-Encoding: chunked\r\nConnection: keep-alive\r\n\r\n6\r\ndenied\r\n0\r\n\r\n")]
        public void ForwardProxy_BasicChallengeWithUndrainableBody_RetriesOnFreshConnection(string framingAndBody)
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new ScriptedProxy(request =>
                {
                    if (request.ProxyAuthorization == null)
                    {
                        return new ProxyReply(
                            "HTTP/1.1 407 Proxy Authentication Required\r\n" + BasicChallenge + framingAndBody,
                            close: false);
                    }

                    return new ProxyReply("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok", close: true);
                });

                using var client = CreateClient(proxy.Port, allowPlaintextProxyAuth: true);
                using var response = await client.Get("http://origin.example.com/a").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("ok", response.GetBodyAsString());
                var requests = proxy.Requests;
                Assert.AreEqual(2, requests.Count);
                Assert.IsNull(requests[0].ProxyAuthorization);
                Assert.AreEqual(2, requests[1].Connection, "an undrainable 407 body closes the connection");
                Assert.AreEqual(ExpectedBasic, requests[1].ProxyAuthorization);
            });
        }

        private readonly struct ProxyReply
        {
            public ProxyReply(string response, bool close, bool sendContinue = false)
            {
                Response = response;
                Close = close;
                SendContinue = sendContinue;
            }

            public string Response { get; }
            public bool Close { get; }
            public bool SendContinue { get; }
        }

        /// <summary>
        /// Loopback proxy that records every request with its connection number and answers each
        /// one from a script. A connection stays open after a reply unless the script closes it.
        /// </summary>
        private sealed class ScriptedProxy : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Func<CapturedRequest, ProxyReply> _script;
            private readonly List<CapturedRequest> _requests = new List<CapturedRequest>();
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _lock = new object();
            private int _acceptCount;
            private volatile bool _disposed;

            public ScriptedProxy(Func<CapturedRequest, ProxyReply> script)
            {
                _script = script;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = Task.Run(AcceptLoopAsync);
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public List<CapturedRequest> Requests
            {
                get
                {
                    lock (_lock)
                        return _requests.OrderBy(r => r.Connection).ThenBy(r => r.IndexOnConnection).ToList();
                }
            }

            private async Task AcceptLoopAsync()
            {
                while (!_disposed)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception) when (_disposed)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        continue;
                    }

                    lock (_lock)
                        _clients.Add(client);

                    var connection = Interlocked.Increment(ref _acceptCount);
                    _ = Task.Run(() => ServeAsync(client, connection));
                }
            }

            private async Task ServeAsync(TcpClient client, int connection)
            {
                try
                {
                    using var stream = client.GetStream();
                    for (var index = 1; ; index++)
                    {
                        var requestLine = await ReadLineAsync(stream);
                        if (string.IsNullOrEmpty(requestLine))
                            return;

                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        while (true)
                        {
                            var line = await ReadLineAsync(stream);
                            if (string.IsNullOrEmpty(line))
                                break;
                            var colon = line.IndexOf(':');
                            if (colon > 0)
                                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                        }

                        var captured = new CapturedRequest
                        {
                            Connection = connection,
                            IndexOnConnection = index,
                            RequestLine = requestLine,
                            Headers = headers
                        };

                        var reply = _script(captured);
                        var expectsContinue = headers.TryGetValue("Expect", out var expect) &&
                            string.Equals(expect, "100-continue", StringComparison.OrdinalIgnoreCase);
                        if (reply.SendContinue)
                            await WriteAsync(stream, "HTTP/1.1 100 Continue\r\n\r\n");

                        if ((!expectsContinue || reply.SendContinue) &&
                            headers.TryGetValue("Content-Length", out var lengthText) &&
                            int.TryParse(lengthText, out var length) && length > 0)
                        {
                            captured.Body = await ReadExactAsync(stream, length);
                        }

                        lock (_lock)
                            _requests.Add(captured);

                        await WriteAsync(stream, reply.Response);
                        if (reply.Close)
                            return;
                    }
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (SocketException)
                {
                }
                finally
                {
                    client.Close();
                }
            }

            public void Dispose()
            {
                _disposed = true;
                _listener.Stop();
                lock (_lock)
                {
                    foreach (var client in _clients)
                    {
                        try { client.Close(); } catch { /* best effort */ }
                    }
                    _clients.Clear();
                }
            }

            private static async Task WriteAsync(NetworkStream stream, string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                await stream.FlushAsync();
            }

            private static async Task<string> ReadLineAsync(NetworkStream stream)
            {
                var bytes = new List<byte>(128);
                var buffer = new byte[1];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, 0, 1);
                    if (read == 0)
                        break;
                    if (buffer[0] == (byte)'\n')
                        break;
                    if (buffer[0] != (byte)'\r')
                        bytes.Add(buffer[0]);
                }

                return Encoding.ASCII.GetString(bytes.ToArray());
            }

            private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
            {
                var buffer = new byte[count];
                var offset = 0;
                while (offset < count)
                {
                    var read = await stream.ReadAsync(buffer, offset, count - offset);
                    if (read == 0)
                        throw new IOException("Unexpected end of request body.");
                    offset += read;
                }

                return buffer;
            }
        }

        private sealed class NonSeekableStream : Stream
        {
            private readonly MemoryStream _inner;

            public NonSeekableStream(byte[] data)
            {
                _inner = new MemoryStream(data, writable: false);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
