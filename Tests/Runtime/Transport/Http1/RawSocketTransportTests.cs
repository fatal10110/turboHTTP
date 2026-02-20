using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    [TestFixture]
    public class RawSocketTransportTests
    {
        private sealed class HttpServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _lock = new object();
            private readonly Func<TcpClient, int, Task> _handler;
            private int _acceptCount;

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public int AcceptCount => Volatile.Read(ref _acceptCount);

            public HttpServer(Func<TcpClient, int, Task> handler)
            {
                _handler = handler ?? throw new ArgumentNullException(nameof(handler));
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = Task.Run(AcceptLoop);
            }

            private async Task AcceptLoop()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client = null;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        if (_cts.IsCancellationRequested)
                            break;
                        continue;
                    }

                    lock (_lock)
                    {
                        _clients.Add(client);
                    }

                    int index = Interlocked.Increment(ref _acceptCount);
                    _ = Task.Run(() => _handler(client, index));
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                _listener.Stop();
                lock (_lock)
                {
                    foreach (var client in _clients)
                    {
                        try { client.Close(); } catch { }
                    }
                    _clients.Clear();
                }
            }
        }

        private sealed class FailingStream : Stream
        {
            private readonly Stream _inner;

            public FailingStream(Stream inner)
            {
                _inner = inner;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() => _inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new IOException("Injected failure");
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return Task.FromException<int>(new IOException("Injected failure"));
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new IOException("Injected failure");
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return Task.FromException(new IOException("Injected failure"));
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();
                base.Dispose(disposing);
            }
        }

        [Test]
        public void SendAsync_UnsupportedTransferEncoding_MapsToNetworkError()        {
            Task.Run(async () =>
            {
                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream);
                    var response = "HTTP/1.1 200 OK\r\n" +
                                   "Transfer-Encoding: gzip\r\n" +
                                   "Content-Type: text/plain\r\n" +
                                   "\r\n" +
                                   "Hello";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get($"http://127.0.0.1:{server.Port}/").SendAsync());

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RawSocketTransport_StaleConnection_RetriesOnce()        {
            Task.Run(async () =>
            {
                using var server = new HttpServer(async (client, index) =>
                {
                    if (index == 1)
                    {
                        // Keep open: this connection is injected as a failing reused connection.
                        return;
                    }

                    using var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream);
                    var response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var transport = new RawSocketTransport(pool);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                // Inject a reused connection that will throw on write/read.
                var failingClient = new TcpClient();
                await failingClient.ConnectAsync(IPAddress.Loopback, server.Port);
                var failingStream = new FailingStream(failingClient.GetStream());
                var failingConn = new PooledConnection(failingClient.Client, failingStream, "127.0.0.1", server.Port, false);

                var idleField = typeof(TcpConnectionPool).GetField("_idleConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
                var key = $"127.0.0.1:{server.Port}:";
                var queue = idle.GetOrAdd(key, _ => new ConcurrentQueue<PooledConnection>());
                queue.Enqueue(failingConn);

                var responseResult = await client.Get($"http://127.0.0.1:{server.Port}/").SendAsync();
                Assert.AreEqual(HttpStatusCode.OK, responseResult.StatusCode);
                Assert.GreaterOrEqual(server.AcceptCount, 2);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RawSocketTransport_StaleConnection_NonIdempotent_NoRetry()        {
            Task.Run(async () =>
            {
                using var server = new HttpServer(async (client, index) =>
                {
                    if (index == 1)
                    {
                        return;
                    }

                    using var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream);
                    var response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var transport = new RawSocketTransport(pool);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var failingClient = new TcpClient();
                await failingClient.ConnectAsync(IPAddress.Loopback, server.Port);
                var failingStream = new FailingStream(failingClient.GetStream());
                var failingConn = new PooledConnection(failingClient.Client, failingStream, "127.0.0.1", server.Port, false);

                var idleField = typeof(TcpConnectionPool).GetField("_idleConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
                var key = $"127.0.0.1:{server.Port}:";
                var queue = idle.GetOrAdd(key, _ => new ConcurrentQueue<PooledConnection>());
                queue.Enqueue(failingConn);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Post($"http://127.0.0.1:{server.Port}/").WithBody("test").SendAsync());

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.AreEqual(1, server.AcceptCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void NonKeepAlive_Response_SemaphoreReleased()        {
            Task.Run(async () =>
            {
                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream);
                    var response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var transport = new RawSocketTransport(pool);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var first = await client.Get($"http://127.0.0.1:{server.Port}/").SendAsync();
                var second = await client.Get($"http://127.0.0.1:{server.Port}/").SendAsync();

                Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void HttpViaProxy_UsesAbsoluteForm()
        {
            Task.Run(async () =>
            {
                string requestLine = null;
                string proxyAuth = null;

                using var proxyServer = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    requestLine = captured.RequestLine;
                    captured.Headers.TryGetValue("Proxy-Authorization", out proxyAuth);

                    var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                var proxySettings = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                    UseEnvironmentVariables = false
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    Proxy = proxySettings
                });

                var response = await client.Get("http://origin.example.com/resource?id=42").SendAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("GET http://origin.example.com/resource?id=42 HTTP/1.1", requestLine);
                Assert.IsNull(proxyAuth);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Connect407_RetryWithAuthOnce()
        {
            Task.Run(async () =>
            {
                int connectCount = 0;
                string firstAuth = null;
                string secondAuth = null;

                using var proxyServer = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();

                    var first = await ReadRequestStartAndHeadersAsync(stream);
                    connectCount++;
                    first.Headers.TryGetValue("Proxy-Authorization", out firstAuth);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");

                    var second = await ReadRequestStartAndHeadersAsync(stream);
                    connectCount++;
                    second.Headers.TryGetValue("Proxy-Authorization", out secondAuth);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 Connection Established\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    client.Close();
                });

                var proxySettings = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                    Credentials = new NetworkCredential("user", "pass"),
                    AllowPlaintextProxyAuth = true,
                    UseEnvironmentVariables = false
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    Proxy = proxySettings
                });

                // CONNECT succeeds but TLS handshake cannot complete with this plain test server.
                AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get("https://origin.example.com/secure").SendAsync());

                Assert.AreEqual(2, connectCount);
                Assert.IsNull(firstAuth);
                Assert.IsNotNull(secondAuth);
                StringAssert.StartsWith("Basic ", secondAuth);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Connect407_NoCredentialsFails()
        {
            Task.Run(async () =>
            {
                using var proxyServer = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    await ReadRequestStartAndHeadersAsync(stream);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    client.Close();
                });

                var proxySettings = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                    UseEnvironmentVariables = false
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    Proxy = proxySettings
                });

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get("https://origin.example.com/secure").SendAsync());
                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
                StringAssert.Contains("Proxy authentication required", ex.HttpError.Message);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CancellationDuringConnect_NoLeaks()
        {
            Task.Run(async () =>
            {
                using var proxyServer = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    await ReadRequestStartAndHeadersAsync(stream);
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    client.Close();
                });

                var proxySettings = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                    UseEnvironmentVariables = false
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    Proxy = proxySettings
                });

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get("https://origin.example.com/secure").SendAsync(cts.Token));
                Assert.AreEqual(UHttpErrorType.Cancelled, ex.HttpError.Type);
            }).GetAwaiter().GetResult();
        }

#if TURBOHTTP_INTEGRATION_TESTS
        [Test]
        [Category("Integration")]
        public void RawSocketTransport_FreshConnection_IOException_NoRetry()        {
            Task.Run(async () =>
            {
                using var server = new HttpServer((client, _) =>
                {
                    client.Client.LingerState = new LingerOption(true, 0);
                    client.Close();
                    return Task.CompletedTask;
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get($"http://127.0.0.1:{server.Port}/").SendAsync());

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.AreEqual(1, server.AcceptCount);
            }).GetAwaiter().GetResult();
        }
#endif

        private static async Task WriteResponseAsync(NetworkStream stream, string response)
        {
            var bytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        private static async Task ReadRequestHeadersAsync(NetworkStream stream)
        {
            var buffer = new byte[1];
            int matched = 0; // matches \r\n\r\n

            while (matched < 4)
            {
                int read = await stream.ReadAsync(buffer, 0, 1);
                if (read == 0) break;

                byte b = buffer[0];
                if (matched == 0 && b == (byte)'\r') matched = 1;
                else if (matched == 1 && b == (byte)'\n') matched = 2;
                else if (matched == 2 && b == (byte)'\r') matched = 3;
                else if (matched == 3 && b == (byte)'\n') matched = 4;
                else matched = b == (byte)'\r' ? 1 : 0;
            }
        }

        private static async Task<(string RequestLine, Dictionary<string, string> Headers)> ReadRequestStartAndHeadersAsync(NetworkStream stream)
        {
            var requestLine = await ReadLineAsync(stream);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                var line = await ReadLineAsync(stream);
                if (string.IsNullOrEmpty(line))
                    break;

                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;

                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            return (requestLine, headers);
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
    }
}
