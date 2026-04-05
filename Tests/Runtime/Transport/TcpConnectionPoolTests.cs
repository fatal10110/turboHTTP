using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;
using UnityEngine.TestTools;

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

        private sealed class ForwardingStream : Stream
        {
            private readonly Stream _inner;

            public ForwardingStream(Stream inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;
            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count)
                => _inner.Read(buffer, offset, count);

            public override long Seek(long offset, SeekOrigin origin)
                => _inner.Seek(offset, origin);

            public override void SetLength(long value) => _inner.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count)
                => _inner.Write(buffer, offset, count);

            public override Task FlushAsync(CancellationToken cancellationToken)
                => _inner.FlushAsync(cancellationToken);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => _inner.ReadAsync(buffer, offset, count, cancellationToken);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => _inner.ReadAsync(buffer, cancellationToken);

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => _inner.WriteAsync(buffer, offset, count, cancellationToken);

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
                => _inner.WriteAsync(buffer, cancellationToken);

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();

                base.Dispose(disposing);
            }
        }

        private static async Task WaitForAcceptCountAsync(PassiveServer server, int expectedCount, int timeoutMs = 1000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (server.AcceptCount < expectedCount && Environment.TickCount64 < deadline)
            {
                await Task.Delay(10);
            }
        }

        private static string BuildTunnelPoolKey(PassiveServer server, string targetHost, int targetPort)
        {
            return "tunnel|" +
                TcpConnectionPool.BuildConnectionKey("127.0.0.1", server.Port, secure: false) +
                "|" +
                TcpConnectionPool.BuildConnectionKey(targetHost, targetPort, secure: true);
        }

        [Test]
        public void GetConnection_CreatesNewConnection()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool();
                using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.IsNotNull(lease.Connection);
                Assert.IsFalse(lease.Connection.IsReused);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ReturnConnection_ConnectionReused()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void GetConnection_WithPoolKeyOverride_ReuseIsScopedToTunnelKey()
        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 2);

                var tunnelKeyA = BuildTunnelPoolKey(server, "localhost", 443);
                var tunnelKeyB = BuildTunnelPoolKey(server, "example.test", 443);

                using (var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, tunnelKeyA, CancellationToken.None))
                {
                    var wrappedStream = new ForwardingStream(lease1.Connection.Stream);
                    lease1.Connection.UpdateTransportBinding(
                        wrappedStream,
                        "localhost",
                        443,
                        isSecure: true,
                        poolKey: tunnelKeyA,
                        tlsVersion: "1.3",
                        tlsProviderName: "TestTls",
                        negotiatedAlpnProtocol: "http/1.1");

                    lease1.ReturnToPool();
                }

                using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, tunnelKeyA, CancellationToken.None);
                await WaitForAcceptCountAsync(server, 1);
                Assert.AreEqual(1, server.AcceptCount);
                Assert.IsTrue(lease2.Connection.IsReused);
                Assert.IsTrue(lease2.Connection.IsSecure);
                Assert.AreEqual("localhost", lease2.Connection.Host);
                Assert.AreEqual(443, lease2.Connection.Port);
                Assert.AreEqual("1.3", lease2.Connection.TlsVersion);
                Assert.AreEqual("TestTls", lease2.Connection.TlsProviderName);
                Assert.AreEqual("http/1.1", lease2.Connection.NegotiatedAlpnProtocol);

                using var lease3 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, tunnelKeyB, CancellationToken.None);
                await WaitForAcceptCountAsync(server, 2);
                Assert.AreEqual(2, server.AcceptCount);
                Assert.AreNotSame(lease2.Connection, lease3.Connection);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void GetConnection_WithPoolKeyOverride_SemaphoreUsesPhysicalEndpoint()
        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                using var lease1 = await pool.GetConnectionAsync(
                    "127.0.0.1",
                    server.Port,
                    false,
                    BuildTunnelPoolKey(server, "first.test", 443),
                    CancellationToken.None);

                var pendingLeaseTask = pool.GetConnectionAsync(
                    "127.0.0.1",
                    server.Port,
                    false,
                    BuildTunnelPoolKey(server, "second.test", 443),
                    CancellationToken.None).AsTask();

                var completed = await Task.WhenAny(pendingLeaseTask, Task.Delay(100));
                Assert.IsNotSame(pendingLeaseTask, completed);

                lease1.Dispose();

                using var lease2 = await pendingLeaseTask;
                await WaitForAcceptCountAsync(server, 2);
                Assert.AreEqual(2, server.AcceptCount);
                Assert.IsFalse(lease2.Connection.IsReused);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ReturnConnection_StaleConnection_Disposed()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                using var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                var conn1 = lease1.Connection;

                server.CloseAllClients(reset: true);

                // Try to observe server-side reset first.
                bool observedRemoteClose = false;
                var probeByte = new byte[] { 0x2A };
                for (int i = 0; i < 200; i++)
                {
                    if (!conn1.IsAlive)
                    {
                        observedRemoteClose = true;
                        break;
                    }

                    try
                    {
                        conn1.Socket.Send(probeByte);
                    }
                    catch (SocketException)
                    {
                        observedRemoteClose = true;
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        observedRemoteClose = true;
                        break;
                    }

                    await Task.Delay(20);
                }

                // Some runtimes do not surface remote reset promptly. Force-close locally
                // so this test deterministically validates pool behavior.
                if (!observedRemoteClose && conn1.IsAlive)
                {
                    try { conn1.Socket.Shutdown(SocketShutdown.Both); } catch { }
                    try { conn1.Socket.Close(); } catch { }
                }

                lease1.ReturnToPool();
                lease1.Dispose();

                using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.AreNotSame(conn1, lease2.Connection);
                Assert.IsFalse(conn1.IsAlive);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Dispose_DrainsAllConnections()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void MaxConnectionsPerHost_BlocksWhenAtLimit()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void MaxConnectionsPerHost_WaitThenProceed()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DisposedPool_ReturnConnection_DisposesConnection()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                var pool = new TcpConnectionPool();

                using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                var conn = lease.Connection;
                pool.Dispose();

                lease.ReturnToPool();
                lease.Dispose();

                Assert.IsFalse(conn.IsAlive);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CaseInsensitiveHostKey_SharesPool()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                using var lease1 = await pool.GetConnectionAsync("LOCALHOST", server.Port, false, CancellationToken.None);
                var conn = lease1.Connection;
                lease1.ReturnToPool();
                lease1.Dispose();

                using var lease2 = await pool.GetConnectionAsync("localhost", server.Port, false, CancellationToken.None);
                Assert.AreSame(conn, lease2.Connection);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PooledConnection_IsReused_FalseForNewConnection()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool();

                using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.IsFalse(lease.Connection.IsReused);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PooledConnection_IsReused_TrueAfterPoolDequeue()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                using var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                lease1.ReturnToPool();
                lease1.Dispose();

                using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.IsTrue(lease2.Connection.IsReused);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConnectionLease_Dispose_AlwaysReleasesSemaphore()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                lease.Dispose();

                using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.IsNotNull(lease2.Connection);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConnectionLease_ReturnToPool_ThenDispose_DoesNotDisposeConnection()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConnectionLease_NoReturnToPool_ThenDispose_DisposesConnection()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool();

                var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                var conn = lease.Connection;
                lease.Dispose();

                Assert.IsFalse(conn.IsAlive);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConnectionLease_ExceptionPath_SemaphoreReleased()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConnectionLease_DoubleDispose_Idempotent()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool();
                var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                lease.Dispose();
                Assert.DoesNotThrow(() => lease.Dispose());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PooledConnection_IsAlive_AfterDispose_ReturnsFalse()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool();
                using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                var conn = lease.Connection;
                conn.Dispose();
                Assert.IsFalse(conn.IsAlive);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConnectionLease_Dispose_AfterPoolDispose_DoesNotThrow()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                var pool = new TcpConnectionPool();
                var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                pool.Dispose();
                Assert.DoesNotThrow(() => lease.Dispose());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConnectionLease_ConcurrentReturnAndDispose_NoRace()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);

                var t1 = Task.Run(() => lease.ReturnToPool());
                var t2 = Task.Run(() => lease.Dispose());
                await Task.WhenAll(t1, t2);

                using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.IsNotNull(lease2.Connection);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void GetConnectionAsync_AfterPoolDispose_ThrowsObjectDisposedException()
        {
            using var server = new PassiveServer();
            var pool = new TcpConnectionPool();
            pool.Dispose();

            AssertAsync.ThrowsAsync<ObjectDisposedException>(async () =>
                await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None));
        }

        [UnityTest]
        public IEnumerator Dispose_ConcurrentCalls_DoNotThrow()
        {
            var task = RunAsync();
            yield return new UnityEngine.WaitUntil(() => task.IsCompleted);
            if (task.IsFaulted)
            {
                throw task.Exception?.GetBaseException() ?? new Exception("Task failed without an exception.");
            }

            async Task RunAsync()
            {
                using var server = new PassiveServer();
                var pool = new TcpConnectionPool();
                using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);

                var t1 = Task.Run(() => pool.Dispose());
                var t2 = Task.Run(() => pool.Dispose());
                var t3 = Task.Run(() => pool.Dispose());

                await Task.WhenAll(t1, t2, t3);
            }
        }

        [Test]
        public void EnqueueConnection_AfterPoolDispose_DisposesConnection()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                var pool = new TcpConnectionPool();

                var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                var conn = lease.Connection;

                pool.Dispose();
                pool.EnqueueConnection(conn);

                Assert.IsFalse(conn.IsAlive);
                lease.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SemaphoreCapEviction_DrainsIdleConnections_BeforeRemoval()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SemaphoreCapEviction_NeverEvictsCurrentKey()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SemaphoreCapEviction_DoesNotDisposeSemaphores()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

#if TURBOHTTP_INTEGRATION_TESTS
        [Test]
        [Category("Integration")]
        public void PooledConnection_TlsVersion_SetAfterTlsHandshake()
        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var pool = new TcpConnectionPool(
                    maxConnectionsPerHost: 1,
                    tlsBackend: TlsBackend.SslStream,
                    dnsTimeout: TimeSpan.FromSeconds(10));

                ConnectionLease lease = null;
                try
                {
                    try
                    {
                        lease = await pool.GetConnectionAsync("www.google.com", 443, true, cts.Token);
                    }
                    catch (Exception ex) when (IsNetworkEnvironmentIssue(ex))
                    {
                        Assert.Ignore($"TLS integration endpoint unavailable in this environment: {ex.GetType().Name}: {ex.Message}");
                        return;
                    }

                    Assert.IsNotNull(lease.Connection);
                    Assert.IsTrue(lease.Connection.IsSecure);
                    Assert.IsNotNull(lease.Connection.TlsVersion);
                    Assert.IsNotNull(lease.Connection.TlsProviderName);
                    Assert.That(lease.Connection.TlsVersion, Is.EqualTo("1.2").Or.EqualTo("1.3"));
                }
                finally
                {
                    lease?.Dispose();
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Category("Integration")]
        public void ITlsProvider_WrapAsync_ReturnsTlsResult_WithNegotiatedProtocol()
        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                Socket socket = null;
                NetworkStream stream = null;
                try
                {
                    try
                    {
                        socket = ConnectWithTimeout("www.google.com", 443, 10000);
                        stream = new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (IsNetworkEnvironmentIssue(ex))
                    {
                        Assert.Ignore($"TLS integration endpoint unavailable in this environment: {ex.GetType().Name}: {ex.Message}");
                        return;
                    }

                    var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
                    var wrapTask = provider.WrapAsync(stream, "www.google.com", new[] { "h2", "http/1.1" }, cts.Token);
                    var completed = await Task.WhenAny(wrapTask, Task.Delay(TimeSpan.FromSeconds(20), CancellationToken.None));
                    if (completed != wrapTask)
                    {
                        Assert.Ignore("TLS handshake exceeded timeout in this environment.");
                        return;
                    }

                    var result = await wrapTask;
                    Assert.IsNotNull(result.SecureStream);
                    Assert.AreEqual("SslStream", result.ProviderName);
                    Assert.That(result.NegotiatedAlpn, Is.EqualTo("h2").Or.EqualTo("http/1.1").Or.Null);
                    Assert.That(result.TlsVersion, Is.EqualTo("1.2").Or.EqualTo("1.3"));
                }
                finally
                {
                    try { stream?.Dispose(); } catch { }
                    try { socket?.Dispose(); } catch { }
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Category("Integration")]
        public void ITlsProvider_TlsBelowMinimum_ThrowsAuthenticationException()
        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                Socket socket = null;
                NetworkStream stream = null;
                try
                {
                    try
                    {
                        // badssl endpoint constrained to legacy TLS 1.0; should fail since provider enforces >= TLS1.2.
                        socket = ConnectWithTimeout("tls-v1-0.badssl.com", 1010, 10000);
                        stream = new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (IsNetworkEnvironmentIssue(ex))
                    {
                        Assert.Ignore($"Legacy TLS test endpoint unavailable in this environment: {ex.GetType().Name}: {ex.Message}");
                        return;
                    }

                    var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
                    var exThrown = AssertAsync.ThrowsAsync<AuthenticationException>(async () =>
                    {
                        await provider.WrapAsync(stream, "tls-v1-0.badssl.com", new[] { "http/1.1" }, cts.Token);
                    });
                    Assert.IsNotNull(exThrown);
                }
                finally
                {
                    try { stream?.Dispose(); } catch { }
                    try { socket?.Dispose(); } catch { }
                }
            }).GetAwaiter().GetResult();
        }

        private static void RethrowIfFaulted(Task task)
        {
            if (!task.IsFaulted)
                return;

            throw task.Exception?.GetBaseException() ?? new Exception("Task failed without an exception.");
        }

        private static Socket ConnectWithTimeout(string host, int port, int timeoutMs)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var ar = socket.BeginConnect(host, port, null, null);
            try
            {
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                    throw new TimeoutException($"Connect to {host}:{port} timed out after {timeoutMs}ms.");

                socket.EndConnect(ar);
                return socket;
            }
            finally
            {
                ar.AsyncWaitHandle.Close();
            }
        }

        private static bool IsNetworkEnvironmentIssue(Exception ex)
        {
            if (ex is TimeoutException || ex is SocketException || ex is IOException || ex is OperationCanceledException)
                return true;

            if (ex is UHttpException httpEx)
            {
                return httpEx.HttpError.Type == UHttpErrorType.Timeout
                    || httpEx.HttpError.Type == UHttpErrorType.NetworkError
                    || httpEx.HttpError.Type == UHttpErrorType.Cancelled;
            }

            return false;
        }
#endif
    }
}
