using System;
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
    /// <summary>
    /// #821: an idempotent request to a server slower than the first-byte bound succeeds whether or not
    /// it started on a pooled connection. On a pooled one the first attempt is aborted at the bound and
    /// the request goes out again on a new connection, which waits for the response (it is not armed).
    /// </summary>
    [TestFixture]
    public class RetrySlowFirstByteTests
    {
        private static readonly TimeSpan ShortFirstByte = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan SlowFirstByte = TimeSpan.FromMilliseconds(1000);

        [Test]
        public void SlowResponse_AfterPooledConnection_SucceedsOnTheUnarmedRetry()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new SlowServer(SlowFirstByte);
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1, reusedConnectionFirstByteTimeout: ShortFirstByte);
                using var client = CreateClient(pool);

                using (var warm = await client.Get(server.Url("/warm")).SendBufferedAsync())
                    Assert.AreEqual(HttpStatusCode.OK, warm.StatusCode);

                // Before #821 the retry was armed too and failed at the bound (NetworkError).
                using var response = await client.Get(server.Url("/slow"))
                    .WithTimeout(TimeSpan.FromSeconds(10))
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("slow", response.GetBodyAsString());
                Assert.AreEqual(2, server.AcceptCount, "the pooled connection, then one retry connection");
                Assert.AreEqual(2, server.SlowRequestCount,
                    "the attempt on the pooled connection is aborted at the bound and sent again (documented)");
            }, TimeSpan.FromSeconds(20));
        }

        [Test]
        public void SlowResponse_OnFreshConnection_IsNotRetried()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new SlowServer(SlowFirstByte);
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1, reusedConnectionFirstByteTimeout: ShortFirstByte);
                using var client = CreateClient(pool);

                using var response = await client.Get(server.Url("/slow"))
                    .WithTimeout(TimeSpan.FromSeconds(10))
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("slow", response.GetBodyAsString());
                Assert.AreEqual(1, server.AcceptCount);
                Assert.AreEqual(1, server.SlowRequestCount);
            }, TimeSpan.FromSeconds(20));
        }

        private static UHttpClient CreateClient(TcpConnectionPool pool)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                AllowCleartext = true,
                Transport = new RawSocketTransport(pool),
                DisposeTransport = true
            });
        }

        /// <summary>
        /// Answers <c>/warm</c> at once and keeps the connection; answers <c>/slow</c> only after the
        /// configured time to first byte, then closes.
        /// </summary>
        private sealed class SlowServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _lock = new object();
            private readonly TimeSpan _slowFirstByte;
            private int _acceptCount;
            private int _slowRequestCount;
            private volatile bool _disposed;

            public SlowServer(TimeSpan slowFirstByte)
            {
                _slowFirstByte = slowFirstByte;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = AcceptLoopAsync();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public int AcceptCount => Volatile.Read(ref _acceptCount);
            public int SlowRequestCount => Volatile.Read(ref _slowRequestCount);
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

                    Interlocked.Increment(ref _acceptCount);
                    _ = Task.Run(() => HandleAsync(client));
                }
            }

            private async Task HandleAsync(TcpClient client)
            {
                try
                {
                    var stream = client.GetStream();
                    while (true)
                    {
                        var head = await ReadHeadAsync(stream);
                        if (head == null)
                            return;

                        if (head.StartsWith("GET /slow ", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref _slowRequestCount);
                            await Task.Delay(_slowFirstByte);
                            var slow = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\nslow");
                            await stream.WriteAsync(slow, 0, slow.Length);
                            client.Close();
                            return;
                        }

                        var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
                        await stream.WriteAsync(ok, 0, ok.Length);
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
