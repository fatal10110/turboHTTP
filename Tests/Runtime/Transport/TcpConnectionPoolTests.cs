using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport
{
    [TestFixture]
    public class TcpConnectionPoolTests
    {
        private sealed class PassiveServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _lock = new object();
            private int _acceptCount;

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public int AcceptCount => Volatile.Read(ref _acceptCount);

            public PassiveServer()
            {
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

                    Interlocked.Increment(ref _acceptCount);
                }
            }

            public void CloseAllClients(bool reset)
            {
                lock (_lock)
                {
                    foreach (var client in _clients)
                    {
                        try
                        {
                            if (reset)
                                client.Client.LingerState = new LingerOption(true, 0);
                            client.Close();
                        }
                        catch { }
                    }
                    _clients.Clear();
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                _listener.Stop();
                CloseAllClients(false);
            }
        }

        [Test]
        public async Task GetConnection_CreatesNewConnection()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool();
            using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            Assert.IsNotNull(lease.Connection);
            Assert.IsFalse(lease.Connection.IsReused);
        }

        [Test]
        public async Task ReturnConnection_ConnectionReused()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

            using var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            var conn1 = lease1.Connection;
            lease1.ReturnToPool();
            lease1.Dispose();

            using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            Assert.AreSame(conn1, lease2.Connection);
            Assert.IsTrue(lease2.Connection.IsReused);
        }

        [Test]
        public async Task ReturnConnection_StaleConnection_Disposed()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

            using var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            var conn1 = lease1.Connection;

            server.CloseAllClients(reset: true);
            lease1.ReturnToPool();
            lease1.Dispose();

            using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            Assert.AreNotSame(conn1, lease2.Connection);
            Assert.IsFalse(conn1.IsAlive);
        }

        [Test]
        public async Task Dispose_DrainsAllConnections()
        {
            using var server = new PassiveServer();
            var pool = new TcpConnectionPool();
            ConnectionLease lease = null;
            try
            {
                lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                var conn = lease.Connection;
                lease.ReturnToPool();
                lease.Dispose();
                pool.Dispose();

                Assert.IsFalse(conn.IsAlive);
            }
            finally
            {
                lease?.Dispose();
            }
        }

        [Test]
        public async Task MaxConnectionsPerHost_BlocksWhenAtLimit()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

            using var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            var task = pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);

            await Task.Delay(100);
            Assert.IsFalse(task.IsCompleted);

            lease1.Dispose();
            using var lease2 = await task;
            Assert.IsNotNull(lease2.Connection);
        }

        [Test]
        public async Task MaxConnectionsPerHost_WaitThenProceed()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool(maxConnectionsPerHost: 2);

            using var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);

            var task = pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            await Task.Delay(100);
            Assert.IsFalse(task.IsCompleted);

            lease1.Dispose();
            using var lease3 = await task;
            Assert.IsNotNull(lease3.Connection);
        }

        [Test]
        public async Task DisposedPool_ReturnConnection_DisposesConnection()
        {
            using var server = new PassiveServer();
            var pool = new TcpConnectionPool();

            using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            var conn = lease.Connection;
            pool.Dispose();

            lease.ReturnToPool();
            lease.Dispose();

            Assert.IsFalse(conn.IsAlive);
        }

        [Test]
        public async Task CaseInsensitiveHostKey_SharesPool()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

            using var lease1 = await pool.GetConnectionAsync("LOCALHOST", server.Port, false, CancellationToken.None);
            var conn = lease1.Connection;
            lease1.ReturnToPool();
            lease1.Dispose();

            using var lease2 = await pool.GetConnectionAsync("localhost", server.Port, false, CancellationToken.None);
            Assert.AreSame(conn, lease2.Connection);
        }

        [Test]
        public async Task PooledConnection_IsReused_FalseForNewConnection()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool();

            using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            Assert.IsFalse(lease.Connection.IsReused);
        }

        [Test]
        public async Task PooledConnection_IsReused_TrueAfterPoolDequeue()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

            using var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            lease1.ReturnToPool();
            lease1.Dispose();

            using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            Assert.IsTrue(lease2.Connection.IsReused);
        }

        [Test]
        public async Task ConnectionLease_Dispose_AlwaysReleasesSemaphore()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

            var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            lease.Dispose();

            using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            Assert.IsNotNull(lease2.Connection);
        }

        [Test]
        public async Task ConnectionLease_ReturnToPool_ThenDispose_DoesNotDisposeConnection()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

            using var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            var conn = lease1.Connection;
            lease1.ReturnToPool();
            lease1.Dispose();

            using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            Assert.AreSame(conn, lease2.Connection);
            Assert.IsTrue(lease2.Connection.IsAlive);
        }

        [Test]
        public async Task ConnectionLease_NoReturnToPool_ThenDispose_DisposesConnection()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool();

            var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            var conn = lease.Connection;
            lease.Dispose();

            Assert.IsFalse(conn.IsAlive);
        }

        [Test]
        public async Task ConnectionLease_ExceptionPath_SemaphoreReleased()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

            ConnectionLease lease = null;
            try
            {
                lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                throw new InvalidOperationException("simulate error");
            }
            catch (InvalidOperationException)
            {
                lease?.Dispose();
            }

            using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            Assert.IsNotNull(lease2.Connection);
        }

        [Test]
        public async Task ConnectionLease_DoubleDispose_Idempotent()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool();
            var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            lease.Dispose();
            Assert.DoesNotThrow(() => lease.Dispose());
        }

        [Test]
        public async Task PooledConnection_IsAlive_AfterDispose_ReturnsFalse()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool();
            using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            var conn = lease.Connection;
            conn.Dispose();
            Assert.IsFalse(conn.IsAlive);
        }

        [Test]
        public async Task ConnectionLease_Dispose_AfterPoolDispose_DoesNotThrow()
        {
            using var server = new PassiveServer();
            var pool = new TcpConnectionPool();
            var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            pool.Dispose();
            Assert.DoesNotThrow(() => lease.Dispose());
        }

        [Test]
        public async Task ConnectionLease_ConcurrentReturnAndDispose_NoRace()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
            var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);

            var t1 = Task.Run(() => lease.ReturnToPool());
            var t2 = Task.Run(() => lease.Dispose());
            await Task.WhenAll(t1, t2);

            using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            Assert.IsNotNull(lease2.Connection);
        }

        [Test]
        public void GetConnectionAsync_AfterPoolDispose_ThrowsObjectDisposedException()
        {
            using var server = new PassiveServer();
            var pool = new TcpConnectionPool();
            pool.Dispose();

            Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None));
        }

        [Test]
        public async Task EnqueueConnection_AfterPoolDispose_DisposesConnection()
        {
            using var server = new PassiveServer();
            var pool = new TcpConnectionPool();

            var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
            var conn = lease.Connection;

            pool.Dispose();
            pool.EnqueueConnection(conn);

            Assert.IsFalse(conn.IsAlive);
            lease.Dispose();
        }

        [Test]
        public async Task SemaphoreCapEviction_DrainsIdleConnections_BeforeRemoval()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

            // Create an idle connection for eviction key
            using var lease1 = await pool.GetConnectionAsync("localhost", server.Port, false, CancellationToken.None);
            var conn = lease1.Connection;
            lease1.ReturnToPool();
            lease1.Dispose();

            var semField = typeof(TcpConnectionPool).GetField("_semaphores", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var idleField = typeof(TcpConnectionPool).GetField("_idleConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var semaphores = (ConcurrentDictionary<string, SemaphoreSlim>)semField.GetValue(pool);
            var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);

            // Inflate semaphore count beyond cap with dummy entries that are not eligible for eviction
            for (int i = 0; i < 1000; i++)
            {
                var sem = new SemaphoreSlim(1, 1);
                sem.Wait(0); // currentCount = 0, not eligible for eviction
                semaphores[$"dummy-{i}"] = sem;
            }

            // Ensure eviction candidate has idle connection queued
            var evictKey = $"{conn.Host}:{conn.Port}:{(conn.IsSecure ? "s" : "")}";
            Assert.IsTrue(idle.ContainsKey(evictKey));

            // Trigger eviction with a different current key
            using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);

            Assert.IsFalse(conn.IsAlive);
        }

        [Test]
        public async Task SemaphoreCapEviction_NeverEvictsCurrentKey()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

            var semField = typeof(TcpConnectionPool).GetField("_semaphores", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var semaphores = (ConcurrentDictionary<string, SemaphoreSlim>)semField.GetValue(pool);

            for (int i = 0; i < 1000; i++)
            {
                var sem = new SemaphoreSlim(1, 1);
                sem.Wait(0);
                semaphores[$"dummy-{i}"] = sem;
            }

            using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);

            var currentKey = $"127.0.0.1:{server.Port}:";
            Assert.IsTrue(semaphores.ContainsKey(currentKey));
        }

        [Test]
        public async Task SemaphoreCapEviction_DoesNotDisposeSemaphores()
        {
            using var server = new PassiveServer();
            using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

            var semField = typeof(TcpConnectionPool).GetField("_semaphores", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var semaphores = (ConcurrentDictionary<string, SemaphoreSlim>)semField.GetValue(pool);

            var tracked = new SemaphoreSlim(1, 1);
            semaphores["tracked"] = tracked; // eligible for eviction

            for (int i = 0; i < 1000; i++)
            {
                var sem = new SemaphoreSlim(1, 1);
                sem.Wait(0);
                semaphores[$"dummy-{i}"] = sem;
            }

            using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);

            bool waited = tracked.Wait(0);
            if (waited)
                tracked.Release();
        }

        [Test]
        [Explicit("Requires local TLS test server and certificate")]
        public void PooledConnection_NegotiatedTlsVersion_SetAfterTlsHandshake()
        {
            Assert.Inconclusive("Requires TLS server setup.");
        }

        [Test]
        [Explicit("Requires local TLS test server and certificate")]
        public void TlsStreamWrapper_WrapAsync_ReturnsTlsResult_WithNegotiatedProtocol()
        {
            Assert.Inconclusive("Requires TLS server setup.");
        }

        [Test]
        [Explicit("Requires TLS 1.0/1.1 test server")]
        public void TlsStreamWrapper_TlsBelowMinimum_ThrowsAuthenticationException()
        {
            Assert.Inconclusive("Requires TLS server setup.");
        }
    }
}
