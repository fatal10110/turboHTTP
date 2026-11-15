using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// #830/#831: through a forward HTTP proxy, a reused connection that stays silent after an idempotent
    /// request is bounded by <see cref="ConnectionPoolOptions.ReusedConnectionFirstByteTimeout"/> like a
    /// direct one, and the request is retried once on a newly connected proxy socket, never the next idle
    /// one. The pool's liveness check on dequeue drops RST/FIN'd sockets, so these connections are silent.
    /// </summary>
    [TestFixture]
    public class ProxyStaleConnectionRetryTests
    {
        private static readonly TimeSpan ShortFirstByte = TimeSpan.FromMilliseconds(200);

        [Test]
        public void SilentReusedProxyConnection_IdempotentGet_IsRetriedOnNewProxyConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new SilentThenOkProxy(silentConnections: 1);
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1, reusedConnectionFirstByteTimeout: ShortFirstByte);
                using var client = CreateClient(pool, proxy.Port);
                await InjectIdleProxyConnectionAsync(pool, proxy.Port);

                // Before #831 the reused proxy connection had no first-byte bound: this GET hit the timeout.
                using var response = await client.Get("http://origin.example.com/a")
                    .WithTimeout(TimeSpan.FromSeconds(5))
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, proxy.AcceptCount, "accept 1 is the silent idle connection, accept 2 the retry");
                CollectionAssert.AreEqual(
                    new[] { "GET http://origin.example.com/a HTTP/1.1", "GET http://origin.example.com/a HTTP/1.1" },
                    proxy.RequestLines);
            }, TimeSpan.FromSeconds(20));
        }

        [Test]
        public void TwoSilentIdleProxyConnections_IdempotentGet_RetriesOnNewlyConnectedSocket()
        {
            AssertAsync.Run(async () =>
            {
                // A NAT drop kills every idle proxy connection at once: the retry must not take the
                // second dead idle socket.
                using var proxy = new SilentThenOkProxy(silentConnections: 2);
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 2, reusedConnectionFirstByteTimeout: ShortFirstByte);
                using var client = CreateClient(pool, proxy.Port);
                await InjectIdleProxyConnectionAsync(pool, proxy.Port);
                await InjectIdleProxyConnectionAsync(pool, proxy.Port);

                using var response = await client.Get("http://origin.example.com/a")
                    .WithTimeout(TimeSpan.FromSeconds(5))
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(3, proxy.AcceptCount, "accepts 1 and 2 are the idle connections, accept 3 the new retry socket");
                Assert.AreEqual(2, proxy.RequestLines.Length, "the second idle connection carries no request");
            }, TimeSpan.FromSeconds(20));
        }

        [Test]
        public void SilentReusedProxyConnection_Post_IsNotRetried()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new SilentThenOkProxy(silentConnections: 1);
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1, reusedConnectionFirstByteTimeout: ShortFirstByte);
                using var client = CreateClient(pool, proxy.Port);
                await InjectIdleProxyConnectionAsync(pool, proxy.Port);
                await proxy.FirstConnectionCounted;

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Post("http://origin.example.com/charge")
                        .WithTimeout(TimeSpan.FromSeconds(1))
                        .SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.Timeout, ex.HttpError.Type);
                Assert.AreEqual(1, proxy.AcceptCount, "a POST must not be replayed on a new proxy connection");
            }, TimeSpan.FromSeconds(20));
        }

        private static UHttpClient CreateClient(TcpConnectionPool pool, int proxyPort)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                AllowCleartext = true,
                Transport = new RawSocketTransport(pool),
                DisposeTransport = true,
                Proxy = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyPort}"),
                    UseEnvironmentVariables = false
                }
            });
        }

        /// <summary>Puts a live connection to the proxy in the pool's idle queue for the proxy key.</summary>
        private static async Task InjectIdleProxyConnectionAsync(TcpConnectionPool pool, int proxyPort)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, proxyPort);
            var connection = new PooledConnection(tcp.Client, new HoldingNetworkStream(tcp.Client), "127.0.0.1", proxyPort, false);

            var idleField = typeof(TcpConnectionPool).GetField(
                "_idleConnections",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
            idle.GetOrAdd(TcpConnectionPool.BuildConnectionKey("127.0.0.1", proxyPort, false), _ => new ConcurrentQueue<PooledConnection>())
                .Enqueue(connection);
        }

        /// <summary>
        /// A forward proxy that answers itself: the first <c>silentConnections</c> connections read the
        /// request and never answer; later ones answer 200 and close.
        /// </summary>
        private sealed class SilentThenOkProxy : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly List<string> _requestLines = new List<string>();
            private readonly object _lock = new object();
            private readonly int _silentConnections;
            private readonly TaskCompletionSource<bool> _firstConnectionCounted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _acceptCount;
            private volatile bool _disposed;

            public SilentThenOkProxy(int silentConnections)
            {
                _silentConnections = silentConnections;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = AcceptLoopAsync();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public int AcceptCount => Volatile.Read(ref _acceptCount);

            /// <summary>Completes once the accept loop counted the first connection (#370).</summary>
            public Task FirstConnectionCounted => _firstConnectionCounted.Task;

            public string[] RequestLines
            {
                get
                {
                    lock (_lock)
                        return _requestLines.ToArray();
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
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException)
                    {
                        if (_disposed)
                            return;
                        continue;
                    }

                    lock (_lock)
                        _clients.Add(client);

                    int index = Interlocked.Increment(ref _acceptCount);
                    if (index == 1)
                        _firstConnectionCounted.TrySetResult(true);
                    _ = Task.Run(() => HandleAsync(client, index));
                }
            }

            private async Task HandleAsync(TcpClient client, int index)
            {
                try
                {
                    var stream = client.GetStream();
                    var head = await ReadHeadAsync(stream);
                    if (head == null)
                        return;

                    lock (_lock)
                        _requestLines.Add(head.Substring(0, head.IndexOf("\r\n", StringComparison.Ordinal)));
                    if (index <= _silentConnections)
                        return; // keep the connection open and silent

                    var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, 0, response.Length);
                    client.Close();
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            private static async Task<string> ReadHeadAsync(Stream stream)
            {
                var buffer = new byte[1];
                var head = new StringBuilder();
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, 0, 1);
                    if (read == 0)
                        return null;
                    head.Append((char)buffer[0]);
                    if (head.Length >= 4 && head.ToString(head.Length - 4, 4) == "\r\n\r\n")
                        return head.ToString();
                }
            }

            public void Dispose()
            {
                _disposed = true;
                _listener.Stop();
                lock (_lock)
                {
                    foreach (var client in _clients)
                        client.Close();
                }
            }
        }
    }
}
