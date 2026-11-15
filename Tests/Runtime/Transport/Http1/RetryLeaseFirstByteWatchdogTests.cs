using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// The retry after a first-byte timeout on a reused connection runs on a newly connected socket
    /// that is not armed with the first-byte watchdog (#821): like any other fresh connection it waits
    /// for the request timeout, and it is not retried again. The #364/#384 stopgap that armed it is
    /// gone now that the abandoned read holds its descriptor (#384).
    /// </summary>
    [TestFixture]
    public class RetryLeaseFirstByteWatchdogTests
    {
        private static readonly TimeSpan ShortFirstByte = TimeSpan.FromMilliseconds(200);

        [Test]
        public void SilentRetryConnection_IdempotentGet_WaitsForTheRequestTimeoutNotTheFirstByteBound()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new SilentServer();
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1, reusedConnectionFirstByteTimeout: ShortFirstByte);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    AllowCleartext = true,
                    Transport = new RawSocketTransport(pool),
                    DisposeTransport = true
                });
                await InjectIdleConnectionAsync(pool, server.Port);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get($"http://127.0.0.1:{server.Port}/")
                        .WithTimeout(TimeSpan.FromMilliseconds(1500))
                        .SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.Timeout, ex.HttpError.Type,
                    "the silent retry connection waits for the request timeout, not the first-byte bound: " + ex);
                Assert.AreEqual(2, server.AcceptCount, "one injected connection, one retry, no third attempt");
            }, TimeSpan.FromSeconds(20));
        }

        private static async Task InjectIdleConnectionAsync(TcpConnectionPool pool, int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var connection = new PooledConnection(tcp.Client, new HoldingNetworkStream(tcp.Client), "127.0.0.1", port, false);

            var idleField = typeof(TcpConnectionPool).GetField(
                "_idleConnections",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
            idle.GetOrAdd($"127.0.0.1:{port}:", _ => new ConcurrentQueue<PooledConnection>())
                .Enqueue(connection);
        }

        /// <summary>Accepts connections, reads whatever arrives and never answers.</summary>
        private sealed class SilentServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly List<Socket> _accepted = new List<Socket>();
            private readonly object _lock = new object();
            private int _acceptCount;
            private volatile bool _disposed;

            public SilentServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = AcceptLoopAsync();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public int AcceptCount => Volatile.Read(ref _acceptCount);

            private async Task AcceptLoopAsync()
            {
                while (!_disposed)
                {
                    Socket socket;
                    try
                    {
                        socket = await _listener.AcceptSocketAsync();
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
                        _accepted.Add(socket);
                    Interlocked.Increment(ref _acceptCount);
                }
            }

            public void Dispose()
            {
                _disposed = true;
                _listener.Stop();
                lock (_lock)
                {
                    foreach (var socket in _accepted)
                        socket.Dispose();
                }
            }
        }
    }
}
