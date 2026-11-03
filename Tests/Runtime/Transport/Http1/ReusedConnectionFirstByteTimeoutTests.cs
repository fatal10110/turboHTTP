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
using TurboHTTP.Transport.Connection;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// #47: pooled sockets enable TCP keepalive, and a reused connection that stays silent after an
    /// idempotent request is retried once on a fresh connection (a POST is not).
    /// </summary>
    [TestFixture]
    public class ReusedConnectionFirstByteTimeoutTests
    {
        private static readonly TimeSpan ShortFirstByte = TimeSpan.FromMilliseconds(200);

        [Test]
        public void ConnectedSocket_HasKeepAliveEnabled()
        {
            AssertAsync.Run(async () =>
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var accept = listener.AcceptTcpClientAsync();
                    using var socket = await HappyEyeballsConnector.ConnectAsync(
                        new[] { IPAddress.Loopback },
                        port,
                        TimeSpan.FromSeconds(10),
                        new HappyEyeballsOptions(),
                        CancellationToken.None);
                    using var accepted = await accept;

                    var keepAlive = (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive);
                    Assert.AreNotEqual(0, keepAlive, "SO_KEEPALIVE must be enabled on pooled sockets");
                }
                finally
                {
                    listener.Stop();
                }
            });
        }

        [Test]
        public void SilentReusedConnection_IdempotentGet_IsRetriedOnFreshConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new SilentThenOkServer();
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1, reusedConnectionFirstByteTimeout: ShortFirstByte);
                using var client = CreateClient(new RawSocketTransport(pool));
                await InjectIdleConnectionAsync(pool, server.Port);

                using var response = await client.Get(server.Url("/"))
                    .WithTimeout(TimeSpan.FromSeconds(20))
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                // Accept 1 is the injected silent connection, accept 2 the fresh retry.
                Assert.AreEqual(2, server.AcceptCount, "the GET must move to a fresh connection");
            });
        }

        [Test]
        public void SilentReusedConnection_Post_IsNotRetried()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new SilentThenOkServer();
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1, reusedConnectionFirstByteTimeout: ShortFirstByte);
                using var client = CreateClient(new RawSocketTransport(pool));
                await InjectIdleConnectionAsync(pool, server.Port);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Post(server.Url("/charge"))
                        .WithTimeout(TimeSpan.FromSeconds(1))
                        .SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.Timeout, ex.HttpError.Type);
                Assert.AreEqual(1, server.AcceptCount, "a POST must not be replayed on a fresh connection");
            });
        }

        [Test]
        public void InfiniteFirstByteTimeout_DisablesTheRetry()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new SilentThenOkServer();
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1, reusedConnectionFirstByteTimeout: Timeout.InfiniteTimeSpan);
                using var client = CreateClient(new RawSocketTransport(pool));
                await InjectIdleConnectionAsync(pool, server.Port);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get(server.Url("/"))
                        .WithTimeout(TimeSpan.FromSeconds(1))
                        .SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.Timeout, ex.HttpError.Type);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        [Test]
        public void ConnectionPoolOptions_FirstByteTimeout_DefaultsToTenSeconds_AndValidates()
        {
            var options = new ConnectionPoolOptions();
            Assert.AreEqual(TimeSpan.FromSeconds(10), options.ReusedConnectionFirstByteTimeout);
            Assert.Throws<ArgumentOutOfRangeException>(() => options.ReusedConnectionFirstByteTimeout = TimeSpan.Zero);
            Assert.IsTrue(options.IsDefault());

            options.ReusedConnectionFirstByteTimeout = Timeout.InfiniteTimeSpan;
            Assert.IsFalse(options.IsDefault());
            Assert.AreEqual(Timeout.InfiniteTimeSpan, options.Clone().ReusedConnectionFirstByteTimeout);
        }

        private static UHttpClient CreateClient(RawSocketTransport transport)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });
        }

        private static async Task InjectIdleConnectionAsync(TcpConnectionPool pool, int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var connection = new PooledConnection(tcp.Client, tcp.GetStream(), "127.0.0.1", port, false);

            var idleField = typeof(TcpConnectionPool).GetField(
                "_idleConnections",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
            idle.GetOrAdd($"127.0.0.1:{port}:", _ => new ConcurrentQueue<PooledConnection>())
                .Enqueue(connection);
        }

        /// <summary>
        /// Connection 1 reads the request and never answers (a half-dead pooled connection);
        /// later connections answer 200 and close.
        /// </summary>
        private sealed class SilentThenOkServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _lock = new object();
            private int _acceptCount;
            private volatile bool _disposed;

            public SilentThenOkServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = AcceptLoopAsync();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public int AcceptCount => Volatile.Read(ref _acceptCount);
            public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

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
                    _ = Task.Run(() => HandleAsync(client, index));
                }
            }

            private async Task HandleAsync(TcpClient client, int index)
            {
                try
                {
                    var stream = client.GetStream();
                    await ReadHeadAsync(stream);
                    if (index == 1)
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
